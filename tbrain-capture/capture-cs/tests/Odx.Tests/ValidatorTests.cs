using System.Diagnostics;
using Odx.Core;
using Odx.Session;
using Xunit;

namespace Odx.Tests;

/// <summary>Built with video.mp4 when ffmpeg is available; validated with or without video checks accordingly.</summary>
public sealed class ValidSession : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"odx-validate-{Guid.NewGuid():N}");
    public string Dir { get; }

    public ValidSession() => Dir = SessionFixtures.MakeValidSession(Path.Combine(Root, "valid"), video: SessionFixtures.HasFfmpeg);

    public string Copy() => SessionFixtures.CopySession(Dir, Path.Combine(Root, Guid.NewGuid().ToString("N")));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class ValidatorTests(ValidSession valid) : IClassFixture<ValidSession>
{
    public static TheoryData<string> Rules => new(SessionFixtures.Mutations.Keys.Order());

    private static readonly bool Video = SessionFixtures.HasFfmpeg;

    [Fact]
    public void Valid_session_has_no_issues() => Assert.Empty(SessionValidator.ValidateSession(valid.Dir, Video));

    [SkippableTheory]
    [MemberData(nameof(Rules))]
    public void Each_violation_is_caught(string rule)
    {
        Skip.If(rule.StartsWith("video.", StringComparison.Ordinal) && !Video, "ffmpeg/ffprobe not on PATH");
        string session = valid.Copy();
        SessionFixtures.Mutations[rule](session);
        var fired = SessionValidator.ValidateSession(session, Video).Where(i => i.Severity == "error").Select(i => i.Rule).ToHashSet();
        Assert.True(fired.Contains(rule), $"expected {rule}, got {string.Join(", ", fired.Order())}");
    }

    [Fact]
    public void Every_error_rule_has_a_mutator()
    {
        var errorRules = Odx.Session.Rules.All.Where(r => r.Severity == Issue.ErrorSeverity).Select(r => r.Id);
        Assert.Empty(errorRules.Except(SessionFixtures.Mutations.Keys));
    }

    [Fact]
    public void Cli_exit_codes()
    {
        string videoFlag = Video ? "--json" : "--no-video";
        Assert.Equal(0, RunCli(valid.Dir, videoFlag));
        string broken = valid.Copy();
        SessionFixtures.Mutations["c2w.det"](broken);
        Assert.Equal(1, RunCli(broken, "--json", videoFlag));
    }

    [Fact]
    public void Session_without_camera_data_passes_camera_checks()
    {
        string session = SessionFixtures.MakeValidSession(Path.Combine(valid.Root, "no-camera"), video: false);
        SessionFixtures.EditRows(session, rows =>
        {
            foreach (var r in rows)
                foreach (var c in Frames.C2wColumns.Append("camera_model").Concat(Frames.IntrinsicColumns))
                    r[c] = Frames.Null;
        });
        Assert.Empty(SessionValidator.ValidateSession(session, video: false));
    }

    private static int RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo(ExternalTools.Odx())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("validate");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
