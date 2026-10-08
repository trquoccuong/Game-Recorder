using System.Windows;

namespace Odx.App;

/// <summary>The countdown and "END" in the middle of the screen (<see cref="Overlay"/>).</summary>
public partial class CountdownOverlay : Window
{
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
        Overlay.Apply(this);
    }
}
