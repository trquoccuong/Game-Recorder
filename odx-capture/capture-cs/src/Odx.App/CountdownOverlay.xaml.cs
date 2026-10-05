using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Odx.App;

/// <summary>
/// Click-through, never-focused badge over the game: the countdown and "END". Excluded from screen capture, so it never
/// shows in a recording, even of the whole monitor or while the capture already runs (multiplayer countdown).
/// </summary>
public partial class CountdownOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;  // Windows 10 2004+

    public CountdownOverlay() => InitializeComponent();

    public int Seconds
    {
        set => Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string Text
    {
        set
        {
            Number.Text = value;
            Number.FontSize = value.Length <= 2 ? 110 : 64;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongW(hwnd, GWL_EXSTYLE,
            GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
}
