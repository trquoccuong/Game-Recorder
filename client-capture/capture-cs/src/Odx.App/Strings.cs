using System.Globalization;
using Odx.Core;
using Odx.Session;

namespace Odx.App;

/// <summary>
/// Every UI text of odx-app in English and Vietnamese. Validator issue messages stay in English: they quote spec terms
/// and column names.
/// </summary>
public sealed class Strings
{
    public const string English = "en", Vietnamese = "vi";

    public sealed record LanguageOption(string Code, string Name)
    {
        public override string ToString() => Name;  // what screen readers announce
    }

    public static readonly IReadOnlyList<LanguageOption> Languages = [new(English, "English"), new(Vietnamese, "Tiếng Việt")];

    /// <summary>The Windows display language if it is Vietnamese, otherwise English.</summary>
    public static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Vietnamese ? Vietnamese : English;

    public static Strings For(string? code) => code == Vietnamese ? Vi : En;

    public required string Language { get; init; }
    public required string Source { get; init; }
    public required string GameProfile { get; init; }
    public required string Fps { get; init; }
    public required string OutputFolder { get; init; }
    public required string NtpServer { get; init; }
    public required string Hotkey { get; init; }
    public required string Refresh { get; init; }
    public required string Browse { get; init; }
    public required string GameTooltip { get; init; }
    public required string NtpTooltip { get; init; }
    public required string HotkeyTooltip { get; init; }

    public required string PrimaryMonitor { get; init; }
    public required Func<string, string> NotRunning { get; init; }
    public required string NoGameProfile { get; init; }
    public required Func<string, string, string> SkippedProfile { get; init; }

    public required string Start { get; init; }
    public required Func<string, string> Stop { get; init; }
    public required Func<string, string> Cancel { get; init; }
    public required string Stopping { get; init; }
    public required string TrayShow { get; init; }
    public required string TrayExit { get; init; }
    public required string TrayCancel { get; init; }
    public required string TrayStop { get; init; }

    public required string Ready { get; init; }
    public required Func<int, string> CountdownStatus { get; init; }
    public required string Cancelled { get; init; }
    public required string StartingCapture { get; init; }
    public required Func<TimeSpan, RecorderStatus?, string, string> RecordingStatus { get; init; }
    public required string StoppingStatus { get; init; }
    public required Func<string, string> CouldNotStart { get; init; }
    public required Func<string, string> HotkeyInUse { get; init; }
    public required string PressKeys { get; init; }
    public required Func<string, string> TrayTooltip { get; init; }

    public required Func<string, string> Saved { get; init; }
    public required Func<string, string> StoppedWithErrors { get; init; }
    public required Func<long, int, int, double, long, long, string> FramesSummary { get; init; }
    public required Func<int, string> InputEvents { get; init; }
    public required string NoProfileNoBuild { get; init; }
    public required Func<string, string> BuildFailed { get; init; }
    public required Func<int, int, string> ValidationPass { get; init; }
    public required Func<int, int, string> ValidationFail { get; init; }
    public required Func<string, string> UnboundKeys { get; init; }
    public required Func<string, double, double, string> ClockNtp { get; init; }
    public required string ClockSystem { get; init; }
    public required Func<double, string> ClockWarning { get; init; }

