using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Protocol;

namespace Odx.Agent;

/// <summary>A kept round's files, sizes and SHA-256, for checking a copy or a later upload.</summary>
public static class Manifest
{
    public const string FileName = "manifest.json";

    /// <summary>Lists every file of the round folder in raw/manifest.json (manifest.json when not tidied).</summary>
    public static void Write(string dir)
    {
        string path = CaptureFolder.Raw(dir, FileName);
        string self = Path.GetRelativePath(dir, path).Replace('\\', '/');
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(f => f != self)
            .Order(StringComparer.Ordinal)
            .Select(f =>
            {
                string path = Path.Combine(dir, f);
                using var stream = File.OpenRead(path);
                return (JsonNode)new JsonObject
                {
                    ["path"] = f,
                    ["bytes"] = stream.Length,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream)),
                };
            });
        File.WriteAllText(path,
            new JsonObject { ["files"] = new JsonArray(files.ToArray()) }.ToJsonString(RoundRecord.PrettyJson));
    }
}
