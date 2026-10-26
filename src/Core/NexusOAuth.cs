using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AStoryWealth.NexusApi;

namespace AStoryWealth.Core;

/// <summary>
/// Settings for the Nexus sign-in. The client id is issued by Nexus when the app is registered; until then the guide's
/// public_test client is used. They live in Data/nexus-oauth.json so a new id needs no code change.
/// </summary>
public sealed class NexusOAuthConfig
{
    public string ClientId { get; set; } = "";
    /// <summary>The port the app listens on at 127.0.0.1 for Nexus's answer. Nexus matches the redirect address exactly.</summary>
    public int Port { get; set; } = 8089;
    public string Scope { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "https://users.nexusmods.com/oauth/authorize";
    public string TokenUrl { get; set; } = "https://users.nexusmods.com/oauth/token";
    public string RevokeUrl { get; set; } = "https://users.nexusmods.com/oauth/revoke";

    public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    public static NexusOAuthConfig Load()
    {
        try
        {
            var asm = typeof(NexusOAuthConfig).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("nexus-oauth.json", StringComparison.OrdinalIgnoreCase));
            if (res is null) return new();
            using var s = asm.GetManifestResourceStream(res)!;
            return JsonSerializer.Deserialize<NexusOAuthConfig>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }
}

public sealed record NexusTokens(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc);

/// <summary>Where the tokens are kept between runs.</summary>
public interface INexusTokenStore
{
    string? Load();
    void Save(string json);
    void Delete();
}

/// <summary>Tokens encrypted with Windows' per-user encryption, in %LOCALAPPDATA%\AStoryWealth.</summary>
public sealed class DpapiTokenStore : INexusTokenStore
{
    private const string Name = "nexus-oauth";
    public string? Load() => SecretStore.Load(Name);
    public void Save(string json) => SecretStore.Save(Name, json);
    public void Delete() => SecretStore.Delete(Name);
}

/// <summary>
/// The user's Nexus sign-in: OAuth 2.0 with PKCE, as a public desktop app (no client secret).
/// Sign in opens the browser, listens on 127.0.0.1 for Nexus's answer, and swaps the code for an access and a refresh token.
/// Every API call asks for a fresh access token, so one that runs out during a long install is renewed in time.
/// </summary>
public sealed class NexusSession : NexusCredential
{
    private readonly NexusOAuthConfig _cfg;
    private readonly INexusTokenStore _store;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NexusTokens? _tokens;

    public NexusSession(NexusOAuthConfig cfg, INexusTokenStore store, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _store = store;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"aSWInstaller/{NexusApiClient.AppVersion} (+https://github.com/npc18-projects/aSW-Installer)");
        _tokens = ReadTokens(store.Load());
    }

    public bool IsSignedIn => _tokens is not null;

    public override IEnumerable<string> Secrets => _tokens is null ? [] : [_tokens.AccessToken, _tokens.RefreshToken];

