using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Odx.Core;

/// <summary>
/// The part of the obs-websocket 5 protocol odx record needs (built into OBS Studio 28+, Tools → WebSocket Server
/// Settings): identify with the optional password, send requests, wait for RecordStateChanged events.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    /// <summary>EventSubscription.Outputs: RecordStateChanged and the other output events.</summary>
    private const int OutputEvents = 1 << 6;

    private readonly ClientWebSocket _socket = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _pending = [];
    private readonly Channel<JsonObject> _recordStates = Channel.CreateUnbounded<JsonObject>();
    private Task? _receive;
    private int _nextId;

    /// <param name="address">host:port of the OBS WebSocket server (default port 4455), or a ws:// URI.</param>
    /// <exception cref="ObsException">OBS refused the connection or the password.</exception>
    public static async Task<ObsClient> ConnectAsync(string address, string? password, CancellationToken cancel = default)
    {
        var client = new ObsClient();
        try
        {
            var uri = new Uri(address.Contains("://", StringComparison.Ordinal) ? address : $"ws://{address}");
            await client._socket.ConnectAsync(uri, cancel);
            var hello = await client.ReceiveAsync(cancel) ?? throw new ObsException("OBS closed the connection");
            var identify = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = OutputEvents };
            if (hello["d"]?["authentication"] is JsonObject auth)
            {
                if (string.IsNullOrEmpty(password)) throw new ObsException("OBS asks for a WebSocket password");
                identify["authentication"] = Authentication(password, (string)auth["salt"]!, (string)auth["challenge"]!);
            }
            await client.SendAsync(new JsonObject { ["op"] = 1, ["d"] = identify }, cancel);
            var identified = await client.ReceiveAsync(cancel);
            // 4009: WebSocketCloseCode::AuthenticationFailed.
            if (identified?["op"]?.GetValue<int>() != 2)
                throw new ObsException((int?)client._socket.CloseStatus == 4009 ? "wrong OBS WebSocket password"
                    : $"OBS refused the connection: {client._socket.CloseStatusDescription}");
            client._receive = Task.Run(client.ReceiveLoopAsync);
            return client;
        }
        catch (Exception e) when (e is WebSocketException or UriFormatException or ObsException)
        {
            await client.DisposeAsync();
            throw e as ObsException ?? new ObsException($"cannot connect to OBS at {address}: {e.Message}");
        }
    }

    /// <summary>obs-websocket 5: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    public static string Authentication(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <returns>The response's responseData (empty when there is none).</returns>
    /// <exception cref="ObsException">The request failed.</exception>
    public async Task<JsonObject> RequestAsync(string type, JsonObject? data = null, CancellationToken cancel = default)
    {
        string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = response;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data is not null) d["requestData"] = data;
        await SendAsync(new JsonObject { ["op"] = 6, ["d"] = d }, cancel);
        var result = await response.Task.WaitAsync(cancel);
        var status = result["requestStatus"];
        if (status?["result"]?.GetValue<bool>() != true)
            throw new ObsException($"OBS {type} failed: {status?["comment"] ?? status?["code"]}");
        return result["responseData"] as JsonObject ?? [];
    }

    /// <summary>Waits for a RecordStateChanged event with this outputState (e.g. OBS_WEBSOCKET_OUTPUT_STOPPED).</summary>
    public async Task<JsonObject> WaitForRecordStateAsync(string state, CancellationToken cancel = default)
    {
        while (true)
        {
            var e = await _recordStates.Reader.ReadAsync(cancel);
            if ((string?)e["outputState"] == state) return e;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (await ReceiveAsync(CancellationToken.None) is { } message)
            {
                int op = message["op"]?.GetValue<int>() ?? -1;
                var d = message["d"] as JsonObject;
                if (op == 7 && (string?)d?["requestId"] is string id)
                {
                    TaskCompletionSource<JsonObject>? response;
                    lock (_pending) _pending.Remove(id, out response);
                    response?.TrySetResult(d!);
                }
                else if (op == 5 && (string?)d?["eventType"] == "RecordStateChanged" && d!["eventData"] is JsonObject data)
                {
                    _recordStates.Writer.TryWrite(data);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        var closed = new ObsException("the connection to OBS was closed");
        lock (_pending)
        {
            foreach (var response in _pending.Values) response.TrySetException(closed);
            _pending.Clear();
        }
        _recordStates.Writer.TryComplete(closed);
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancel);
    }

    /// <summary>Null once OBS has closed the connection.</summary>
    private async Task<JsonObject?> ReceiveAsync(CancellationToken cancel)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonNode.Parse(message.ToArray()) as JsonObject;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
            }
        }
        if (_receive is not null) await _receive;
        _socket.Dispose();
    }
}

public sealed class ObsException(string message) : Exception(message);
