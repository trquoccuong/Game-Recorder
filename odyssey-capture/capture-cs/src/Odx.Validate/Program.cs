// odx-validate <session_dir> [<session_dir> ...] [--json] [--no-video]
// A session dir holds session.json, frames.csv and video.mp4. Exit code 1 if any error is found; warnings do not fail.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Odx.Validate;

Console.OutputEncoding = new UTF8Encoding(false);
bool json = args.Contains("--json"), video = !args.Contains("--no-video");
var sessions = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
if (sessions.Count == 0 || args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a is not ("--json" or "--no-video")))
{
    Console.Error.WriteLine("usage: odx-validate <session_dir> [<session_dir> ...] [--json] [--no-video]");
    return 2;
}

var results = sessions.ToDictionary(s => s, s => SessionValidator.ValidateSession(s, video));
if (json)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    Console.WriteLine(JsonSerializer.Serialize(results, options));
}
else
{
    foreach (var (session, issues) in results)
    {
        int errors = issues.Count(i => i.Severity == "error");
        Console.WriteLine($"{(errors > 0 ? "FAIL" : "PASS")}  {session}  ({errors} errors, {issues.Count - errors} warnings)");
        foreach (var i in issues) Console.WriteLine($"  [{i.Severity}] {i.Rule}: {i.Message}");
    }
}
return results.Values.Any(issues => issues.Any(i => i.Severity == "error")) ? 1 : 0;
