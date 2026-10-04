using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Odx.Core;
using Odx.Session;
using Forms = System.Windows.Forms;

namespace Odx.App;

public sealed record SourceItem(string Label, string? Process)
{
    public override string ToString() => Label;  // what screen readers announce
}

public sealed record GameItem(string Label, string? Path)
{
    public override string ToString() => Label;
}

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 1;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int CountdownSeconds = 3;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ToolStripMenuItem _trayStartStop = new(), _trayShow = new(), _trayExit = new();
    private Strings _t = Strings.En;
    /// <summary>Renders the status line; kept so a language switch re-renders it.</summary>
    private Func<Strings, string> _status = t => t.Ready;
    private IntPtr _hwnd;
    private RunState _state = RunState.Idle;
    private CaptureRun? _run;
    private DispatcherTimer? _countdown;
    private CountdownOverlay? _overlay;
    private int _countdownLeft;
    private DateTime _runStarted;
    private bool _closeWhenIdle;
    /// <summary>Windows that can be recorded, listed off the UI thread (~200 ms) once the window is up and on Refresh.</summary>
    private List<SourceItem> _windows = [];
    private bool _windowsListed;

    /// <summary>Idle → Countdown → Starting (odx record warming up) → Recording → Stopping (incl. build + validate) → Idle.</summary>
    private enum RunState { Idle, Countdown, Starting, Recording, Stopping }

    public MainWindow()
    {
        InitializeComponent();
        _t = Strings.For(_settings.Language ?? Strings.DefaultLanguage);
        LanguageBox.ItemsSource = Strings.Languages;
        LanguageBox.SelectedItem = Strings.Languages.First(l => l.Code == (_settings.Language ?? Strings.DefaultLanguage));
        NtpBox.Text = _settings.NtpServer;
        FpsBox.ItemsSource = new[] { 30, 60 };
        FpsBox.SelectedItem = _settings.Fps;
        OutputBox.Text = _settings.OutputRoot;
        HotkeyBox.Text = _settings.Hotkey.ToString();
        Loaded += async (_, _) => await RefreshWindowsAsync();
        _clock.Tick += (_, _) => ShowRecordingStatus();

        _trayStartStop.Click += (_, _) => Toggle();
        using (var icon = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/odx.ico")).Stream)
            _tray.Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize);
        _tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        _trayShow.Click += (_, _) => RestoreFromTray();
        _trayExit.Click += (_, _) => Close();
        _tray.ContextMenuStrip.Items.AddRange([_trayStartStop, _trayShow, _trayExit]);
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        LanguageLabel.Content = _t.Language;
        SourceLabel.Content = _t.Source;
        GameLabel.Content = _t.GameProfile;
        FpsLabel.Content = _t.Fps;
        OutputLabel.Content = _t.OutputFolder;
        NtpLabel.Content = _t.NtpServer;
        HotkeyLabel.Content = _t.Hotkey;
        RefreshButton.Content = _t.Refresh;
        BrowseButton.Content = _t.Browse;
        GameBox.ToolTip = _t.GameTooltip;
        NtpBox.ToolTip = _t.NtpTooltip;
        HotkeyBox.ToolTip = _t.HotkeyTooltip;
        _trayShow.Text = _t.TrayShow;
        _trayExit.Text = _t.TrayExit;
        // Rebuild the pickers in the new language, keeping what is selected.
        if (SourceBox.SelectedItem is SourceItem source) _settings.WindowProcess = source.Process;
        if (GameBox.SelectedItem is GameItem game) _settings.GameProfile = game.Path;
        LoadSources();
        LoadGames();
        SetState(_state);
        SetStatus(_status);
    }

    private void Language_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Also fires while the constructor fills the box; the constructor applies the language itself.
        if (!IsLoaded || LanguageBox.SelectedItem is not Strings.LanguageOption option) return;
        _settings.Language = option.Code;
        _t = Strings.For(option.Code);
        ApplyLanguage();
    }

    private void SetStatus(Func<Strings, string> status)
    {
        _status = status;
        StatusText.Text = status(_t);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        RegisterHotkey();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_state == RunState.Countdown) CancelCountdown();
        if (_run is not null)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            if (_state != RunState.Stopping) StopRecording();
            return;
        }
        SaveSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        _tray.Dispose();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void LoadSources()
    {
        var items = new List<SourceItem> { new(_t.PrimaryMonitor, null) };
        items.AddRange(_windows);
        if (_settings.WindowProcess is string saved && !items.Any(i => i.Process == saved))
            items.Add(new SourceItem(_windowsListed ? _t.NotRunning($"{saved}.exe") : $"{saved}.exe", saved));
        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.First(i => i.Process == _settings.WindowProcess);
    }

    /// <summary>Game profiles shipped in games/ next to the app, plus a previously chosen file elsewhere.</summary>
    private void LoadGames()
    {
        var items = new List<GameItem> { new(_t.NoGameProfile, null) };
        string dir = Path.Combine(AppContext.BaseDirectory, "games");
        if (Directory.Exists(dir))
            foreach (string file in Directory.GetFiles(dir, "*.json").Order())
            {
                try
                {
                    items.Add(new GameItem(GameProfile.Load(file).GameTitle, file));
                }
                catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException)
                {
                    string name = Path.GetFileName(file), error = e.Message;
                    SetStatus(t => t.SkippedProfile(name, error));
                }
            }
        if (_settings.GameProfile is string saved && File.Exists(saved) && !items.Any(i => i.Path == saved))
            items.Add(new GameItem(Path.GetFileNameWithoutExtension(saved), saved));
        GameBox.ItemsSource = items;
        GameBox.SelectedItem = items.FirstOrDefault(i => i.Path == _settings.GameProfile) ?? items[0];
    }

    private void SaveSettings()
    {
        _settings.WindowProcess = (SourceBox.SelectedItem as SourceItem)?.Process;
        _settings.GameProfile = (GameBox.SelectedItem as GameItem)?.Path;
        _settings.Fps = FpsBox.SelectedItem as int? ?? 60;
        _settings.OutputRoot = OutputBox.Text.Trim();
        _settings.NtpServer = NtpBox.Text.Trim();
        _settings.Save();
    }

    private void Toggle()
    {
        switch (_state)
        {
            case RunState.Idle: BeginCountdown(); break;
            case RunState.Countdown: CancelCountdown(); break;
            case RunState.Starting or RunState.Recording: StopRecording(); break;
        }
    }

    private void SetState(RunState state, Func<Strings, string>? status = null)
    {
        _state = state;
        if (status is not null) SetStatus(status);
        if (state != RunState.Recording) _clock.Stop();
        bool idle = state == RunState.Idle;
        StartStopButton.Content = state switch
        {
            RunState.Idle => _t.Start,
            RunState.Countdown => _t.Cancel(_settings.Hotkey.ToString()),
            RunState.Stopping => _t.Stopping,
            _ => _t.Stop(_settings.Hotkey.ToString()),
        };
        StartStopButton.IsEnabled = _trayStartStop.Enabled = state != RunState.Stopping;
        _trayStartStop.Text = state switch
        {
            RunState.Idle => _t.Start,
            RunState.Countdown => _t.TrayCancel,
            RunState.Stopping => _t.Stopping,
            _ => _t.TrayStop,
        };
        SourceBox.IsEnabled = GameBox.IsEnabled = FpsBox.IsEnabled = OutputBox.IsEnabled = HotkeyBox.IsEnabled = idle;
        NtpBox.IsEnabled = LanguageBox.IsEnabled = idle;
        RefreshButton.IsEnabled = BrowseButton.IsEnabled = idle;
        _tray.Text = idle ? "Odx Capture" : _t.TrayTooltip(_trayStartStop.Text);
    }

    private void BeginCountdown()
    {
        _countdownLeft = CountdownSeconds;
        _overlay = new CountdownOverlay();
        _overlay.Show();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (--_countdownLeft > 0)
            {
                ShowCountdown();
                return;
            }
            // The overlay is closed before odx record starts, so it never appears in the video.
            EndCountdown();
            StartRecording();
        };
        _countdown.Start();
        SetState(RunState.Countdown);
        ShowCountdown();
    }

    private void ShowCountdown()
    {
        _overlay!.Seconds = _countdownLeft;
        int left = _countdownLeft;
        SetStatus(t => t.CountdownStatus(left));
    }

    private void CancelCountdown()
    {
        EndCountdown();
        SetState(RunState.Idle, t => t.Cancelled);
    }

    private void EndCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
        _overlay?.Close();
        _overlay = null;
    }

    private void StartRecording()
    {
        SaveSettings();
        CaptureRun run;
        try
        {
            run = CaptureRun.Start(_settings.ToCaptureOptions());
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            SetState(RunState.Idle, t => t.CouldNotStart(error));
            return;
        }
        _run = run;
        SetState(RunState.Starting, t => t.StartingCapture);
        run.Started.ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_run != run || _state != RunState.Starting) return;
            _runStarted = DateTime.Now;
            SetState(RunState.Recording);
            _clock.Start();
            ShowRecordingStatus();
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void StopRecording()
    {
        SetState(RunState.Stopping, t => t.StoppingStatus);
        _run!.Stop();
    }

    private void OnRunCompleted(RunResult result)
    {
        _run = null;
        SetState(RunState.Idle, t => t.Describe(result));
        if (_closeWhenIdle) Close();
    }

    private void ShowRecordingStatus()
    {
        if (_run is null || _state != RunState.Recording) return;
        var elapsed = DateTime.Now - _runStarted;
        var status = _run.LastStatus;
        string dir = _run.SessionDir;
        SetStatus(t => t.RecordingStatus(elapsed, status, dir));
        _tray.Text = _t.TrayTooltip($"{elapsed:hh\\:mm\\:ss}");
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) => Toggle();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWindowsAsync();

    private async Task RefreshWindowsAsync()
    {
        int self = Environment.ProcessId;
        _windows = await Task.Run(() =>
        {
            var windows = new List<SourceItem>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle))
                        continue;
                    windows.Add(new SourceItem($"{p.MainWindowTitle} ({p.ProcessName}.exe)", p.ProcessName));
                }
            }
            return windows;
        });
        _windowsListed = true;
        if (SourceBox.SelectedItem is SourceItem selected) _settings.WindowProcess = selected.Process;
        LoadSources();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = OutputBox.Text };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }

    // While the hotkey box has focus the global hotkey is released, so pressing the current combination re-assigns it
    // instead of toggling a recording.
    private void Hotkey_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        HotkeyBox.Text = _t.PressKeys;
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = _settings.Hotkey.ToString();
        RegisterHotkey();
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;
        _settings.Hotkey = new Hotkey((HotkeyModifiers)Keyboard.Modifiers, KeyInterop.VirtualKeyFromKey(key));
        _settings.Save();
        Keyboard.ClearFocus();
        StartStopButton.Focus();
    }

    private void RegisterHotkey()
    {
        var hk = _settings.Hotkey;
        if (!RegisterHotKey(_hwnd, HotkeyId, (uint)hk.Modifiers | MOD_NOREPEAT, (uint)hk.VirtualKey))
            SetStatus(t => t.HotkeyInUse(hk.ToString()));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
