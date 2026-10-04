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

public sealed record SourceItem(string Label, string? Process);

public sealed record GameItem(string Label, string? Path);

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 1;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int CountdownSeconds = 3;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Forms.NotifyIcon _tray = new();
    private readonly Forms.ToolStripMenuItem _trayStartStop = new();
    private IntPtr _hwnd;
    private RunState _state = RunState.Idle;
    private CaptureRun? _run;
    private DispatcherTimer? _countdown;
    private CountdownOverlay? _overlay;
    private int _countdownLeft;
    private DateTime _runStarted;
    private bool _closeWhenIdle;

    /// <summary>Idle → Countdown → Starting (odx record warming up) → Recording → Stopping (incl. build + validate) → Idle.</summary>
    private enum RunState { Idle, Countdown, Starting, Recording, Stopping }

    public MainWindow()
    {
        InitializeComponent();
        FpsBox.ItemsSource = new[] { 30, 60 };
        FpsBox.SelectedItem = _settings.Fps;
        OutputBox.Text = _settings.OutputRoot;
        HotkeyBox.Text = _settings.Hotkey.ToString();
        LoadSources();
        LoadGames();
        _clock.Tick += (_, _) => ShowRecordingStatus();

        _trayStartStop.Click += (_, _) => Toggle();
        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        _tray.ContextMenuStrip.Items.Add(_trayStartStop);
        _tray.ContextMenuStrip.Items.Add("Show", null, (_, _) => RestoreFromTray());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Close());
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
        SetState(RunState.Idle);
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
        var items = new List<SourceItem> { new("Primary monitor", null) };
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    continue;
                items.Add(new SourceItem($"{p.MainWindowTitle} ({p.ProcessName}.exe)", p.ProcessName));
            }
        }
        if (_settings.WindowProcess is string saved && !items.Any(i => i.Process == saved))
            items.Add(new SourceItem($"{saved}.exe (not running)", saved));
        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.First(i => i.Process == _settings.WindowProcess);
    }

    /// <summary>Game profiles shipped in games/ next to the app, plus a previously chosen file elsewhere.</summary>
    private void LoadGames()
    {
        var items = new List<GameItem> { new("None (keep raw capture only)", null) };
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
                    StatusText.Text = $"Skipped game profile {Path.GetFileName(file)}: {e.Message}";
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

    private void SetState(RunState state, string? status = null)
    {
        _state = state;
        if (status is not null) StatusText.Text = status;
        if (state != RunState.Recording) _clock.Stop();
        bool idle = state == RunState.Idle;
        StartStopButton.Content = state switch
        {
            RunState.Idle => "Start recording",
            RunState.Countdown => $"Cancel ({_settings.Hotkey})",
            RunState.Stopping => "Stopping…",
            _ => $"Stop recording ({_settings.Hotkey})",
        };
        StartStopButton.IsEnabled = _trayStartStop.Enabled = state != RunState.Stopping;
        _trayStartStop.Text = state switch
        {
            RunState.Idle => "Start recording",
            RunState.Countdown => "Cancel",
            RunState.Stopping => "Stopping…",
            _ => "Stop recording",
        };
        SourceBox.IsEnabled = GameBox.IsEnabled = FpsBox.IsEnabled = OutputBox.IsEnabled = HotkeyBox.IsEnabled = idle;
        RefreshButton.IsEnabled = BrowseButton.IsEnabled = idle;
        _tray.Text = idle ? "Client Capture" : $"Client Capture: {state.ToString().ToLowerInvariant()}";
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
        StatusText.Text = $"Recording starts in {_countdownLeft}…";
    }

    private void CancelCountdown()
    {
        EndCountdown();
        SetState(RunState.Idle, "Cancelled");
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
            SetState(RunState.Idle, $"Could not start: {ex.Message}");
            return;
        }
        _run = run;
        SetState(RunState.Starting, "Starting capture…");
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
        SetState(RunState.Stopping, "Stopping, then building and validating the session…");
        _run!.Stop();
    }

    private void OnRunCompleted(RunResult result)
    {
        _run = null;
        SetState(RunState.Idle, $"{(result.Ok ? "Saved" : "Stopped with errors")}: {result.SessionDir}\n{result.Summary}");
        if (_closeWhenIdle) Close();
    }

    private void ShowRecordingStatus()
    {
        if (_run is null || _state != RunState.Recording) return;
        var elapsed = DateTime.Now - _runStarted;
        string counters = _run.LastStatus is { } s ? $" · {s.Frames} frames, {s.Dropped} dropped" : "";
        StatusText.Text = $"Recording {elapsed:hh\\:mm\\:ss}{counters}\n→ {_run.SessionDir}";
        _tray.Text = $"Client Capture: recording {elapsed:hh\\:mm\\:ss}";
    }

    private void StartStop_Click(object sender, RoutedEventArgs e) => Toggle();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _settings.WindowProcess = (SourceBox.SelectedItem as SourceItem)?.Process;
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
        HotkeyBox.Text = "Press a key combination…";
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
            StatusText.Text = $"Hotkey {hk} is already used by another application; choose another one.";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
