using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odx.Session;

/// <summary>
/// ZeroTier One's HTTP API, for a room's virtual LAN (layer 2, so LAN games find each other by broadcast). On a player's
/// machine it is the local service (join/leave a network); on the orchestrator's server the same API is the network
/// controller (one network per room, members authorized while they are in the room).
/// </summary>
public sealed partial class ZeroTierApi(Uri baseAddress, string token) : IDisposable
{
    public static readonly Uri Local = new("http://127.0.0.1:9993");

    private readonly HttpClient _http = new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "X-ZT1-Auth", token } },
    };

    /// <summary>
    /// This machine's API token as a normal user can read it: the copy ZeroTier's tray app keeps per user (the service's own
    /// authtoken.secret is admin-only). Null when ZeroTier is not installed or its tray app never ran.
    /// </summary>
    public static string? LocalToken()
    {
        string path = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroTier", "authtoken.secret")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zeroTierOneAuthToken");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsNodeId(string s) => NodeIdPattern().IsMatch(s);

    public static bool IsNetworkId(string s) => NetworkIdPattern().IsMatch(s);

    /// <exception cref="HttpRequestException">The service does not answer or refuses the token.</exception>
    public async Task<string> NodeIdAsync(CancellationToken stop) =>
        (string)(await _http.GetFromJsonAsync<JsonObject>("/status", stop))!["address"]!;

    public async Task JoinAsync(string networkId, CancellationToken stop) =>
        (await _http.PostAsJsonAsync($"/network/{Checked(networkId)}", new JsonObject(), stop)).EnsureSuccessStatusCode();

    public async Task LeaveAsync(string networkId, CancellationToken stop) =>
        (await _http.DeleteAsync($"/network/{Checked(networkId)}", stop)).EnsureSuccessStatusCode();

    /// <summary>"OK" once this machine is authorized and configured, "REQUESTING_CONFIGURATION", "ACCESS_DENIED"…; NOT_FOUND if not joined.</summary>
    public async Task<(string Status, IReadOnlyList<string> Addresses)> NetworkAsync(string networkId, CancellationToken stop)
    {
        using var response = await _http.GetAsync($"/network/{Checked(networkId)}", stop);
        if (response.StatusCode == HttpStatusCode.NotFound) return ("NOT_FOUND", []);
        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonObject>(stop))!;
        var addresses = json["assignedAddresses"]?.AsArray().Select(a => ((string)a!).Split('/')[0]).ToList() ?? [];
        return ((string?)json["status"] ?? "", addresses);
    }

    /// <summary>
    /// Controller: a private network with an IPv4 range for the members. Every room may use the same range: a player is in
    /// one room's network at a time.
    /// </summary>
    public async Task<string> CreateNetworkAsync(string name, CancellationToken stop)
    {
        string controller = await NodeIdAsync(stop);
        var config = new JsonObject
        {
            ["name"] = name,
            ["private"] = true,
            ["enableBroadcast"] = true,
            ["v4AssignMode"] = new JsonObject { ["zt"] = true },
            ["ipAssignmentPools"] = new JsonArray(new JsonObject { ["ipRangeStart"] = "10.147.20.1", ["ipRangeEnd"] = "10.147.20.254" }),
            ["routes"] = new JsonArray(new JsonObject { ["target"] = "10.147.20.0/24" }),
        };
        using var response = await _http.PostAsJsonAsync($"/controller/network/{controller}______", config, stop);
        response.EnsureSuccessStatusCode();
        return (string)(await response.Content.ReadFromJsonAsync<JsonObject>(stop))!["id"]!;
    }

    public async Task AuthorizeAsync(string networkId, string nodeId, bool authorized, CancellationToken stop)
    {
        if (!IsNodeId(nodeId)) throw new ArgumentException($"not a ZeroTier node id: {nodeId}", nameof(nodeId));
        (await _http.PostAsJsonAsync($"/controller/network/{Checked(networkId)}/member/{nodeId}",
            new JsonObject { ["authorized"] = authorized }, stop)).EnsureSuccessStatusCode();
    }

    public async Task DeleteNetworkAsync(string networkId, CancellationToken stop) =>
        (await _http.DeleteAsync($"/controller/network/{Checked(networkId)}", stop)).EnsureSuccessStatusCode();

    private static string Checked(string networkId) =>
        IsNetworkId(networkId) ? networkId : throw new ArgumentException($"not a ZeroTier network id: {networkId}", nameof(networkId));

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("^[0-9a-f]{10}$")]
    private static partial Regex NodeIdPattern();

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex NetworkIdPattern();
}
