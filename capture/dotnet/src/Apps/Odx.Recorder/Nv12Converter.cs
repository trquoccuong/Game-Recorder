using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Odx.Recorder;

/// <summary>
/// Crops captured BGRA frames to the video area and converts them to NV12 on the GPU (D3D11 video processor), then reads
/// them back. NV12 is 1.5 bytes per pixel instead of 4 and goes to the encoder as is: converting BGRA in ffmpeg cost
/// ~1/4 of x264's CPU and ~4/5 of a hardware encoder's (1080p30, plan "Timing model of odx-record").
/// Two staging textures are used in turn: a frame is read when the next one arrives, by then its copy is done and Map
/// does not wait for the GPU.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _bgra;
    private readonly ID3D11Texture2D _nv12;
    private readonly ID3D11VideoProcessorInputView _input;
    private readonly ID3D11VideoProcessorOutputView _output;
    private readonly ID3D11Texture2D[] _staging;
    private readonly int _cropX, _cropY;
    private int _next;

    /// <summary>Even video size (NV12 and yuv420p need it): an odd last row or column of the area is left out.</summary>
    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 3 / 2;

    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context, int cropX, int cropY, int width, int height)
    {
        _context = context;
        (_cropX, _cropY, Width, Height) = (cropX, cropY, width & ~1, height & ~1);
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)Width,
            InputHeight = (uint)Height,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.PlaybackNormal,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _bgra = device.CreateTexture2D(Texture(Format.B8G8R8A8_UNorm, ResourceUsage.Default, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _nv12 = device.CreateTexture2D(Texture(Format.NV12, ResourceUsage.Default, BindFlags.RenderTarget));
        _staging = [device.CreateTexture2D(Texture(Format.NV12, ResourceUsage.Staging, BindFlags.None)),
                    device.CreateTexture2D(Texture(Format.NV12, ResourceUsage.Staging, BindFlags.None))];
        _input = _videoDevice.CreateVideoProcessorInputView(_bgra, _enumerator, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        });
        _output = _videoDevice.CreateVideoProcessorOutputView(_nv12, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });
        // Full-range RGB in, BT.601 limited-range YCbCr out: what ffmpeg's BGRA -> yuv420p conversion produced before.
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor,
            new VideoProcessorColorSpace { YCbCr_Matrix = 0, Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235 });
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
    }

    private Texture2DDescription Texture(Format format, ResourceUsage usage, BindFlags bind) => new()
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = bind,
        CPUAccessFlags = usage == ResourceUsage.Staging ? CpuAccessFlags.Read : CpuAccessFlags.None,
    };

    /// <summary>Queues the conversion of <paramref name="frame"/>; returns the staging texture to <see cref="Read"/> later.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D frame)
    {
        _context.CopySubresourceRegion(_bgra, 0, 0, 0, 0, frame, 0,
            new Box(_cropX, _cropY, 0, _cropX + Width, _cropY + Height, 1));
        _videoContext.VideoProcessorBlt(_processor, _output, 0, 1,
            [new VideoProcessorStream { Enable = true, InputSurface = _input }]).CheckError();
        var staging = _staging[_next];
        _next ^= 1;
        _context.CopyResource(staging, _nv12);
        _context.Flush();
        return staging;
    }

    /// <summary>Copies the Y plane, then the interleaved UV plane (half height), into <paramref name="frame"/>.</summary>
    public void Read(ID3D11Texture2D staging, byte[] frame)
    {
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            // In a mapped NV12 texture the UV plane starts right after Height rows of the Y plane.
            for (int y = 0; y < Height * 3 / 2; y++)
                Marshal.Copy(mapped.DataPointer + (nint)(y * (long)mapped.RowPitch), frame, y * Width, Width);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
        foreach (var s in _staging) s.Dispose();
        _nv12.Dispose();
        _bgra.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
