using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RepoBrowser;

/// <summary>Azure DevOps Services (<c>dev.azure.com</c>).</summary>
public sealed class AzureDevOpsClient(HttpClient http, CredentialResolver credentials) : HostingClient(http, credentials)
{
    private const string Host = "dev.azure.com";

    /// <inheritdoc />
    public override HostingProvider Kind => HostingProvider.AzureDevOps;

    /// <inheritdoc />
    public override async ValueTask<AccountCheck> CheckAsync(HostingAccount account, CancellationToken ct)
    {
        var token = await Credentials.ResolveAsync(account, ct);
        string? who = null;
        foreach (var organization in account.Owners)
        {
            var (body, _) = await GetAsync(new Uri($"https://{Host}/{Uri.EscapeDataString(organization)}/_apis/connectionData"), Host, token, ct);
            using (body)
            {
                if (body.RootElement.TryGetProperty("authenticatedUser", out var user))
                {
                    who ??= Text(user, "providerDisplayName") ?? Text(user, "customDisplayName");
                    if (who == "Anonymous")
                        return new AccountCheck(false, $"The organization {organization} answered as an anonymous user: the token was not accepted.");
                }
            }
        }

        return new AccountCheck(true, $"Signed in as {who ?? "?"} in {string.Join(", ", account.Owners)}.");
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<IReadOnlyList<RemoteRepository>> ListAsync(HostingAccount account, [EnumeratorCancellation] CancellationToken ct)
    {
        var token = await Credentials.ResolveAsync(account, ct);
        foreach (var organization in account.Owners)
        {
            var (body, _) = await GetAsync(new Uri($"https://{Host}/{Uri.EscapeDataString(organization)}/_apis/git/repositories?api-version=7.1"), Host, token, ct);
            var page = new List<RemoteRepository>();
            using (body)
            {
                if (body.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (Read(account, organization, item) is { } repository) page.Add(repository);
                    }
                }
            }

            yield return page.OrderBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static RemoteRepository? Read(HostingAccount account, string organization, JsonElement item)
    {
        var name = Text(item, "name");
        var project = item.TryGetProperty("project", out var projectElement) ? Text(projectElement, "name") : null;
        var web = Text(item, "webUrl");
        if (name is null || project is null || web is null) return null;
        if (Flag(item, "isDisabled")) return null;
        var https = $"https://{Host}/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(name)}";
        var branch = Text(item, "defaultBranch");
        if (branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true) branch = branch["refs/heads/".Length..];
        var owner = $"{organization}/{project}";
        return new RemoteRepository(account.Id, RepositoryKey.Join(Host, organization, project, name), owner, name, $"{owner}/{name}", null,
            IsPrivate: !(item.TryGetProperty("project", out var p) && Text(p, "visibility") == "public"), IsArchived: false, IsFork: Flag(item, "isFork"),
            DefaultBranch: branch, HttpsUrl: https, SshUrl: Text(item, "sshUrl"), WebUrl: web, UpdatedAt: null);
    }
}
