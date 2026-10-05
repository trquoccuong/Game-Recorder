#include <ISmmPlugin.h>
#include <eiface.h>
#include <iserver.h>
#include <networksystem/iprotobufbinding.h>
#include <networksystem/netmessage.h>
#include <tier1/bitbuf.h>
#include <tier1/utlstring.h>

#include <cstdio>
#include <ctime>
#include <filesystem>
#include <mutex>
#include <string>
#include <unordered_map>

#include "vtable.h"

namespace
{
// INetChannel vtable slots (hl2sdk-cs2 public/inetchannel.h): 34 INetChannelInfo virtuals, then the destructor, Reset,
// Clear, Shutdown and GetSteamNetConnection come before SendNetMessage.
constexpr int kSendNetMessage = 39;
constexpr int kSendData = 40;
constexpr int kInstallMessageFilter = 73;
constexpr int kUninstallMessageFilter = 74;
constexpr int kMaxPlayers = 64;

using SendNetMessageFn = bool (*)(void *channel, const CNetMessage *message, int8 bufType);
using SendDataFn = bool (*)(void *channel, bf_write &data, int8 bufType);
using MessageFilterFn = void (*)(void *channel, void *filter);

// Same layout as INetworkMessageProcessingPreFilter (one virtual), without pulling in inetchannel.h and its generated
// protobuf header.
class PreFilter
{
public:
    virtual bool FilterMessage(const CNetMessage *message, void *channel);
};

struct Peer
{
    int slot = -1;
    uint64 steamId = 0;
    std::string address;
    bool filterInstalled = false;
};

IVEngineServer2 *g_engine;
void **g_vtable;
SendNetMessageFn g_originalSend;
SendDataFn g_originalSendData;
PreFilter g_filter;
std::mutex g_mutex;
std::unordered_map<void *, Peer> g_peers;
FILE *g_log;
FILE *g_sendDataLog;
long long g_lastFlushUs;

void *Slot(void *object, int index) { return (*static_cast<void ***>(object))[index]; }

// The game server's current tick; -1 while no map runs.
int ServerTick()
{
    INetworkGameServer *server = g_pNetworkServerService->GetIGameServer();
    CGlobalVars *globals = server ? server->GetGlobals() : nullptr;
    return globals ? globals->tickcount : -1;
}

uint64 SteamIdOf(int slot)
{
    const CSteamID *id = g_engine->GetClientSteamID(CPlayerSlot(slot));
    return id ? id->ConvertToUint64() : 0;
}

// Called with g_mutex held. Slot and SteamID are looked up until the client is authenticated. A freed channel's
// address can come back for the next client: that resets the entry, filter included.
Peer &PeerOf(void *channel)
{
    Peer &peer = g_peers[channel];
    if (peer.steamId != 0)
    {
        if (static_cast<void *>(g_engine->GetPlayerNetInfo(CPlayerSlot(peer.slot))) == channel && SteamIdOf(peer.slot) == peer.steamId)
            return peer;
        peer = Peer{};
    }
    for (int i = 0; i < kMaxPlayers; i++)
    {
        INetChannelInfo *info = g_engine->GetPlayerNetInfo(CPlayerSlot(i));
        if (static_cast<void *>(info) != channel) continue;
        peer.slot = i;
        peer.address = info->GetAddress();
        peer.steamId = SteamIdOf(i);
        break;
    }
    return peer;
}

void AppendJsonString(std::string &out, const char *s)
{
    out += '"';
    for (; *s; s++)
    {
        unsigned char c = static_cast<unsigned char>(*s);
        if (c == '"' || c == '\\') (out += '\\') += static_cast<char>(c);
        else if (c == '\n') out += "\\n";
        else if (c < 0x20)
        {
            char escaped[8];
            snprintf(escaped, sizeof escaped, "\\u%04x", c);
            out += escaped;
        }
        else out += static_cast<char>(c);
    }
    out += '"';
}

std::string Base64(const unsigned char *data, int length)
{
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((length + 2) / 3 * 4);
    for (int i = 0; i < length; i += 3)
    {
        unsigned value = data[i] << 16 | (i + 1 < length ? data[i + 1] << 8 : 0) | (i + 2 < length ? data[i + 2] : 0);
        out += alphabet[value >> 18 & 63];
        out += alphabet[value >> 12 & 63];
        out += i + 1 < length ? alphabet[value >> 6 & 63] : '=';
        out += i + 2 < length ? alphabet[value & 63] : '=';
    }
    return out;
}

// One JSON object per line: the message as the engine's protobuf binding prints it (text format; bytes fields
// escaped), so nothing depends on our own copy of the game's .proto files. "tick" with "utc_us" is what puts the
// server's SourceTV demo on the clock (Odx.Session Cs2ServerCapture).
void Log(const char *direction, void *channel, const CNetMessage *message, int bufType)
{
    long long now = UtcNowUs();
    int tick = ServerTick();
    NetMessageInfo_t *info = message->GetNetMessage()->GetNetMessageInfo();
    CUtlString text;
    // ToString takes the CNetMessage itself (engine2 reads the proto object embedded at +0x30), not AsProto().
    const char *printed = info->m_pBinding->ToString(message, text);

    std::lock_guard<std::mutex> lock(g_mutex);
    Peer &peer = PeerOf(channel);
    if (!peer.filterInstalled)
    {
        // Uninstall first so a reset entry for a channel that already has the filter does not log twice.
        reinterpret_cast<MessageFilterFn>(Slot(channel, kUninstallMessageFilter))(channel, &g_filter);
        reinterpret_cast<MessageFilterFn>(Slot(channel, kInstallMessageFilter))(channel, &g_filter);
        peer.filterInstalled = true;
    }
    if (!g_log) return;

    std::string line = "{\"utc_us\":" + std::to_string(now) + ",\"tick\":" + std::to_string(tick) + ",\"direction\":\"" + direction +
                       "\",\"slot\":" + std::to_string(peer.slot) + ",\"steamid\":\"" + std::to_string(peer.steamId) +
                       "\",\"address\":";
    AppendJsonString(line, peer.address.c_str());
    line += ",\"message\":";
    AppendJsonString(line, info->m_pBinding->GetName());
    line += ",\"id\":" + std::to_string(info->m_MessageId) + ",\"buf\":" + std::to_string(bufType) + ",\"text\":";
    AppendJsonString(line, printed ? printed : text.Get());
    line += "}\n";
    fwrite(line.data(), 1, line.size(), g_log);
    if (now - g_lastFlushUs > 250000)
    {
        fflush(g_log);
        g_lastFlushUs = now;
    }
}

// Already serialized messages, sent as they are: the signon data each joining client gets (server info, string tables,
// class info, the game event list…). Kept as bytes in their own file (rare but large); Cs2ServerCapture splits them into
// messages. Only the buffer is read here, nothing is called on the engine.
bool SendDataHook(void *channel, bf_write &data, int8 bufType)
{
    long long now = UtcNowUs();
    int tick = ServerTick();
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        Peer &peer = PeerOf(channel);
        if (g_sendDataLog)
        {
            std::string line = "{\"utc_us\":" + std::to_string(now) + ",\"tick\":" + std::to_string(tick) +
                               ",\"direction\":\"out\",\"slot\":" + std::to_string(peer.slot) + ",\"steamid\":\"" +
                               std::to_string(peer.steamId) + "\",\"address\":";
            AppendJsonString(line, peer.address.c_str());
            line += ",\"message\":\"SendData\",\"id\":-1,\"buf\":" + std::to_string(bufType) + ",\"text\":\"\",\"bits\":" +
                    std::to_string(data.GetNumBitsWritten()) + ",\"data\":\"" +
                    Base64(data.GetBasePointer(), data.GetNumBytesWritten()) + "\"}\n";
            fwrite(line.data(), 1, line.size(), g_sendDataLog);
            fflush(g_sendDataLog);
        }
    }
    return g_originalSendData(channel, data, bufType);
}

