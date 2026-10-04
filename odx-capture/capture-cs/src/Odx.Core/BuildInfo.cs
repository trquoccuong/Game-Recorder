using System.Reflection;

namespace Odx.Core;

public static class BuildInfo
{
    /// <summary>
    /// Version of the running tool, "&lt;version&gt;+&lt;git commit&gt;" (the SDK appends the commit at build time). Written into
    /// recording.json and session.json so every delivered batch can be traced back to the code that produced it.
    /// </summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
