using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Odx.App;

/// <summary>Click-through, never-focused badge over the game: the countdown before capture and "END" after it.</summary>
public partial class CountdownOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

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
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
}