bool SendNetMessageHook(void *channel, const CNetMessage *message, int8 bufType)
{
    Log("out", channel, message, bufType);
    return g_originalSend(channel, message, bufType);
}

bool PreFilter::FilterMessage(const CNetMessage *message, void *channel)
{
    Log("in", channel, message, -1);
    return false;
}
}  // namespace

class NetLogPlugin : public ISmmPlugin
{
public:
    bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late) override;
    bool Unload(char *error, size_t maxlen) override;
    const char *GetAuthor() override { return "Odx"; }
    const char *GetName() override { return "Odx net message log"; }
    const char *GetDescription() override { return "Logs every net message the server sends to or receives from each client"; }
    const char *GetURL() override { return ""; }
    const char *GetLicense() override { return "Proprietary"; }
    const char *GetVersion() override { return "0.1.0"; }
    const char *GetDate() override { return __DATE__; }
    const char *GetLogTag() override { return "ODX"; }
};

NetLogPlugin g_plugin;
PLUGIN_EXPOSE(NetLogPlugin, g_plugin);

bool NetLogPlugin::Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late)
{
    PLUGIN_SAVEVARS();
    GET_V_IFACE_CURRENT(GetEngineFactory, g_engine, IVEngineServer2, INTERFACEVERSION_VENGINESERVER);
    GET_V_IFACE_ANY(GetEngineFactory, g_pNetworkServerService, INetworkServerService, NETWORKSERVERSERVICE_INTERFACE_VERSION);

    g_vtable = FindVtable("networksystem.dll", ".?AVCNetChan@@");
    if (!g_vtable)
    {
        snprintf(error, maxlen, "CNetChan vtable not found in networksystem.dll");
        return false;
    }

    char stamp[32];
    time_t now = time(nullptr);
    strftime(stamp, sizeof stamp, "%Y%m%dT%H%M%SZ", gmtime(&now));
    std::filesystem::path dir = std::filesystem::path(ismm->GetBaseDir()) / "odx-netlog";
    std::filesystem::create_directories(dir);
    std::string path = (dir / (std::string(stamp) + ".jsonl")).string();
    g_log = fopen(path.c_str(), "wb");
    std::filesystem::create_directories(dir / "senddata");
    g_sendDataLog = fopen((dir / "senddata" / (std::string(stamp) + ".jsonl")).string().c_str(), "wb");
    if (!g_log || !g_sendDataLog)
    {
        snprintf(error, maxlen, "cannot write %s", path.c_str());
        return false;
    }

    g_originalSend = reinterpret_cast<SendNetMessageFn>(PatchVtable(g_vtable, kSendNetMessage, reinterpret_cast<void *>(&SendNetMessageHook)));
    g_originalSendData = reinterpret_cast<SendDataFn>(PatchVtable(g_vtable, kSendData, reinterpret_cast<void *>(&SendDataHook)));
    META_CONPRINTF("[odx] logging net messages to %s (server tick %d)\n", path.c_str(), ServerTick());
    return true;
}

bool NetLogPlugin::Unload(char *error, size_t maxlen)
{
    PatchVtable(g_vtable, kSendNetMessage, reinterpret_cast<void *>(g_originalSend));
    PatchVtable(g_vtable, kSendData, reinterpret_cast<void *>(g_originalSendData));
    std::lock_guard<std::mutex> lock(g_mutex);
    // Only channels that still belong to a client are alive; the others were freed with their filter list.
    for (int i = 0; i < kMaxPlayers; i++)
    {
        void *channel = g_engine->GetPlayerNetInfo(CPlayerSlot(i));
        auto peer = g_peers.find(channel);
        if (channel && peer != g_peers.end() && peer->second.filterInstalled)
            reinterpret_cast<MessageFilterFn>(Slot(channel, kUninstallMessageFilter))(channel, &g_filter);
    }
    g_peers.clear();
    if (g_log) fclose(g_log);
    if (g_sendDataLog) fclose(g_sendDataLog);
    g_log = g_sendDataLog = nullptr;
    return true;
}
