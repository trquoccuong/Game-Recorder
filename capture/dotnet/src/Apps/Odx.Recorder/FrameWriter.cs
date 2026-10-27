using System.Collections.Concurrent;
using Odx.Core;

namespace Odx.Recorder;

/// <summary>
/// Writes grid ticks to ffmpeg and video_frames.csv on its own thread, so the capture callback does not wait for the
/// encoder: while that callback is blocked, Windows.Graphics.Capture (2-buffer frame pool) drops frames and the video
/// jumps. Frames are pooled buffers; one goes back to the pool once its ticks are written, and <see cref="Rent"/> blocks
/// only when the encoder is a whole pool behind.
/// </summary>
internal sealed class FrameWriter
{
    // The frame on screen plus 3 queued: ~100 ms of slack at 30 fps (frames are 8 MB at 1080p).
    private const int Buffers = 4;

    private readonly FfmpegEncoder _encoder;
    private readonly VideoFrameLogWriter _log;
    private readonly BlockingCollection<byte[]> _free = new();
    private readonly BlockingCollection<(byte[] Frame, IReadOnlyList<GridTick> Ticks)> _queue = new();
    private readonly Thread _thread;
    private long _written;
    private volatile string? _error;

    public long Written => Interlocked.Read(ref _written);
    /// <summary>Why frames stopped being written (ffmpeg closed its input); later ticks are dropped.</summary>
    public string? Error => _error;

    public FrameWriter(FfmpegEncoder encoder, VideoFrameLogWriter log, int frameBytes)
    {
        _encoder = encoder;
        _log = log;
        for (int i = 0; i < Buffers; i++) _free.Add(new byte[frameBytes]);
        _thread = new Thread(Run) { IsBackground = true, Name = "frame writer" };
        _thread.Start();
    }

    public byte[] Rent() => _free.Take();

    /// <summary>Queues <paramref name="ticks"/>, all showing <paramref name="frame"/>, which returns to the pool after.</summary>
    public void Submit(byte[] frame, IReadOnlyList<GridTick> ticks) => _queue.Add((frame, ticks));

    /// <summary>Writes what is queued; <see cref="Submit"/> may not be called after.</summary>
    public void Complete()
    {
        _queue.CompleteAdding();
        _thread.Join();
    }

    private void Run()
    {
        foreach (var (frame, ticks) in _queue.GetConsumingEnumerable())
        {
            foreach (var t in ticks)
            {
                if (_error is not null) break;
                try
                {
                    _encoder.Write(frame);
                }
                catch (IOException e)
                {
                    _error = $"ffmpeg pipe closed: {e.Message}";
                    break;
                }
                _log.Write(t);
                Interlocked.Increment(ref _written);
            }
            _free.Add(frame);
        }
    }
}
