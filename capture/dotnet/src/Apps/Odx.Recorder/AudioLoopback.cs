using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Odx.Core;

namespace Odx.Recorder;

/// <summary>Raw PCM that the video's ffmpeg reads as its audio input, from a named pipe.</summary>
internal sealed record AudioInput(string SampleFormat, int SampleRate, int Channels, string Pipe);

/// <summary>
/// Streams what the default playback device plays (WASAPI loopback) as PCM into a named pipe that the video's ffmpeg
/// muxes into video.mp4, with sample 0 at <see cref="SetOrigin"/> on the QPC clock (the first video grid tick).
/// Packets are placed by their QPC position. WASAPI delivers nothing while nothing plays, so silence is written up to
/// <see cref="LagMs"/> behind now: the audio keeps pace with the video and ffmpeg never waits for it. The device clock's
/// drift against QPC is absorbed by padding or trimming once it exceeds <see cref="ToleranceMs"/>.
/// </summary>
internal sealed class AudioLoopback : IDisposable
{
    private const double ToleranceMs = 20;
    // Longer than a packet takes to arrive (10 ms polling + the device period), so real sound is not overwritten.
    private const double LagMs = 100;

    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _connected;
    private readonly List<byte[]> _pending = [];
    private readonly Thread _thread;
    private readonly List<(byte[] Data, double QpcMs)> _early = [];
    private readonly object _gate = new();
    private readonly int _bytesPerFrame;
    private volatile bool _stopping;
    private double? _originMs;
    private long _framesWritten;

    public AudioInput Input { get; }
    public string Device { get; }
    public double? OriginMs => _originMs;
    /// <summary>Why audio stopped being written (ffmpeg closed the pipe).</summary>
    public string? Error { get; private set; }

    /// <exception cref="NotSupportedException">The device's mix format is neither 32-bit float nor 16-bit PCM.</exception>
    public AudioLoopback()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Device = device.FriendlyName;
        _client = device.AudioClient;
        var format = _client.MixFormat;
        string sampleFormat = format switch
        {
            { BitsPerSample: 32 } when IsFloat(format) => "f32le",
            { BitsPerSample: 16 } when !IsFloat(format) => "s16le",
            _ => throw new NotSupportedException($"audio mix format {format} is not supported"),
        };
        _bytesPerFrame = format.BlockAlign;
        _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 1_000_000, 0, format, Guid.Empty);
        _capture = _client.AudioCaptureClient;

        string name = "odx-audio-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _connected = _pipe.WaitForConnectionAsync();
        Input = new AudioInput(sampleFormat, format.SampleRate, format.Channels, @"\\.\pipe\" + name);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio loopback" };
    }

    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");  // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible { SubFormat: var s } && s == SubtypeIeeeFloat;

    public void Start()
    {
        _client.Start();
        _thread.Start();
    }

    /// <summary>QPC time (ms) of sample 0; packets captured before it are held until it is known.</summary>
    public void SetOrigin(double qpcMs)
    {
        lock (_gate)
        {
            if (_originMs is not null) return;
            _originMs = qpcMs;
            foreach (var (data, at) in _early) Place(data, at);
            _early.Clear();
        }
    }

    /// <summary>Pads to <paramref name="stopQpcMs"/> and closes the pipe, so ffmpeg sees the audio end.</summary>
    public void Finish(double stopQpcMs)
    {
        _stopping = true;
        _thread.Join();
        _client.Stop();
        lock (_gate)
        {
            SetOrigin(stopQpcMs);
            WriteSilence(FrameAt(stopQpcMs) - _framesWritten);
            if (!_connected.Wait(TimeSpan.FromSeconds(10))) Error ??= "ffmpeg did not open the audio pipe";
            else Flush();
        }
        _pipe.Dispose();
    }

    private void Run()
    {
        while (!_stopping)
        {
            Thread.Sleep(10);
            while (_capture.GetNextPacketSize() > 0)
            {
                // qpcPosition is in 100 ns units (IAudioCaptureClient::GetBuffer).
                IntPtr buffer = _capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpc);
                var data = new byte[frames * _bytesPerFrame];
                if ((flags & AudioClientBufferFlags.Silent) == 0)
                    System.Runtime.InteropServices.Marshal.Copy(buffer, data, 0, data.Length);
                _capture.ReleaseBuffer(frames);
                double at = qpc / 10_000.0;
                lock (_gate)
                {
                    if (_originMs is null) _early.Add((data, at));
                    else Place(data, at);
                }
            }
            lock (_gate)
            {
                if (_originMs is not null) WriteSilence(FrameAt(QpcClock.NowMs() - LagMs) - _framesWritten);
            }
        }
    }

    private long FrameAt(double qpcMs) => (long)Math.Round((qpcMs - _originMs!.Value) * Input.SampleRate / 1000);

    private void Place(byte[] data, double qpcMs)
    {
        long start = FrameAt(qpcMs), tolerance = (long)(ToleranceMs * Input.SampleRate / 1000);
        int skip = 0;
        if (start > _framesWritten + tolerance) WriteSilence(start - _framesWritten);
        else if (start < _framesWritten - tolerance) skip = (int)Math.Min(_framesWritten - start, data.Length / _bytesPerFrame);
        Write(data, skip * _bytesPerFrame, data.Length - skip * _bytesPerFrame);
        _framesWritten += data.Length / _bytesPerFrame - skip;
    }

    private void WriteSilence(long frames)
    {
        if (frames <= 0) return;
        var zeros = new byte[Math.Min(frames, Input.SampleRate) * _bytesPerFrame];
        for (long left = frames; left > 0; left -= zeros.Length / _bytesPerFrame)
            Write(zeros, 0, (int)Math.Min(left * _bytesPerFrame, zeros.Length));
        _framesWritten += frames;
    }

    /// <summary>Held in memory until ffmpeg has opened the pipe (it starts with the first video frame).</summary>
    private void Write(byte[] data, int offset, int count)
    {
        if (Error is not null || count == 0) return;
        if (!_connected.IsCompleted)
        {
            _pending.Add(data.AsSpan(offset, count).ToArray());
            return;
        }
        Flush();
        Send(data, offset, count);
    }

    private void Flush()
    {
        foreach (var chunk in _pending) Send(chunk, 0, chunk.Length);
        _pending.Clear();
    }

    private void Send(byte[] data, int offset, int count)
    {
        if (Error is not null) return;
        try
        {
            _pipe.Write(data, offset, count);
        }
        catch (IOException e)
        {
            Error = $"audio pipe closed: {e.Message}";
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _pipe.Dispose();
    }
}
