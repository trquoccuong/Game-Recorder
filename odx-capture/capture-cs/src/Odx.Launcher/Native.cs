using System.Runtime.InteropServices;

namespace Odx.Launcher;

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread, SuppressExternalCodecs;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll")]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW cls);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DefWindowProcW(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG msg, IntPtr window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG msg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(ref MSG msg);

    [LibraryImport("user32.dll")]
    public static partial UIntPtr SetTimer(IntPtr window, UIntPtr id, uint ms, IntPtr callback);

    [LibraryImport("user32.dll")]
    public static partial IntPtr BeginPaint(IntPtr window, out PAINTSTRUCT ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr window, ref PAINTSTRUCT ps);

    [LibraryImport("user32.dll")]
    public static partial IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint action, uint param, void* value, uint winIni);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(IntPtr callback, IntPtr param);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? name);

    [LibraryImport("shlwapi.dll")]
    public static partial IntPtr SHCreateMemStream(byte* data, uint size);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreateBitmapFromStream(IntPtr stream, out IntPtr bitmap);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipGetImageWidth(IntPtr image, out uint width);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipGetImageHeight(IntPtr image, out uint height);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreateFromHDC(IntPtr dc, out IntPtr graphics);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDeleteGraphics(IntPtr graphics);
}
