using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Odx.Protocol;

namespace Odx.Agent;

/// <summary>A kept round's files, sizes and SHA-256, for checking a copy or a later upload.</summary>
public static class Manifest
{
    public const string FileName = "manifest.json";

    public static void Write(string dir)
    {
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(f => f != FileName)
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
        File.WriteAllText(Path.Combine(dir, FileName),
            new JsonObject { ["files"] = new JsonArray(files.ToArray()) }.ToJsonString(RoundRecord.PrettyJson));
    }
}
