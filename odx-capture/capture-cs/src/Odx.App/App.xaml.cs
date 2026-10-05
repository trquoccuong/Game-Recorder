using System.Windows;

namespace Odx.App;

/// <summary>
/// Single instance: a second start (e.g. the launcher clicked again while the app sits in the tray) only brings the
/// running window back, so two apps never record at the same time.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\OdxCapture.odx-app", ShowEventName = @"Local\OdxCapture.odx-app.show";
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) show.Set();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showRequests.WaitOne()) Dispatcher.Invoke(window.RestoreFromTray);
        }) { IsBackground = true }.Start();
    }
}
