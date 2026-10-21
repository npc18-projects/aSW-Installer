using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AStoryWealth.NexusApi;

/// <summary>
/// Nexus Mods SSO flow (same as Vortex / Nolvus Dashboard).
/// The user authorizes in their browser; the API key arrives over a
/// websocket and never passes through user input.
///
/// Sequence:
///  1. connect wss://sso.nexusmods.com
///  2. send {"id": guid, "protocol": 2}  ->  {"data":{"connection_token":...}}
///  3. send {"id": guid, "token":{"connection_token":...}, "protocol": 2}
///  4. open https://www.nexusmods.com/sso?id=guid&application=slug
///  5. server pushes {"data":{"api_key":...}} once the user authorizes
/// </summary>
public sealed class NexusSsoClient : IDisposable
{
    private const string SsoUrl = "wss://sso.nexusmods.com";
    private ClientWebSocket? _ws;

    /// <summary>Raised with the SSO url the user must open in their browser.</summary>
    public event Action<string>? OnBrowserUrl;
    public event Action<string>? OnError;

    /// <summary>Runs the full flow. Returns the API key. Cancels via ct.</summary>
    public async Task<string> AuthorizeAsync(string applicationSlug, CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(SsoUrl), ct);

        var id = Guid.NewGuid().ToString();

        // step 2: hello
        await SendAsync(new { id, protocol = 2 }, ct);
        var first = await ReceiveAsync(ct);
        var connectionToken = first.GetProperty("data").GetProperty("connection_token").GetString()
            ?? throw new InvalidOperationException("No connection_token in SSO response");

        // step 3: echo token back
        await SendAsync(new { id, token = new { connection_token = connectionToken }, protocol = 2 }, ct);

        // step 4: send user to browser
        var ssoPage = $"https://www.nexusmods.com/sso?id={id}&application={Uri.EscapeDataString(applicationSlug)}";
        OnBrowserUrl?.Invoke(ssoPage);

        // step 5: wait for api_key (or error) while the user authorizes
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var msg = await ReceiveAsync(ct);
            var data = msg.GetProperty("data");
            if (data.TryGetProperty("api_key", out var key) && key.ValueKind == JsonValueKind.String)
                return key.GetString()!;
            if (data.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"SSO error: {err.GetString()}");
        }
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await _ws!.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, result.Count);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("SSO websocket closed");
            if (result.EndOfMessage) break;
        }
        var json = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(ms.ToArray()));
        if (json.TryGetProperty("success", out var ok) && !ok.GetBoolean())
        {
            var err = json.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            OnError?.Invoke(err ?? "unknown");
            throw new InvalidOperationException($"SSO error: {err}");
        }
        return json;
    }

    public void Dispose() => _ws?.Dispose();
}
