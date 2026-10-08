namespace Odx.Capture;

public sealed record Issue(string Rule, string Severity, string Message)
{
    public const string ErrorSeverity = "error", WarningSeverity = "warning";

    public static Issue Error(string rule, string message) => new(rule, ErrorSeverity, message);
    public static Issue Warning(string rule, string message) => new(rule, WarningSeverity, message);
}

/// <summary>
/// Numeric limits of the checks. The spec fixes none of them; they are listed under "Decisions pending with the client" in
/// the README, so a confirmed value changes here only.
/// </summary>
public sealed record ValidationTolerances
{
    public static readonly ValidationTolerances Default = new();

    public double DurationUnitsSeconds { get; init; } = 1;
    public double DurationTimestampsMs { get; init; } = 1000;
    public double FrameCountFrames { get; init; } = 2;
    /// <summary>A frame interval is irregular when it deviates from 1/fps by more than max(min, fraction × 1/fps).</summary>
    public double FrameSpacingMinMs { get; init; } = 1;
    public double FrameSpacingFraction { get; init; } = 0.25;
    public double Rotation { get; init; } = 1e-3;
    public double LevelRoll { get; init; } = 0.05;
    public double PrincipalPointPx { get; init; } = 4;
    public double FxFyRelative { get; init; } = 1e-3;
    public double VideoDurationSeconds { get; init; } = 1;
}
