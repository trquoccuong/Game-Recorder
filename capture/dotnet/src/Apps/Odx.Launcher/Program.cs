// Odx Capture.exe: shows the splash at once, starts bin\odx-app.exe (passing its arguments on) and closes when the
// app shows its first window, when the app exits, or after two minutes. Plain Win32 + GDI+ so it compiles with NativeAOT.
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Odx.Launcher.Native;

namespace Odx.Launcher;

internal static unsafe class Program
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);
    private static IntPtr _image;
    private static int _width, _height;
    private static Process? _app;
    private static readonly Stopwatch Elapsed = Stopwatch.StartNew();

    [STAThread]
    private static int Main(string[] args)
    {
        string app = Path.Combine(AppContext.BaseDirectory, "bin", "odx-app.exe");
        if (!File.Exists(app))
        {
            MessageBoxW(IntPtr.Zero, $"Missing {app}.\nUnzip the whole package and run Odx Capture.exe from it.",
                "Odx Capture", 0x10);
            return 1;
        }
        SetProcessDpiAwarenessContext((IntPtr)(-4));  // per-monitor v2: draw the image at its pixel size, like WPF's splash
        IntPtr window = ShowSplash();

        var psi = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _app = Process.Start(psi);

        SetTimer(window, (UIntPtr)1, 100, IntPtr.Zero);
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return 0;
    }

    private static IntPtr ShowSplash()
    {
        var input = new GdiplusStartupInput { GdiplusVersion = 1 };
        GdiplusStartup(out _, ref input, IntPtr.Zero);
        using (var png = typeof(Program).Assembly.GetManifestResourceStream("splash.png")!)
        {
            var bytes = new byte[png.Length];
            png.ReadExactly(bytes);
            fixed (byte* p = bytes)
            {
                IntPtr stream = SHCreateMemStream(p, (uint)bytes.Length);
                GdipCreateBitmapFromStream(stream, out _image);
                Marshal.Release(stream);
            }
        }
        GdipGetImageWidth(_image, out uint w);
        GdipGetImageHeight(_image, out uint h);
        (_width, _height) = ((int)w, (int)h);

        IntPtr instance = GetModuleHandleW(null);
        var cls = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = instance,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32514),  // IDC_APPSTARTING
        };
        fixed (char* name = "OdxLauncherSplash")
        {
            cls.lpszClassName = name;
            RegisterClassExW(ref cls);
        }
        RECT work;
        SystemParametersInfoW(0x0030, 0, &work, 0);  // SPI_GETWORKAREA of the primary monitor
        int x = work.Left + (work.Right - work.Left - _width) / 2, y = work.Top + (work.Bottom - work.Top - _height) / 2;
        IntPtr window = CreateWindowExW(0x00000080 | 0x00000008, "OdxLauncherSplash", "Odx Capture",  // TOOLWINDOW | TOPMOST
            0x80000000, x, y, _width, _height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);  // WS_POPUP
        ShowWindow(window, 5);
        UpdateWindow(window);
        return window;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x000F:  // WM_PAINT
                IntPtr dc = BeginPaint(window, out var ps);
                GdipCreateFromHDC(dc, out IntPtr graphics);
                GdipDrawImageRectI(graphics, _image, 0, 0, _width, _height);
                GdipDeleteGraphics(graphics);
                EndPaint(window, ref ps);
                return IntPtr.Zero;
            case 0x0113:  // WM_TIMER
                if (_app is null || _app.HasExited || AppHasWindow(_app.Id) || Elapsed.Elapsed > GiveUpAfter) DestroyWindow(window);
                return IntPtr.Zero;
            case 0x0002:  // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(window, msg, wParam, lParam);
    }

    private static bool _found;

    // The app's first window is WPF's splash, which shows the same image, then its main window.
    private static bool AppHasWindow(int pid)
    {
        _found = false;
        EnumWindows((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&HasWindowOf, (IntPtr)(&pid));
        return _found;
    }

    [UnmanagedCallersOnly]
    private static int HasWindowOf(IntPtr window, IntPtr pidPtr)
    {
        GetWindowThreadProcessId(window, out uint owner);
        if (owner == *(int*)pidPtr && IsWindowVisible(window))
        {
            _found = true;
            return 0;  // stop enumerating
        }
        return 1;
    }
}