    public static readonly Strings En = new()
    {
        Language = "Language",
        Source = "Source",
        GameProfile = "Game profile",
        Fps = "FPS",
        OutputFolder = "Output folder",
        NtpServer = "NTP server",
        Hotkey = "Start/stop hotkey",
        Refresh = "Refresh",
        Browse = "Browse…",
        GameTooltip = "Builds frames.csv + session.json after recording and validates them",
        NtpTooltip = "Measures the clock against this server during recording; leave empty to use the Windows clock only",
        HotkeyTooltip = "Click, then press the new key combination",
        PrimaryMonitor = "Primary monitor",
        NotRunning = exe => $"{exe} (not running)",
        NoGameProfile = "None (keep raw capture only)",
        SkippedProfile = (file, error) => $"Skipped game profile {file}: {error}",
        Start = "Start recording",
        Stop = hotkey => $"Stop recording ({hotkey})",
        Cancel = hotkey => $"Cancel ({hotkey})",
        Stopping = "Stopping…",
        TrayShow = "Show",
        TrayExit = "Exit",
        TrayCancel = "Cancel",
        TrayStop = "Stop recording",
        Ready = "Ready",
        CountdownStatus = n => $"Recording starts in {n}…",
        Cancelled = "Cancelled",
        StartingCapture = "Starting capture…",
        RecordingStatus = (elapsed, c, dir) =>
            $"Recording {elapsed:hh\\:mm\\:ss}{(c is { } x ? $" · {x.Frames} frames, {x.Dropped} dropped" : "")}\n→ {dir}",
        StoppingStatus = "Stopping, then building and validating the session…",
        CouldNotStart = error => $"Could not start: {error}",
        HotkeyInUse = hotkey => $"Hotkey {hotkey} is already used by another application; choose another one.",
        PressKeys = "Press a key combination…",
        TrayTooltip = state => $"Client Capture: {state}",
        Saved = dir => $"Saved: {dir}",
        StoppedWithErrors = dir => $"Stopped with errors: {dir}",
        FramesSummary = (frames, w, h, fps, duplicates, dropped) =>
            $"{frames} frames ({w}x{h} @ {fps} fps), {duplicates} duplicated, {dropped} dropped",
        InputEvents = n => $"{n} input events",
        NoProfileNoBuild = "No game profile selected: session not built (run odx build later).",
        BuildFailed = error => $"Could not build the session: {error}",
        ValidationPass = (errors, warnings) => $"Validation PASS: {errors} errors, {warnings} warnings",
        ValidationFail = (errors, warnings) => $"Validation FAIL: {errors} errors, {warnings} warnings",
        UnboundKeys = keys => $"Keys not bound in the game profile (left out): {keys}",
        ClockNtp = (server, rtt, offset) =>
            $"Clock: NTP {server}, RTT {rtt:F0} ms; Windows clock off by {offset / 1000:+0.000;-0.000} s",
        ClockSystem = "Clock: Windows clock only (NTP not measured — check the NTP server)",
        ClockWarning = offset => $"Warning: the Windows clock is {Math.Abs(offset) / 1000:F1} s off; fix Windows time sync.",
    };

