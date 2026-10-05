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

public sealed record GameItem(string Label, string? Path, string? WindowProcess = null)
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
    private readonly CaptureConfig _config;
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
    private string? _lastSessionDir;
    /// <summary>Windows that can be recorded, listed off the UI thread (~200 ms) once the window is up and on Refresh.</summary>
    private List<SourceItem> _windows = [];
    private bool _windowsListed;
    private CancellationTokenSource? _agentStop;
    private AgentClient? _agent;
    private string _agentLog = "";

    /// <summary>
    /// Idle → Countdown → Starting (odx record warming up) → Recording → Stopping (incl. build + validate) → Idle.
    /// With an orchestrator: Idle → Agent (the orchestrator starts and stops recordings) → Idle, or Stopping if recording.
    /// </summary>
    private enum RunState { Idle, Countdown, Starting, Recording, Stopping, Agent }

    public MainWindow()
    {
        InitializeComponent();
        _t = Strings.For(_settings.Language ?? Strings.DefaultLanguage);
        string? configError = null;
        try
        {
            _config = CaptureConfig.Load();
        }
        catch (InvalidDataException e)
        {
            _config = new CaptureConfig((string?)null);
            configError = e.Message;
        }
        LanguageBox.ItemsSource = Strings.Languages;
        LanguageBox.SelectedItem = Strings.Languages.First(l => l.Code == (_settings.Language ?? Strings.DefaultLanguage));
        FpsBox.ItemsSource = new[] { 30, 60 };
        FpsBox.SelectedItem = _settings.Fps;
        OutputBox.Text = _settings.OutputRoot;
        HotkeyBox.Text = _settings.Hotkey.ToString();
        OrchestratorBox.Text = _settings.Orchestrator;
        PlayerBox.Text = _settings.PlayerId;
        HostBox.IsChecked = _settings.IsHost;
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
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastSessionDir is not null && Directory.Exists(_lastSessionDir)) Process.Start("explorer.exe", _lastSessionDir);
        };
        _tray.Visible = true;
        ApplyLanguage();
        if (configError is not null) SetStatus(t => t.ConfigInvalid(configError));
    }

    private void ApplyLanguage()
    {
        LanguageLabel.Content = _t.Language;
        SourceLabel.Content = _t.Source;
        GameLabel.Content = _t.GameProfile;
        FpsLabel.Content = _t.Fps;
        OutputLabel.Content = _t.OutputFolder;
        HotkeyLabel.Content = _t.Hotkey;
        OrchestratorLabel.Content = _t.Orchestrator;
        PlayerLabel.Content = _t.PlayerId;
        OrchestratorBox.ToolTip = _t.OrchestratorTooltip;
        HostBox.Content = _t.Host;
        HostBox.ToolTip = _t.HostTooltip;
        RefreshButton.Content = _t.Refresh;
        BrowseButton.Content = _t.Browse;
        GameBox.ToolTip = _t.GameTooltip;
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
        if (_state == RunState.Agent) Disconnect();
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
            if (!AgentMode) Toggle();
            else if (_state == RunState.Agent && _settings.IsHost) ToggleRound();
            else if (_state == RunState.Agent) ToggleReady();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Also called when the app is started a second time (App: single instance).</summary>
    public void RestoreFromTray()
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
                    var profile = GameProfile.Load(file);
                    items.Add(new GameItem(profile.GameTitle, file, profile.WindowProcess));
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
        _settings.Orchestrator = OrchestratorBox.Text.Trim();
        _settings.PlayerId = PlayerBox.Text.Trim();
        _settings.IsHost = HostBox.IsChecked == true;
        _settings.Save();
    }

    private void Toggle()
    {
        switch (_state)
        {
            case RunState.Idle when AgentMode: Connect(); break;
            case RunState.Idle: BeginCountdown(); break;
            case RunState.Agent: Disconnect(); break;
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
            RunState.Idle => AgentMode ? _t.Connect : _t.Start,
            RunState.Agent => _t.Disconnect,
            RunState.Countdown => _t.Cancel(_settings.Hotkey.ToString()),
            RunState.Stopping => _t.Stopping,
            _ => _t.Stop(_settings.Hotkey.ToString()),
        };
        StartStopButton.IsEnabled = _trayStartStop.Enabled = state != RunState.Stopping;
        _trayStartStop.Text = state switch
        {
            RunState.Idle => AgentMode ? _t.Connect : _t.Start,
            RunState.Agent => _t.Disconnect,
            RunState.Countdown => _t.TrayCancel,
            RunState.Stopping => _t.Stopping,
            _ => _t.TrayStop,
        };
        SourceBox.IsEnabled = GameBox.IsEnabled = FpsBox.IsEnabled = OutputBox.IsEnabled = HotkeyBox.IsEnabled = idle;
        LanguageBox.IsEnabled = OrchestratorBox.IsEnabled = PlayerBox.IsEnabled = HostBox.IsEnabled = idle;
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
            run = CaptureRun.Start(_settings.ToCaptureOptions(_config.TimeServer));
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
        // Shown only once both capture tools have exited, so the badge never ends up in the video.
        run.CaptureStopped.ContinueWith(_ => Dispatcher.Invoke(ShowEndBadge), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void ShowEndBadge()
    {
        var badge = new CountdownOverlay { Text = _t.EndOverlay };
        badge.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            badge.Close();
        };
        timer.Start();
    }

    private void StopRecording()
    {
        SetState(RunState.Stopping, t => t.StoppingStatus);
        _run!.Stop();
    }

    private void OnRunCompleted(RunResult result)
    {
        _run = null;
        _lastSessionDir = result.SessionDir;
        SetState(RunState.Idle, t => t.Describe(result));
        var (title, body) = _t.Notification(result);
        _tray.ShowBalloonTip(10_000, title, body, result.Ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        if (_closeWhenIdle) Close();
    }

    private void ShowRecordingStatus()
    {
        if (_run is null || _state is not (RunState.Recording or RunState.Agent)) return;
        var elapsed = DateTime.Now - _runStarted;
        var status = _run.LastStatus;
        string dir = _run.SessionDir;
        SetStatus(t => t.RecordingStatus(elapsed, status, dir));
        _tray.Text = _t.TrayTooltip($"{elapsed:hh\\:mm\\:ss}");
    }

    private bool AgentMode => OrchestratorBox.Text.Trim().Length > 0;

    private void Orchestrator_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsLoaded && _state == RunState.Idle) SetState(RunState.Idle);
    }

    private void Connect()
    {
        SaveSettings();
        if (!Uri.TryCreate(_settings.Orchestrator, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            _settings.PlayerId.Length == 0)
        {
            SetStatus(t => t.InvalidOrchestrator);
            return;
        }
        _agentStop = new CancellationTokenSource();
        var agent = new AgentClient(new AgentOptions(uri, _settings.PlayerId, Environment.UserName, _settings.OutputRoot,
            _settings.WindowProcess, _settings.IsHost), StartAgentCapture);
        agent.Countdown += (ms, cancelled) => Dispatcher.BeginInvoke(() => ShowAgentCountdown(ms, cancelled));
        agent.Log += line => Dispatcher.BeginInvoke(() =>
        {
            _agentLog = $"{DateTime.Now:HH:mm:ss} {line}";
            if (_run is null && _state == RunState.Agent) ShowAgentStatus();
        });
        _agent = agent;
        _agentLog = "";
        SetState(RunState.Agent);
        ShowAgentStatus();
        _ = agent.RunUntilStoppedAsync(_agentStop.Token);
    }

    private void Disconnect()
    {
        _agentStop?.Cancel();
        _agentStop = null;
        _agent = null;
        if (_run is null) SetState(RunState.Idle, t => t.Disconnected);
        else StopRecording();
    }

    private void ShowAgentStatus()
    {
        string url = _settings.Orchestrator, log = _agentLog, hotkey = _settings.Hotkey.ToString();
        bool host = _settings.IsHost, ready = _agent?.IsReady == true;
        SetStatus(t => t.AgentStatus(url, log) + "\n" +
                       (host ? t.HostHint(hotkey) : ready ? t.ReadyOn(hotkey) : t.ReadyHint(hotkey)));
    }

    private async void ToggleReady()
    {
        if (_agent is null || _agentStop is null) return;
        try
        {
            await _agent.SetReadyAsync(!_agent.IsReady, _agentStop.Token);
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
        }
        if (_run is null) ShowAgentStatus();
    }

    private async void ToggleRound()
    {
        if (_agent is null || _agentStop is null) return;
        try
        {
            await _agent.ToggleRoundAsync(CountdownSeconds * 1000, _agentStop.Token);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            string error = e.Message;
            SetStatus(t => t.RoundToggleFailed(error));
        }
    }

    /// <summary>The same 3-2-1 as a solo recording, shown while the capture already runs (the overlay is excluded from it).</summary>
    private void ShowAgentCountdown(int countdownMs, CancellationToken cancelled)
    {
        int left = (int)Math.Ceiling(countdownMs / 1000.0);
        var overlay = new CountdownOverlay { Seconds = left };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(countdownMs / (double)left) };
        bool closed = false;
        void End()
        {
            if (closed) return;
            closed = true;
            timer.Stop();
            overlay.Close();
        }
        timer.Tick += (_, _) =>
        {
            if (--left > 0) overlay.Seconds = left;
            else End();
        };
        cancelled.Register(() => Dispatcher.BeginInvoke(End));
        overlay.Show();
        timer.Start();
    }

    /// <summary>AgentClient calls this off the UI thread when the orchestrator starts a round.</summary>
    private ICaptureRun StartAgentCapture(CaptureOptions options)
    {
        var run = CaptureRun.Start(options);
        Dispatcher.Invoke(() =>
        {
            _run = run;
            _runStarted = DateTime.Now;
            _clock.Start();
            ShowRecordingStatus();
        });
        run.CaptureStopped.ContinueWith(_ => Dispatcher.Invoke(ShowEndBadge), TaskContinuationOptions.OnlyOnRanToCompletion);
        run.Completion.ContinueWith(t => Dispatcher.Invoke(() => OnAgentRunCompleted(t.Result)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
        return run;
    }

    private void OnAgentRunCompleted(RunResult result)
    {
        _run = null;
        _clock.Stop();
        _lastSessionDir = result.SessionDir;
        var (title, body) = _t.Notification(result);
        _tray.ShowBalloonTip(10_000, title, body, result.Ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        if (_state == RunState.Stopping) SetState(RunState.Idle, t => t.Disconnected);
        else ShowAgentStatus();
        if (_closeWhenIdle) Close();
    }

    /// <summary>Picking a game whose window is open switches the source from the monitor to that window.</summary>
    private void Game_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _state != RunState.Idle || GameBox.SelectedItem is not GameItem { WindowProcess: { } process } ||
            SourceBox.SelectedItem is not SourceItem { Process: null } || !_windows.Any(w => w.Process == process))
            return;
        _settings.WindowProcess = process;
        LoadSources();
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
