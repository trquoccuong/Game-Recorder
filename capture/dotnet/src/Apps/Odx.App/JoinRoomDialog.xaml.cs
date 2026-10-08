using System.Windows;

namespace Odx.App;

/// <summary>Asks for a room's invite code; OK is enabled once 6 characters are typed.</summary>
public partial class JoinRoomDialog : Window
{
    private const int CodeLength = 6;

    public JoinRoomDialog(Strings t)
    {
        InitializeComponent();
        Title = t.JoinRoom;
        Prompt.Text = t.EnterInviteCode;
        OkButton.Content = t.JoinRoom;
        CancelButton.Content = t.CancelDialog;
    }

    public string Code => CodeBox.Text.Trim();

    private void Code_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        OkButton.IsEnabled = Code.Length == CodeLength;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
