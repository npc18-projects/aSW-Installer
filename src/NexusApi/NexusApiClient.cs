using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AStoryWealth.NexusApi;

/// <summary>
/// Minimal Nexus Mods API client for the aSW installer spike.
/// Docs: https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_formalized
/// All requests require an API key header + identifying User-Agent.
/// </summary>
public sealed class NexusApiClient : IDisposable
{
    private const string BaseUrl = "https://api.nexusmods.com/";
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The running app's version, sent to Nexus with every request.</summary>
    public static string AppVersion { get; } =
        (System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>A personal API key. Developers only; the app signs in with OAuth.</summary>
    public NexusApiClient(string apiKey, string? appVersion = null) : this(new ApiKeyCredential(apiKey), null, appVersion) { }

    /// <param name="credential">Added to every request for the Nexus API host (never to a download address).</param>
    /// <param name="inner">The handler underneath, so tests can stand in for the network.</param>
    public NexusApiClient(NexusCredential credential, HttpMessageHandler? inner = null, string? appVersion = null)
    {
        appVersion ??= AppVersion;
        _http = new HttpClient(new CredentialHandler(credential, inner ?? new HttpClientHandler()));
        _http.BaseAddress = new Uri(BaseUrl);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Nexus requires identifying headers or returns 409
        _http.DefaultRequestHeaders.Add("Application-Version", appVersion);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"aSWInstaller/{appVersion} (+https://github.com/npc18-projects/aSW-Installer)");
        _http.DefaultRequestHeaders.Add("Application-Name", "aSW Installer");
    }

    // ---------- User / account ----------

    public sealed record NexusUser(int UserId, string Name, bool IsPremium, bool IsSupporter);

