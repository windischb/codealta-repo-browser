using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RepoBrowser;

/// <summary>A repository of an account, as the hosting service lists it.</summary>
public sealed record RemoteRepository(
    string AccountId, string Key, string Owner, string Name, string FullName, string? Description,
    bool IsPrivate, bool IsArchived, bool IsFork, string? DefaultBranch,
    string HttpsUrl, string? SshUrl, string WebUrl, DateTimeOffset? UpdatedAt);

/// <summary>What a check of an account found: who it signs in as, or why it does not.</summary>
public sealed record AccountCheck(bool Ok, string Message);

/// <summary>Lists the repositories of one hosting service.</summary>
public interface IHostingProvider
{
    /// <summary>Gets the service.</summary>
    HostingProvider Kind { get; }

    /// <summary>Says who the account signs in as, or why it does not.</summary>
    ValueTask<AccountCheck> CheckAsync(HostingAccount account, CancellationToken ct);

    /// <summary>Lists the repositories of the account, a page at a time.</summary>
    /// <exception cref="RepoBrowserException">The service refused or failed; the message says why.</exception>
    IAsyncEnumerable<IReadOnlyList<RemoteRepository>> ListAsync(HostingAccount account, CancellationToken ct);
}

/// <summary>The identity of a repository across URLs: <c>host/owner/name</c> in lower case, the same for HTTPS and SSH remotes.</summary>
public static class RepositoryKey
{
    /// <summary>The key of a remote URL, or null when it names no repository of a known form.</summary>
    public static string? FromRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        string host, path;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            host = uri.Host;
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            // scp form: git@github.com:owner/name.git
            var colon = text.IndexOf(':');
            if (colon <= 0) return null;
            host = text[..colon];
            var at = host.LastIndexOf('@');
            if (at >= 0) host = host[(at + 1)..];
            path = text[(colon + 1)..];
        }

        host = host.ToLowerInvariant();
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Azure DevOps: dev.azure.com/org/project/_git/repo, ssh.dev.azure.com:v3/org/project/repo, org.visualstudio.com/project/_git/repo
        if (host == "ssh.dev.azure.com" && parts.Length == 4 && parts[0] == "v3") return Join("dev.azure.com", parts[1], parts[2], parts[3]);
        if (host == "dev.azure.com" && parts.Length == 4 && parts[2] == "_git") return Join(host, parts[0], parts[1], parts[3]);
        if (host == "dev.azure.com" && parts.Length == 3 && parts[1] == "_git") return Join(host, parts[0], parts[2], parts[2]);
        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var organization = host[..^".visualstudio.com".Length];
            if (parts.Length == 3 && parts[1] == "_git") return Join("dev.azure.com", organization, parts[0], parts[2]);
            if (parts.Length == 4 && parts[0] == "defaultcollection" && parts[2] == "_git") return Join("dev.azure.com", organization, parts[1], parts[3]);
            if (parts.Length == 2 && parts[0] == "_git") return Join("dev.azure.com", organization, parts[1], parts[1]);
            return null;
        }

        return parts.Length >= 2 ? Join([host, .. parts]) : null;
    }

    /// <summary>Joins the segments of a key.</summary>
    public static string Join(params string[] parts) => string.Join('/', parts).ToLowerInvariant();
}

/// <summary>The calls both clients make: JSON, the Authorization header only to the account's own host, clear messages.</summary>
public abstract class HostingClient(HttpClient http, CredentialResolver credentials) : IHostingProvider
{
    /// <inheritdoc />
    public abstract HostingProvider Kind { get; }

    /// <summary>Gets the resolver of tokens.</summary>
    protected CredentialResolver Credentials { get; } = credentials;

    /// <inheritdoc />
    public abstract ValueTask<AccountCheck> CheckAsync(HostingAccount account, CancellationToken ct);

    /// <inheritdoc />
    public abstract IAsyncEnumerable<IReadOnlyList<RemoteRepository>> ListAsync(HostingAccount account, CancellationToken ct);

    /// <summary>Sends a GET and returns the JSON body and the response headers.</summary>
    /// <exception cref="RepoBrowserException">The answer is not a success.</exception>
    protected async Task<(JsonDocument Body, HttpResponseHeaders Headers)> GetAsync(Uri uri, string allowedHost, AccountToken? token, CancellationToken ct)
    {
        if (!string.Equals(uri.Host, allowedHost, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
            throw new RepoBrowserException("bad_link", $"The service sent a link to another host ({uri.Host}); it is not followed.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodeAlta-RepoBrowser/1.0");
        Prepare(request);
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", token.ApiAuthorization);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException exception) { throw new RepoBrowserException("network", $"{uri.Host} could not be reached: {exception.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new RepoBrowserException("network", $"{uri.Host} did not answer in time."); }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, token is not null, ct);
            var media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!media.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new RepoBrowserException("unauthorized", $"{uri.Host} answered with a page instead of data: the sign-in was probably refused.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return (await JsonDocument.ParseAsync(stream, cancellationToken: ct), response.Headers);
        }
    }

    /// <summary>Adds the headers of the service.</summary>
    protected virtual void Prepare(HttpRequestMessage request) { }

    /// <summary>Turns a failed answer into a message.</summary>
    protected virtual async Task<RepoBrowserException> FailureAsync(HttpResponseMessage response, bool signedIn, CancellationToken ct)
    {
        var body = "";
        try { body = ProcessRunner.Bound(await response.Content.ReadAsStringAsync(ct), 200); } catch (HttpRequestException) { }
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new RepoBrowserException("unauthorized", signedIn ? "The token was refused (401). Sign in again or check the token." : "This service needs a sign-in (401)."),
            HttpStatusCode.Forbidden => new RepoBrowserException("forbidden", $"Access was refused (403). {body}"),
            HttpStatusCode.NotFound => new RepoBrowserException("not_found", "Not found (404): check the host and the owners of the account."),
            _ => new RepoBrowserException("http_error", $"The service answered {(int)response.StatusCode} {response.ReasonPhrase}. {body}"),
        };
    }

    /// <summary>Reads a text property, or null.</summary>
    protected static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Reads a Boolean property, false when it is missing.</summary>
    protected static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Reads a date property, or null.</summary>
    protected static DateTimeOffset? Date(JsonElement element, string name)
        => Text(element, name) is { } text && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null;
}