    public override async Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct));

    // ---- PKCE ------------------------------------------------------------------------------

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A random string of 86 characters from the unreserved set (the spec allows 43 to 128).</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public string BuildAuthorizeUrl(string state, string challenge)
    {
        var q = new List<string>
        {
            "client_id=" + Uri.EscapeDataString(_cfg.ClientId),
            "response_type=code",
            "redirect_uri=" + Uri.EscapeDataString(_cfg.RedirectUri),
            "state=" + Uri.EscapeDataString(state),
            "code_challenge_method=S256",
            "code_challenge=" + Uri.EscapeDataString(challenge),
            "scope=" + Uri.EscapeDataString(_cfg.Scope),
        };
        return _cfg.AuthorizeUrl + "?" + string.Join("&", q);
    }

    // ---- sign in ---------------------------------------------------------------------------

    /// <summary>Opens the browser through <paramref name="openBrowser"/> and waits (up to <paramref name="timeout"/>) for the user to allow the app.</summary>
    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(_cfg.ClientId)) throw new InvalidOperationException("No Nexus client id is set.");
        var verifier = NewVerifier();
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        TcpListener listener;
        try { listener = new TcpListener(IPAddress.Loopback, _cfg.Port); listener.Start(); }
        catch (SocketException ex) { throw new InvalidOperationException($"Port {_cfg.Port} on this PC is busy, so the Nexus sign-in can't finish. Close what is using it and try again.", ex); }
        try
        {
            openBrowser(BuildAuthorizeUrl(state, Challenge(verifier)));
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
            string code;
            try { code = await WaitForCodeAsync(listener, state, timer.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Nexus sign-in took too long. Try again."); }
            var tokens = await TokenRequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _cfg.ClientId,
                ["code"] = code,
                ["redirect_uri"] = _cfg.RedirectUri,
                ["code_verifier"] = verifier,
            }, previousRefresh: null, ct);
            await _gate.WaitAsync(ct);
            try { SetTokens(tokens); } finally { _gate.Release(); }
        }
        finally { listener.Stop(); }
    }

    /// <summary>What Nexus's redirect carried: a code to swap for tokens, or an error to show the user.</summary>
    public sealed record CallbackResult(string? Code, string? Error, bool Ignore);

    /// <summary>Reads the first line of the browser's request ("GET /callback?code=...&amp;state=... HTTP/1.1").</summary>
    public static CallbackResult ParseCallback(string requestLine, string expectedState)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return new(null, null, true);
        var target = parts[1];
        var qi = target.IndexOf('?');
        var path = qi < 0 ? target : target[..qi];
        if (path != "/callback") return new(null, null, true);   // favicon and the like
        var query = new Dictionary<string, string>();
        if (qi >= 0)
            foreach (var kv in target[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.IndexOf('=');
                var k = Uri.UnescapeDataString((eq < 0 ? kv : kv[..eq]).Replace('+', ' '));
                var v = eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
                query[k] = v;
            }
        if (!query.TryGetValue("state", out var st) || st != expectedState) return new(null, "The sign-in answer didn't match the request. Start again from the app.", false);
        if (query.TryGetValue("error", out var err))
            return new(null, query.TryGetValue("error_description", out var d) && d.Length > 0 ? d : err, false);
        return query.TryGetValue("code", out var code) && code.Length > 0 ? new(code, null, false) : new(null, "Nexus didn't send a sign-in code.", false);
    }

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            client.ReceiveTimeout = client.SendTimeout = 5000;
            var stream = client.GetStream();
            var buf = new byte[8192];
            var n = 0;
            try { n = await stream.ReadAsync(buf, ct); } catch (IOException) { continue; }
            var head = Encoding.ASCII.GetString(buf, 0, n);
            var line = head.Split("\r\n")[0];
            var result = ParseCallback(line, state);
            if (result.Ignore) { await Reply(stream, 404, "Not found", ct); continue; }
            if (result.Code is null)
            {
                await Reply(stream, 400, "Sign-in failed: " + WebUtility.HtmlEncode(result.Error ?? "unknown error") + ". You can close this tab and try again in the app.", ct);
                if (result.Error!.StartsWith("The sign-in answer")) continue;   // a stray or forged request: keep waiting for the real one
                throw new InvalidOperationException(result.Error);
            }
            await Reply(stream, 200, "You're signed in. You can close this tab and go back to the installer.", ct);
            return result.Code;
        }
    }

    private static async Task Reply(NetworkStream s, int status, string message, CancellationToken ct)
    {
        var body = $"<!doctype html><meta charset=\"utf-8\"><title>aSW Installer</title><body style=\"font-family:Segoe UI,sans-serif;background:#121519;color:#e8e8e8;text-align:center;padding-top:15vh\"><h2>aSW Installer</h2><p>{message}</p>";
        var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
        try { await s.WriteAsync(bytes, ct); await s.FlushAsync(ct); } catch (IOException) { }
    }

    // ---- tokens ----------------------------------------------------------------------------

    /// <summary>A valid access token, renewed first when it is about to expire. Throws <see cref="NexusSignedOutException"/> when Nexus no longer honours the sign-in.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var t = _tokens ?? throw new NexusSignedOutException();
            if (t.ExpiresAtUtc - DateTime.UtcNow > TimeSpan.FromSeconds(60)) return t.AccessToken;
            NexusTokens fresh;
            try
            {
                fresh = await TokenRequestAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _cfg.ClientId,
                    ["refresh_token"] = t.RefreshToken,
                }, previousRefresh: t.RefreshToken, ct);
            }
            catch (NexusSignedOutException)
            {
                ClearTokens();   // revoked or expired for good
                throw;
            }
            SetTokens(fresh);
            return fresh.AccessToken;
        }
        finally { _gate.Release(); }
    }

    private async Task<NexusTokens> TokenRequestAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
    {
        if (_cfg.Scope.Length > 0 && form["grant_type"] == "authorization_code") form["scope"] = _cfg.Scope;
        using var resp = await _http.PostAsync(_cfg.TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // 4xx on a refresh means the user took access away (or the grant ran out); anything else (5xx, network) is temporary.
            if (previousRefresh is not null && (int)resp.StatusCode is >= 400 and < 500) throw new NexusSignedOutException("Nexus no longer accepts this sign-in.");
            throw new HttpRequestException($"Nexus sign-in service answered {(int)resp.StatusCode}");
        }
        return ParseTokens(body, previousRefresh, DateTime.UtcNow);
    }

    public static NexusTokens ParseTokens(string json, string? previousRefresh, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("Nexus sent no access token.");
        var refresh = r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String && rt.GetString() is { Length: > 0 } s ? s : previousRefresh;
        if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Nexus sent no refresh token.");
        var seconds = r.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble()
            : r.TryGetProperty("expires_in", out var es) && es.ValueKind == JsonValueKind.String && double.TryParse(es.GetString(), out var d) ? d : 900;
        return new NexusTokens(access, refresh, nowUtc.AddSeconds(seconds));
    }

    private static NexusTokens? ReadTokens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var t = new NexusTokens(r.GetProperty("access").GetString()!, r.GetProperty("refresh").GetString()!, r.GetProperty("expires").GetDateTime().ToUniversalTime());
            return t.AccessToken.Length > 0 && t.RefreshToken.Length > 0 ? t : null;
        }
        catch { return null; }
    }

    private void SetTokens(NexusTokens t)
    {
        _tokens = t;
        try { _store.Save(JsonSerializer.Serialize(new { access = t.AccessToken, refresh = t.RefreshToken, expires = t.ExpiresAtUtc })); }
        catch (Exception ex) { InstallLog.Warn("auth", $"couldn't save the sign-in on this PC: {ex.Message}"); }
    }

    private void ClearTokens()
    {
        _tokens = null;
        try { _store.Delete(); } catch { }
    }

    /// <summary>Forgets the sign-in on this PC and asks Nexus to cancel it (best effort: a failure there doesn't stop the sign-out).</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        NexusTokens? t;
        await _gate.WaitAsync(ct);
        try { t = _tokens; ClearTokens(); } finally { _gate.Release(); }
        if (t is null || string.IsNullOrEmpty(_cfg.RevokeUrl)) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _http.PostAsync(_cfg.RevokeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = t.RefreshToken,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = _cfg.ClientId,
            }), cts.Token);
        }
        catch { /* the sign-in is already gone from this PC */ }
    }
}
