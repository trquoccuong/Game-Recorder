using System.Diagnostics;
using Odx.Core;
using Odx.Validate;
using Xunit;

namespace Odx.Core.Tests;

/// <summary>Needs ffmpeg and ffprobe on PATH (video checks).</summary>
public sealed class ValidSession : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"odx-validate-{Guid.NewGuid():N}");
    public string Dir { get; }

    public ValidSession() => Dir = SessionFixtures.MakeValidSession(Path.Combine(Root, "valid"));

    public string Copy() => SessionFixtures.CopySession(Dir, Path.Combine(Root, Guid.NewGuid().ToString("N")));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class ValidatorTests(ValidSession valid) : IClassFixture<ValidSession>
{
    public static TheoryData<string> Rules => new(SessionFixtures.Mutations.Keys.Order());

    [Fact]
    public void Valid_session_has_no_issues() => Assert.Empty(SessionValidator.ValidateSession(valid.Dir));

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_violation_is_caught(string rule)
    {
        string session = valid.Copy();
        SessionFixtures.Mutations[rule](session);
        var fired = SessionValidator.ValidateSession(session).Where(i => i.Severity == "error").Select(i => i.Rule).ToHashSet();
        Assert.True(fired.Contains(rule), $"expected {rule}, got {string.Join(", ", fired.Order())}");
    }

    [Fact]
    public void Cli_exit_codes()
    {
        Assert.Equal(0, RunCli(valid.Dir));
        string broken = valid.Copy();
        SessionFixtures.Mutations["c2w.det"](broken);
        Assert.Equal(1, RunCli(broken, "--json"));
    }

    [Fact]
    public void Session_without_camera_data_passes_camera_checks()
    {
        string session = SessionFixtures.MakeValidSession(Path.Combine(valid.Root, "no-camera"), video: false);
        SessionFixtures.EditRows(session, rows =>
        {
            foreach (var r in rows)
                foreach (var c in Frames.C2wColumns.Append("camera_model").Concat(Frames.IntrinsicColumns))
                    r[c] = "";
        });
        Assert.Empty(SessionValidator.ValidateSession(session, video: false));
    }

    private static int RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "odx-validate.exe" : "odx-validate"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