    public static readonly Strings Vi = new()
    {
        Language = "Ngôn ngữ",
        Source = "Nguồn quay",
        GameProfile = "Hồ sơ game",
        Fps = "FPS",
        OutputFolder = "Thư mục lưu",
        NtpServer = "Máy chủ NTP",
        Hotkey = "Phím tắt bắt đầu/dừng",
        Refresh = "Làm mới",
        Browse = "Chọn…",
        GameTooltip = "Sau khi ghi, tạo frames.csv + session.json và kiểm tra hợp lệ",
        NtpTooltip = "Đo đồng hồ với máy chủ này trong khi ghi; để trống thì chỉ dùng đồng hồ Windows",
        HotkeyTooltip = "Bấm vào đây rồi nhấn tổ hợp phím mới",
        PrimaryMonitor = "Màn hình chính",
        NotRunning = exe => $"{exe} (không chạy)",
        NoGameProfile = "Không có (chỉ giữ dữ liệu thô)",
        SkippedProfile = (file, error) => $"Bỏ qua hồ sơ game {file}: {error}",
        Start = "Bắt đầu ghi",
        Stop = hotkey => $"Dừng ghi ({hotkey})",
        Cancel = hotkey => $"Hủy ({hotkey})",
        Stopping = "Đang dừng…",
        TrayShow = "Hiện cửa sổ",
        TrayExit = "Thoát",
        TrayCancel = "Hủy",
        TrayStop = "Dừng ghi",
        Ready = "Sẵn sàng",
        CountdownStatus = n => $"Bắt đầu ghi sau {n}…",
        Cancelled = "Đã hủy",
        StartingCapture = "Đang khởi động ghi…",
        RecordingStatus = (elapsed, c, dir) =>
            $"Đang ghi {elapsed:hh\\:mm\\:ss}{(c is { } x ? $" · {x.Frames} khung hình, bỏ {x.Dropped}" : "")}\n→ {dir}",
        StoppingStatus = "Đang dừng, sau đó tạo và kiểm tra session…",
        CouldNotStart = error => $"Không thể bắt đầu: {error}",
        HotkeyInUse = hotkey => $"Phím tắt {hotkey} đang được ứng dụng khác dùng; hãy chọn phím khác.",
        PressKeys = "Nhấn tổ hợp phím…",
        TrayTooltip = state => $"Client Capture: {state}",
        Saved = dir => $"Đã lưu: {dir}",
        StoppedWithErrors = dir => $"Đã dừng nhưng có lỗi: {dir}",
        FramesSummary = (frames, w, h, fps, duplicates, dropped) =>
            $"{frames} khung hình ({w}x{h} @ {fps} fps), lặp {duplicates}, bỏ {dropped}",
        InputEvents = n => $"{n} sự kiện bàn phím/chuột",
        NoProfileNoBuild = "Chưa chọn hồ sơ game: chưa tạo session (chạy odx build sau).",
        BuildFailed = error => $"Không tạo được session: {error}",
        ValidationPass = (errors, warnings) => $"Kiểm tra ĐẠT: {errors} lỗi, {warnings} cảnh báo",
        ValidationFail = (errors, warnings) => $"Kiểm tra KHÔNG ĐẠT: {errors} lỗi, {warnings} cảnh báo",
        UnboundKeys = keys => $"Phím chưa gán trong hồ sơ game (đã bỏ qua): {keys}",
        ClockNtp = (server, rtt, offset) =>
            $"Đồng hồ: NTP {server}, RTT {rtt:F0} ms; đồng hồ Windows lệch {offset / 1000:+0.000;-0.000} giây",
        ClockSystem = "Đồng hồ: chỉ dùng đồng hồ Windows (chưa đo được NTP — kiểm tra máy chủ NTP)",
        ClockWarning = offset => $"Cảnh báo: đồng hồ Windows lệch {Math.Abs(offset) / 1000:F1} giây; hãy sửa đồng bộ giờ của Windows.",
    };

    /// <summary>Multi-line result of one recording in this language.</summary>
    public string Describe(RunResult r)
    {
        var lines = new List<string> { r.Ok ? Saved(r.SessionDir) : StoppedWithErrors(r.SessionDir) };
        if (r.Recording is { } s) lines.Add(FramesSummary(s.Frames, s.Width, s.Height, s.Fps, s.Duplicates, s.Dropped));
        lines.Add(InputEvents(r.InputEvents));
        lines.AddRange(r.ToolErrors);
        if (r.CaptureOk && r.Build is null) lines.Add(NoProfileNoBuild);
        if (r.Build is { Error: { } error }) lines.Add(BuildFailed(error));
        if (r.Build is { Report: { } report } build)
        {
            int errors = build.Issues.Count(i => i.Severity == Issue.ErrorSeverity), warnings = build.Issues.Count - errors;
            lines.Add(errors == 0 ? ValidationPass(errors, warnings) : ValidationFail(errors, warnings));
            lines.AddRange(build.Issues.Take(5).Select(i => $"  [{i.Severity}] {i.Rule}: {i.Message}"));
            if (report.UnboundKeys.Count > 0) lines.Add(UnboundKeys(string.Join(", ", report.UnboundKeys.Keys)));
            var c = report.Clock;
            if (c is { Source: "ntp", RttMs: double rtt, SystemOffsetMs: double offset })
            {
                lines.Add(ClockNtp(c.Server!, rtt, offset));
                if (Math.Abs(offset) > ClockWarningMs) lines.Add(ClockWarning(offset));
            }
            else
            {
                lines.Add(ClockSystem);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Beyond this the Windows clock is badly synced; NTP still fixes the session times, but other tools use it.</summary>
    public const double ClockWarningMs = 1000;
}
