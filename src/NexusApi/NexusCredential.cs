namespace AStoryWealth.NexusApi;

/// <summary>Thrown when Nexus no longer accepts the user's sign-in (revoked, or the refresh token ran out). The user has to sign in again.</summary>
public sealed class NexusSignedOutException(string? message = null) : Exception(message ?? "Not signed in to Nexus Mods");

/// <summary>How a request to the Nexus API proves who the user is. The API client asks for it on every request, so a token that
/// expires during a long install is renewed in time.</summary>
public abstract class NexusCredential
{
    public abstract Task ApplyAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>Strings that must never appear in a report or log.</summary>
    public virtual IEnumerable<string> Secrets => [];
}

/// <summary>A personal API key. Only for developers (the ASW_NEXUS_KEY variable); the app itself signs in with OAuth.</summary>
public sealed class ApiKeyCredential(string key) : NexusCredential
{
    public override Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove("apikey");
        request.Headers.TryAddWithoutValidation("apikey", key);
        return Task.CompletedTask;
    }

    public override IEnumerable<string> Secrets => [key];
}

/// <summary>Adds the credential to requests for the Nexus API host only, never to a CDN or any other address.</summary>
public sealed class CredentialHandler(NexusCredential credential, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is { } u && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase))
            await credential.ApplyAsync(request, ct);
        return await base.SendAsync(request, ct);
    }
}
