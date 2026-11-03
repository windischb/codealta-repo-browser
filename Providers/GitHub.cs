using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RepoBrowser;

/// <summary>GitHub and GitHub Enterprise.</summary>
public sealed class GitHubClient(HttpClient http, CredentialResolver credentials) : HostingClient(http, credentials)
{
    /// <inheritdoc />
    public override HostingProvider Kind => HostingProvider.GitHub;

    /// <inheritdoc />
    public override async ValueTask<AccountCheck> CheckAsync(HostingAccount account, CancellationToken ct)
    {
        var token = await Credentials.ResolveAsync(account, ct);
        if (token is null) return new AccountCheck(true, $"No sign-in: the public repositories of {string.Join(", ", account.Owners)} are listed.");
        var (body, headers) = await GetAsync(new Uri(ApiRoot(account), "user"), ApiHost(account), token, ct);
        using (body)
        {
            var login = Text(body.RootElement, "login") ?? "?";
            var scopes = headers.TryGetValues("X-OAuth-Scopes", out var values) ? string.Join(",", values) : null;
            return new AccountCheck(true, $"Signed in as {login}" + (string.IsNullOrWhiteSpace(scopes) ? "." : $" (scopes: {scopes})."));
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<IReadOnlyList<RemoteRepository>> ListAsync(HostingAccount account, [EnumeratorCancellation] CancellationToken ct)
    {
        var token = await Credentials.ResolveAsync(account, ct);
        var owners = new HashSet<string>(account.Owners, StringComparer.OrdinalIgnoreCase);
        var root = ApiRoot(account);
        IReadOnlyList<Uri> starts = token is null
            ? [.. account.Owners.Select(owner => new Uri(root, $"users/{Uri.EscapeDataString(owner)}/repos?per_page=100&type=owner&sort=full_name"))]
            : account.Owners.Count == 0
                ? [new Uri(root, "user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=full_name")]
                : await OwnerStartsAsync(account, token, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in starts)
        {
            Uri? next = start;
            for (var pages = 0; next is not null && pages < 100; pages++)
            {
                var (body, headers) = await GetAsync(next, ApiHost(account), token, ct);
                var page = new List<RemoteRepository>();
                using (body)
                {
                    foreach (var item in body.RootElement.EnumerateArray())
                    {
                        var repository = Read(account, item);
                        if (repository is null || !seen.Add(repository.Key)) continue;
                        if (owners.Count > 0 && !owners.Contains(repository.Owner)) continue;
                        page.Add(repository);
                    }
                }

                yield return page;
                next = NextLink(headers);
            }
        }
    }

    // Signed in with owners: the repositories of each owner, whether the user is a member or not (dotnet, a colleague's account), with
    // the higher limit of a signed-in account and the private ones the token may see. Filtering /user/repos would miss every owner the
    // user is not part of.
    private async Task<IReadOnlyList<Uri>> OwnerStartsAsync(HostingAccount account, AccountToken token, CancellationToken ct)
    {
        var root = ApiRoot(account);
        string? login;
        var (me, _) = await GetAsync(new Uri(root, "user"), ApiHost(account), token, ct);
        using (me) login = Text(me.RootElement, "login");

        var starts = new List<Uri>();
        foreach (var owner in account.Owners)
        {
            var name = Uri.EscapeDataString(owner);
            if (string.Equals(owner, login, StringComparison.OrdinalIgnoreCase))
            {
                starts.Add(new Uri(root, "user/repos?affiliation=owner&per_page=100&sort=full_name"));
                continue;
            }

            JsonDocument found;
            try { (found, _) = await GetAsync(new Uri(root, $"users/{name}"), ApiHost(account), token, ct); }
            catch (RepoBrowserException error) when (error.Code == "not_found")
            {
                throw new RepoBrowserException("not_found", $"{account.Host} has no user or organization named '{owner}'.");
            }

            using (found)
            {
                starts.Add(Text(found.RootElement, "type") == "Organization"
                    ? new Uri(root, $"orgs/{name}/repos?type=all&per_page=100&sort=full_name")
                    : new Uri(root, $"users/{name}/repos?type=owner&per_page=100&sort=full_name"));
            }
        }

        return starts;
    }

    /// <inheritdoc />
    protected override void Prepare(HttpRequestMessage request)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <inheritdoc />
    protected override async Task<RepoBrowserException> FailureAsync(HttpResponseMessage response, bool signedIn, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
        {
            var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("HH:mm") : "later";
            return new RepoBrowserException("rate_limited", $"GitHub's rate limit is reached; it resets at {reset}." + (signedIn ? "" : " Sign in for a higher limit."));
        }

        return await base.FailureAsync(response, signedIn, ct);
    }

    private static RemoteRepository? Read(HostingAccount account, JsonElement item)
    {
        var fullName = Text(item, "full_name");
        var name = Text(item, "name");
        var owner = item.TryGetProperty("owner", out var ownerElement) ? Text(ownerElement, "login") : null;
        var https = Text(item, "clone_url");
        if (fullName is null || name is null || owner is null || https is null) return null;
        return new RemoteRepository(account.Id, RepositoryKey.Join(account.Host, owner, name), owner, name, fullName, Text(item, "description"),
            Flag(item, "private"), Flag(item, "archived"), Flag(item, "fork"), Text(item, "default_branch"),
            https, Text(item, "ssh_url"), Text(item, "html_url") ?? $"https://{account.Host}/{fullName}", Date(item, "pushed_at") ?? Date(item, "updated_at"));
    }

    private static Uri? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(",", values).Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length < 2 || !pieces.Skip(1).Any(piece => piece.Trim() == "rel=\"next\"")) continue;
            return Uri.TryCreate(pieces[0].Trim().TrimStart('<').TrimEnd('>'), UriKind.Absolute, out var uri) ? uri : null;
        }

        return null;
    }

    private static string ApiHost(HostingAccount account) => account.Host == "github.com" ? "api.github.com" : account.Host.Split(':')[0];

    private static Uri ApiRoot(HostingAccount account)
        => account.Host == "github.com" ? new Uri("https://api.github.com/") : new Uri($"https://{account.Host}/api/v3/");
}
