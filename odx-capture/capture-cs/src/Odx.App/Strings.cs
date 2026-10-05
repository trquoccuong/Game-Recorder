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
    public required string Hotkey { get; init; }
    public required string Refresh { get; init; }
    public required string Browse { get; init; }
    public required string GameTooltip { get; init; }
    public required string HotkeyTooltip { get; init; }
    public required string PlayerId { get; init; }
    public required string Room { get; init; }
    public required string RoomTooltip { get; init; }
    public required string JoinRoom { get; init; }
    public required string CreateRoom { get; init; }
    public required string LeaveRoom { get; init; }
    public required string CloseRoom { get; init; }

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
    public required Func<bool, string> ReadyWithEncoder { get; init; }
    public required Func<int, string> CountdownStatus { get; init; }
    public required string Cancelled { get; init; }
    public required string StartingCapture { get; init; }
    public required Func<TimeSpan, RecorderStatus?, string, string> RecordingStatus { get; init; }
    public required string StoppingStatus { get; init; }
    public required Func<string, string> CouldNotStart { get; init; }
    public required Func<string, string> HotkeyInUse { get; init; }
    public required string PressKeys { get; init; }
    public required Func<string, string> HostHint { get; init; }
    public required string InvalidRoomCode { get; init; }
    public required string JoiningRoom { get; init; }
    public required Func<string, bool, string, string> RoomStatus { get; init; }
    public required Func<string, string> StartRound { get; init; }
    public required Func<string, string> EndRound { get; init; }
    public required Func<string, string> ReadyButton { get; init; }
    public required Func<string, string> UnreadyButton { get; init; }
    public required string InRound { get; init; }
    public required string LeftRoom { get; init; }
    public required string RoomClosed { get; init; }
    public required Func<string, string> ReadyHint { get; init; }
    public required Func<string, string> ReadyOn { get; init; }
    public required Func<string, string> RoundToggleFailed { get; init; }
    public required Func<string, string> TrayTooltip { get; init; }
    public required string EndOverlay { get; init; }
    public required string NotifySavedPass { get; init; }
    public required string NotifySavedRaw { get; init; }
    public required string NotifyFailed { get; init; }
    public required string OpenFolderHint { get; init; }

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
    public required Func<string, string> ConfigInvalid { get; init; }
    public required Func<double, string> ClockWarning { get; init; }

    public static readonly Strings En = new()
    {
        Language = "Language",
        Source = "Source",
        GameProfile = "Game profile",
        Fps = "FPS",
        OutputFolder = "Output folder",
        Hotkey = "Start/stop hotkey",
        Refresh = "Refresh",
        Browse = "Browse…",
        GameTooltip = "Builds frames.csv + session.json after recording and validates them",
        HotkeyTooltip = "Click, then press the new key combination",
        PlayerId = "Player ID",
        Room = "Room",
        RoomTooltip = "Invite code from the room's host (6 characters)",
        JoinRoom = "Join room",
        CreateRoom = "Create room",
        LeaveRoom = "Leave room",
        CloseRoom = "Close room",
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
        ReadyWithEncoder = gpu => gpu ? "Ready · video encoder: NVENC (GPU)" : "Ready · video encoder: x264 (CPU; NVENC not available here)",
        CountdownStatus = n => $"Recording starts in {n}…",
        Cancelled = "Cancelled",
        StartingCapture = "Starting capture…",
        RecordingStatus = (elapsed, c, dir) =>
            $"Recording {elapsed:hh\\:mm\\:ss}{(c is { } x ? $" · {x.Frames} frames, {x.Dropped} dropped" : "")}\n→ {dir}",
        StoppingStatus = "Stopping, then building and validating the session…",
        CouldNotStart = error => $"Could not start: {error}",
        HotkeyInUse = hotkey => $"Hotkey {hotkey} is already used by another application; choose another one.",
        PressKeys = "Press a key combination…",
        HostHint = hotkey => $"Host: press {hotkey} to start or end the round for everyone.",
        InvalidRoomCode = "Type the room's 6-character invite code (and a player ID).",
        JoiningRoom = "Joining the room…",
        RoomStatus = (code, host, members) => $"Room {code}{(host ? " · you are the host: share this invite code" : "")}\nPlayers: {members}",
        StartRound = hotkey => $"Start round ({hotkey})",
        EndRound = hotkey => $"End round ({hotkey})",
        ReadyButton = hotkey => $"I'm ready ({hotkey})",
        UnreadyButton = hotkey => $"Not ready ({hotkey})",
        InRound = "Round in progress…",
        LeftRoom = "Left the room",
        RoomClosed = "The host closed the room",
        ReadyHint = hotkey => $"Press {hotkey} when you are ready; the round starts when everyone is.",
        ReadyOn = hotkey => $"READY: waiting for the others ({hotkey} to cancel).",
        RoundToggleFailed = error => $"Could not start/end the round: {error}",
        TrayTooltip = state => $"Odx Capture: {state}",
        EndOverlay = "END",
        NotifySavedPass = "Recording saved · validation PASS",
        NotifySavedRaw = "Recording saved (raw capture)",
        NotifyFailed = "Recording stopped with errors",
        OpenFolderHint = "Click to open the folder.",
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
        ClockSystem = "Clock: Windows clock only (NTP not measured — check time_server in odx-capture.json)",
        ConfigInvalid = error => $"Settings file not valid, the Windows clock is used: {error}",
        ClockWarning = offset => $"Warning: the Windows clock is {Math.Abs(offset) / 1000:F1} s off; fix Windows time sync.",
    };

    public static readonly Strings Vi = new()
    {
        Language = "Ngôn ngữ",
        Source = "Nguồn quay",
        GameProfile = "Hồ sơ game",
        Fps = "FPS",
        OutputFolder = "Thư mục lưu",
        Hotkey = "Phím tắt bắt đầu/dừng",
        Refresh = "Làm mới",
        Browse = "Chọn…",
        GameTooltip = "Sau khi ghi, tạo frames.csv + session.json và kiểm tra hợp lệ",
        HotkeyTooltip = "Bấm vào đây rồi nhấn tổ hợp phím mới",
        PlayerId = "Mã người chơi",
        Room = "Room",
        RoomTooltip = "Mã mời do host của room gửi (6 ký tự)",
        JoinRoom = "Vào room",
        CreateRoom = "Tạo room",
        LeaveRoom = "Rời room",
        CloseRoom = "Đóng room",
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
        ReadyWithEncoder = gpu => gpu ? "Sẵn sàng · mã hóa video: NVENC (GPU)" : "Sẵn sàng · mã hóa video: x264 (CPU; máy này không dùng được NVENC)",
        CountdownStatus = n => $"Bắt đầu ghi sau {n}…",
        Cancelled = "Đã hủy",
        StartingCapture = "Đang khởi động ghi…",
        RecordingStatus = (elapsed, c, dir) =>
            $"Đang ghi {elapsed:hh\\:mm\\:ss}{(c is { } x ? $" · {x.Frames} khung hình, bỏ {x.Dropped}" : "")}\n→ {dir}",
        StoppingStatus = "Đang dừng, sau đó tạo và kiểm tra session…",
        CouldNotStart = error => $"Không thể bắt đầu: {error}",
        HotkeyInUse = hotkey => $"Phím tắt {hotkey} đang được ứng dụng khác dùng; hãy chọn phím khác.",
        PressKeys = "Nhấn tổ hợp phím…",
        HostHint = hotkey => $"Host: nhấn {hotkey} để bắt đầu hoặc kết thúc round cho mọi người.",
        InvalidRoomCode = "Nhập mã mời 6 ký tự của room (và mã người chơi).",
        JoiningRoom = "Đang vào room…",
        RoomStatus = (code, host, members) => $"Room {code}{(host ? " · bạn là host: gửi mã mời này cho mọi người" : "")}\nNgười chơi: {members}",
        StartRound = hotkey => $"Bắt đầu round ({hotkey})",
        EndRound = hotkey => $"Kết thúc round ({hotkey})",
        ReadyButton = hotkey => $"Tôi sẵn sàng ({hotkey})",
        UnreadyButton = hotkey => $"Hủy sẵn sàng ({hotkey})",
        InRound = "Đang trong round…",
        LeftRoom = "Đã rời room",
        RoomClosed = "Host đã đóng room",
        ReadyHint = hotkey => $"Nhấn {hotkey} khi bạn sẵn sàng; round bắt đầu khi mọi người đều sẵn sàng.",
        ReadyOn = hotkey => $"ĐÃ SẴN SÀNG: đang chờ người khác (nhấn {hotkey} để hủy).",
        RoundToggleFailed = error => $"Không bắt đầu/kết thúc được round: {error}",
        TrayTooltip = state => $"Odx Capture: {state}",
        EndOverlay = "KẾT THÚC",
        NotifySavedPass = "Đã lưu bản ghi · Kiểm tra ĐẠT",
        NotifySavedRaw = "Đã lưu bản ghi (dữ liệu thô)",
        NotifyFailed = "Bản ghi có lỗi",
        OpenFolderHint = "Bấm để mở thư mục.",
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
        ClockSystem = "Đồng hồ: chỉ dùng đồng hồ Windows (chưa đo được NTP — kiểm tra time_server trong odx-capture.json)",
        ConfigInvalid = error => $"File cấu hình không hợp lệ, dùng đồng hồ Windows: {error}",
        ClockWarning = offset => $"Cảnh báo: đồng hồ Windows lệch {Math.Abs(offset) / 1000:F1} giây; hãy sửa đồng bộ giờ của Windows.",
    };

    /// <summary>Title and text of the Windows notification shown when a recording is finished.</summary>
    public (string Title, string Body) Notification(RunResult r) =>
        (!r.Ok ? NotifyFailed : r.Build is null ? NotifySavedRaw : NotifySavedPass, $"{r.SessionDir}\n{OpenFolderHint}");

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
