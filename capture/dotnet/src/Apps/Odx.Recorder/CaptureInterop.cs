using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Odx.Recorder;

/// <summary>Glue between Win32/D3D11 (Vortice) and the WinRT Windows.Graphics.Capture API.</summary>
internal static class CaptureInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    /// <summary>Without this, Win32 reports DPI-scaled sizes to this (unaware) process; capture sizes are physical.</summary>
    public static void UsePhysicalPixels() => SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    public static IntPtr PrimaryMonitor() => MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);

    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    public static (int Width, int Height) MonitorSize(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) throw new InvalidOperationException("GetMonitorInfo failed");
        return (info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
    }

    public static GraphicsCaptureItem ItemForWindow(IntPtr hwnd) => CreateItem(i =>
    {
        var iid = GraphicsCaptureItemIid;
        return i.CreateForWindow(hwnd, ref iid);
    });

    public static GraphicsCaptureItem ItemForPrimaryMonitor() => CreateItem(i =>
    {
        var iid = GraphicsCaptureItemIid;
        return i.CreateForMonitor(PrimaryMonitor(), ref iid);
    });

    private static GraphicsCaptureItem CreateItem(Func<IGraphicsCaptureItemInterop, IntPtr> create)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        IntPtr ptr = create(interop);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>Wraps a D3D11 device as the WinRT IDirect3DDevice the frame pool needs.</summary>
    public static IDirect3DDevice WinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr raw));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    /// <summary>The D3D11 texture behind a captured frame's surface. Caller disposes it.</summary>
    public static ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        var access = ((IWinRTObject)surface).NativeObject.AsInterface<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        return new ID3D11Texture2D(access.GetInterface(ref iid));
    }
}
