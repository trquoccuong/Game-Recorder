using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Odx.Core;

namespace Odx.Recorder;

/// <summary>Raw PCM that the video's ffmpeg reads as its audio input, from a named pipe.</summary>
internal sealed record AudioInput(string SampleFormat, int SampleRate, int Channels, string Pipe);

/// <summary>
/// Streams what one process tree plays (WASAPI process loopback: the game only, not voice chat, browsers or system
/// sounds, on whatever device it plays), or else the whole default playback device, as PCM into a named pipe that the video's ffmpeg
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
    private readonly AutoResetEvent _packetReady = new(false);
    private readonly List<(byte[] Data, double QpcMs)> _early = [];
    private readonly object _gate = new();
    private readonly int _bytesPerFrame;
    private volatile bool _stopping;
    private double? _originMs;
    private long _framesWritten;

    public AudioInput Input { get; }
    /// <summary>The game process recorded; null when the whole default playback device is.</summary>
    public int? ProcessId { get; }
    /// <summary>The playback device recorded; null when only the game process is.</summary>
    public string? Device { get; }
    public double? OriginMs => _originMs;
    /// <summary>Why audio stopped being written (ffmpeg closed the pipe).</summary>
    public string? Error { get; private set; }

    /// <param name="processId">The game's process; processes it starts are included.</param>
    /// <exception cref="NotSupportedException">Windows is older than build 20348, which added process loopback.</exception>
    public AudioLoopback(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new NotSupportedException("recording only the game's audio needs Windows 11 (or Windows 10 build 20348+)");
        ProcessId = processId;
        _client = ProcessLoopback.Activate(processId);
        // A process loopback client has no mix format: Windows converts the game's audio to the one asked for.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _bytesPerFrame = format.BlockAlign;
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            1_000_000, 0, format, Guid.Empty);
        _client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;
        (_pipe, _connected, Input) = OpenPipe("f32le", format);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio loopback" };
    }

    /// <summary>Everything the default playback device plays (voice chat and other apps included); polled every 10 ms.</summary>
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
        (_pipe, _connected, Input) = OpenPipe(sampleFormat, format);
        _thread = new Thread(Run) { IsBackground = true, Name = "audio loopback" };
    }

    private static (NamedPipeServerStream, Task, AudioInput) OpenPipe(string sampleFormat, WaveFormat format)
    {
        string name = "odx-audio-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        return (pipe, pipe.WaitForConnectionAsync(), new AudioInput(sampleFormat, format.SampleRate, format.Channels, @"\\.\pipe\" + name));
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
            _packetReady.WaitOne(10);
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
        _packetReady.Dispose();
    }
}

/// <summary>
/// Activates an audio client on one process tree's audio (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK). NAudio has the
/// COM interfaces but keeps the activation parameters internal.
/// </summary>
internal static class ProcessLoopback
{
    private const string VirtualDevice = @"VAD\Process_Loopback";  // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    // AUDIOCLIENT_ACTIVATION_PARAMS: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK (1), PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE (0).
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    // PROPVARIANT holding a VT_BLOB, x64 layout (the package is win-x64 only).
    [StructLayout(LayoutKind.Explicit)]
    private struct BlobPropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public int Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    public static AudioClient Activate(int processId)
    {
        int paramsSize = Marshal.SizeOf<ActivationParams>();
        IntPtr activationParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(new ActivationParams { ActivationType = 1, TargetProcessId = (uint)processId }, activationParams, false);
            Marshal.StructureToPtr(new BlobPropVariant { Vt = 65 /* VT_BLOB */, Size = paramsSize, Data = activationParams }, variant, false);
            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, IidAudioClient, variant, handler, out _);
            return new AudioClient(handler.Done.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(activationParams);
        }
    }

    // Must be agile: Windows calls ActivateCompleted from one of its own threads.
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Done => _done.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            operation.GetActivateResult(out int hr, out object client);
            if (hr < 0) _done.SetException(Marshal.GetExceptionForHR(hr)!);
            else _done.SetResult((IAudioClient)client);
        }
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }
}