    /// <summary>
    /// Validates the API key and returns account info incl. premium status.
    /// This is the gate for choosing the download engine — never infer
    /// premium from a download attempt (403s are ambiguous and rate-limited).
    /// </summary>
    public async Task<NexusUser> ValidateUserAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("v1/users/validate.json", ct);
        await EnsureAsync(resp, "GET users/validate");
        var d = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        bool Get(string name) =>
            d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        return new NexusUser(
            d.GetProperty("user_id").GetInt32(),
            d.GetProperty("name").GetString() ?? "?",
            Get("is_premium"),
            Get("is_supporter"));
    }

    // ---------- Collections ----------

    // ---------- Collections (GraphQL v2) ----------

    /// <summary>
    /// Collection revision via GraphQL (slug-addressed — the REST v1 endpoint
    /// requires a uuid and 404s on URL slugs).
    /// </summary>
    public async Task<JsonElement> GetCollectionRevisionAsync(string collectionSlug, string? domainName = "fallout4", int? revision = null, CancellationToken ct = default)
    {
        const string query = """
            query($slug:String!,$domainName:String,$revision:Int){
              collectionRevision(slug:$slug,domainName:$domainName,revision:$revision){
                revisionNumber collectionId collection{name} modCount
                downloadLink
                modFiles{ fileId optional file{ fileId modId size mod{name game{domainName}} } }
              }
            }
            """;
        var body = JsonSerializer.Serialize(new
        {
            query,
            variables = new { slug = collectionSlug, domainName, revision }
        }, _json);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("v2/graphql", content, ct);
        await EnsureAsync(resp, $"GraphQL collectionRevision({collectionSlug})");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        if (doc.TryGetProperty("errors", out var errors))
            throw new HttpRequestException($"GraphQL error: {errors.GetRawText()}");
        return doc.GetProperty("data").GetProperty("collectionRevision");
    }

    /// <summary>All published collections on a game domain owned by one user (resolved by name).</summary>
    public sealed record UserCollection(string Slug, string Name, string Author, int LatestRevision, string Summary, string TileImageUrl, string HeaderImageUrl,
        CollectionStats? Stats = null);

    /// <summary>What Nexus publishes about a collection: popularity of the collection and size/mod count of its latest revision.</summary>
    public sealed record CollectionStats(long Endorsements, long TotalDownloads, long UniqueDownloads, double RatingPercent, long RatingCount,
        DateTime? LastPublished, DateTime? FirstPublished, int ModCount, long SizeBytes, bool AdultContent,
        int Revision = 0, string? Changelog = null, DateTime? ChangelogDate = null);

    private const string StatsFields =
        "endorsements totalDownloads uniqueDownloads overallRating overallRatingCount lastPublishedAt firstPublishedAt "
        + "latestPublishedRevision{ revisionNumber modCount totalSize adultContent collectionChangelog{ description createdAt } }";

    private static long AsLong(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (long)e.GetDouble(),
        JsonValueKind.String => long.TryParse(e.GetString(), out var l) ? l : 0,
        _ => 0,
    };

    private static double AsDouble(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => double.TryParse(e.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
        _ => 0,
    };

    private static DateTime? AsDate(JsonElement c, string name) =>
        c.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && DateTime.TryParse(e.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>Reads the fields of <see cref="StatsFields"/> from a collection node; zeros for anything Nexus left out.</summary>
    public static CollectionStats ParseStats(JsonElement c)
    {
        long L(string n) => c.TryGetProperty(n, out var e) ? AsLong(e) : 0;
        var rev = c.TryGetProperty("latestPublishedRevision", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        return new CollectionStats(L("endorsements"), L("totalDownloads"), L("uniqueDownloads"),
            c.TryGetProperty("overallRating", out var rt) ? AsDouble(rt) : 0, L("overallRatingCount"),
            AsDate(c, "lastPublishedAt"), AsDate(c, "firstPublishedAt"),
            rev.ValueKind == JsonValueKind.Object && rev.TryGetProperty("modCount", out var mc) ? (int)AsLong(mc) : 0,
            rev.ValueKind == JsonValueKind.Object && rev.TryGetProperty("totalSize", out var ts) ? AsLong(ts) : 0,
            rev.ValueKind == JsonValueKind.Object && rev.TryGetProperty("adultContent", out var ad) && ad.ValueKind == JsonValueKind.True,
            rev.ValueKind == JsonValueKind.Object && rev.TryGetProperty("revisionNumber", out var rn) ? (int)AsLong(rn) : 0,
            Changelog: rev.ValueKind == JsonValueKind.Object && rev.TryGetProperty("collectionChangelog", out var cl) && cl.ValueKind == JsonValueKind.Object
                && cl.TryGetProperty("description", out var cd) && cd.ValueKind == JsonValueKind.String ? cd.GetString() : null,
            ChangelogDate: rev.ValueKind == JsonValueKind.Object && rev.TryGetProperty("collectionChangelog", out var cl2) && cl2.ValueKind == JsonValueKind.Object ? AsDate(cl2, "createdAt") : null);
    }

    /// <summary>Nexus member id of one account (it never changes, so callers keep it).</summary>
    public async Task<string> GetMemberIdAsync(string username, CancellationToken ct = default)
    {
        var name = username.Replace("\"", ""); // no quote injection
        var idQuery = JsonSerializer.Serialize(new { query = $"{{ userByName(name:\"{name}\") {{ memberId }} }}" }, _json);
        using var content = new StringContent(idQuery, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("v2/graphql", content, ct);
        await EnsureAsync(resp, $"GraphQL userByName({name})");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        if (doc.TryGetProperty("errors", out var errors))
            throw new HttpRequestException($"GraphQL error: {errors.GetRawText()}");
        return doc.GetProperty("data").GetProperty("userByName").GetProperty("memberId").ToString();
    }

    /// <summary>
    /// Every published collection on a game domain owned by any of the given members, in one GraphQL call
    /// (the nested filter with op:OR matches any of the users, the outer one keeps the game).
    /// </summary>
    public async Task<List<UserCollection>> ListCollectionsByUsersAsync(IEnumerable<string> memberIds, string domain = "fallout4", CancellationToken ct = default)
    {
        var users = memberIds.Select(id => new { value = id }).ToArray();
        var listQuery = new
        {
            query = """
                query($users:[BaseFilterValue!],$domain:String!){
                  collectionsV2(filter:{gameDomain:[{value:$domain}], filter:[{op:OR, userId:$users}]}, count:100){
                    nodes{ slug name summary tileImage{ url } headerImage{ url } latestPublishedRevision{ revisionNumber } user{ name } __STATS__ }
                  }
                }
                """.Replace("__STATS__", StatsFields),
            variables = new { users, domain }
        };
        var body = JsonSerializer.Serialize(listQuery, _json);
        using var c2 = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var r2 = await _http.PostAsync("v2/graphql", c2, ct);
        await EnsureAsync(r2, "GraphQL collectionsV2(users)");
        var doc2 = await r2.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        if (doc2.TryGetProperty("errors", out var errs))
            throw new HttpRequestException($"GraphQL error: {errs.GetRawText()}");
        var list = new List<UserCollection>();
        foreach (var n in doc2.GetProperty("data").GetProperty("collectionsV2").GetProperty("nodes").EnumerateArray())
        {
            list.Add(new UserCollection(
                n.GetProperty("slug").GetString()!,
                n.GetProperty("name").GetString() ?? "",
                n.GetProperty("user").GetProperty("name").GetString() ?? "",
                n.GetProperty("latestPublishedRevision").GetProperty("revisionNumber").GetInt32(),
                n.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
                n.TryGetProperty("tileImage", out var ti) && ti.ValueKind == JsonValueKind.Object && ti.TryGetProperty("url", out var tu) ? tu.GetString() ?? "" : "",
                n.TryGetProperty("headerImage", out var hi) && hi.ValueKind == JsonValueKind.Object && hi.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                ParseStats(n)));
        }
        return list;
    }

    /// <summary>Name, summary and header image of one collection (used for the base banner).</summary>
    public sealed record CollectionInfo(string Name, string Summary, string HeaderImageUrl, CollectionStats? Stats = null);

    public async Task<CollectionInfo> GetCollectionInfoAsync(string slug, string domain = "fallout4", CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = """
                query($slug:String!,$domainName:String){
                  collection(slug:$slug, domainName:$domainName){ name summary headerImage{ url } __STATS__ }
                }
                """.Replace("__STATS__", StatsFields),
            variables = new { slug, domainName = domain }
        }, _json);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("v2/graphql", content, ct);
        await EnsureAsync(resp, $"GraphQL collection({slug})");
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        if (doc.TryGetProperty("errors", out var errors))
            throw new HttpRequestException($"GraphQL error: {errors.GetRawText()}");
        var c = doc.GetProperty("data").GetProperty("collection");
        return new CollectionInfo(
            c.GetProperty("name").GetString() ?? "",
            c.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            c.TryGetProperty("headerImage", out var hi) && hi.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            ParseStats(c));
    }

    /// <summary>
    /// CDN link for the collection archive itself (the A-Name-&lt;revId&gt;-&lt;rev&gt;-&lt;ts&gt;.7z
    /// Vortex also downloads). Uses the downloadLink path the GraphQL revision already carries.
    /// Premium only — free users get 403 here too.
    /// </summary>
    public async Task<string?> GetCollectionArchiveLinkAsync(string downloadLinkPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(downloadLinkPath.TrimStart('/'), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) return null;
        await EnsureAsync(resp, $"GET {downloadLinkPath}");
        var d = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        return d.GetProperty("download_links")[0].GetProperty("URI").GetString();
    }

    // ---------- Files / downloads ----------

    /// <summary>
    /// Premium-only: temporary CDN download link for a mod file.
    /// Free users receive 403 from this endpoint — that is the signal to
    /// fall back to browser-assist mode.
    /// </summary>
    public async Task<string?> GetFileDownloadLinkAsync(string gameDomain, int modId, long fileId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return null; // not premium -> caller switches to browser-assist
        await EnsureAsync(resp, $"GET download link for {modId}/{fileId}");
        var links = await resp.Content.ReadFromJsonAsync<JsonElement>(_json, ct);
        // array of { name, short_name, URI } — prefer an explicit Premium mirror if present
        string? best = null;
        foreach (var l in links.EnumerateArray())
        {
            var uri = l.GetProperty("URI").GetString();
            var name = l.GetProperty("short_name").GetString() ?? "";
            if (uri is null) continue;
            if (name.Contains("Premium", StringComparison.OrdinalIgnoreCase)) return uri;
            best ??= uri;
        }
        return best;
    }

    /// <summary>Download to disk with SHA-256 verification. Returns actual hash.</summary>
    public async Task<string> DownloadFileAsync(string url, string destPath,
        string? expectedSha256 = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // only official Nexus hosts, HTTPS only — the URI comes from an API response,
        // so a compromised/malicious response must not redirect us elsewhere.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.EndsWith("nexus-cdn.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith("nexusmods.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"download refused: non-Nexus url '{url}'");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var tmp = destPath + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var total = resp.Content.Headers.ContentLength ?? -1;
            var buffer = new byte[1 << 16];
            long read = 0; int n;
            // A connection that goes quiet would hang this download for ever: give up after 60 s without a byte, as a
            // network error the caller retries (not as a cancel).
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                try { n = await src.ReadAsync(buffer, stall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("The download stopped receiving data."); }
                if (n <= 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report(read * 100.0 / total);
            }
        }
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);

        var hash = await Sha256Async(destPath, ct);
        if (expectedSha256 is not null &&
            !string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destPath);
            throw new InvalidOperationException($"Hash mismatch for {destPath}: expected {expectedSha256}, got {hash}");
        }
        return hash;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(fs, ct)).ToLowerInvariant();
    }

    private static async Task EnsureAsync(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        // Without the query string: download links carry signed parameters that don't belong in logs.
        throw new HttpRequestException($"{what} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} req={resp.RequestMessage?.Method} {resp.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path)}\n{body}");
    }

    public void Dispose() => _http.Dispose();
}
