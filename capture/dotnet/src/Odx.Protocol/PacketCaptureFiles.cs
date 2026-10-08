using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Odx.Protocol;

/// <summary>What a player's agent keeps next to its capture for packets, and odx merge reads.</summary>
public static class PacketCaptureFiles
{
    public const string Pcapng = "packets.pcapng", LocalAddresses = "packets_local.json";

    /// <summary>This machine's addresses, so the assembler can tell sent packets from received ones.</summary>
    public static void WriteLocalAddresses(string dir)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Select(a => (JsonNode)a.Address.ToString())
            .ToArray();
        File.WriteAllText(Path.Combine(dir, LocalAddresses), new JsonObject { ["addresses"] = new JsonArray(addresses) }.ToJsonString());
    }
}
