#!/usr/bin/env dotnet
#:include Accounts.cs
#:include Providers/Hosting.cs
#:include Providers/GitHub.cs
#:include Providers/AzureDevOps.cs
#:include Cloning.cs
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using RepoBrowser;

// Repositories: lists the repositories of your hosting accounts (GitHub, GitHub Enterprise, Azure DevOps) in a tab, clones one with git
// and adds it as a project of a space. The accounts name where a token comes from (a variable, a CLI profile), never a token.
[Plugin("repo-browser", DisplayName = "Repositories", Description = "Browse the repositories of your GitHub and Azure DevOps accounts, clone one and add it as a project.",
    Frontends = PluginFrontends.Desktop)]
public sealed class RepoBrowserPlugin : PluginBase
{
    private const string AgentsCanvasDescription =
        "The repositories of the user's hosting accounts (GitHub, Azure DevOps): list them, clone one into the clone folder and add it as a project of a space.";

    private readonly JsonSerializerOptions _json = RepoJson.Create();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CredentialResolver _credentials = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<RemoteRepository>> _lists = new(StringComparer.OrdinalIgnoreCase);
    private AccountStore? _accounts;
    private ProjectIndex? _projects;
    private CloneService? _clones;
    private Dictionary<HostingProvider, IHostingProvider> _providers = [];

    private AccountStore Accounts => _accounts ?? throw new InvalidOperationException("The plugin is not initialized.");

    private CloneService Clones => _clones ?? throw new InvalidOperationException("The plugin is not initialized.");

    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _accounts = new AccountStore(Services.State, _json);
        await _accounts.LoadAsync(cancellationToken);
        _projects = new ProjectIndex(Services);
        _clones = new CloneService(Services, (name, work) => Services.Tasks.Run(name, work), _credentials, _projects);
        _providers = new()
        {
            [HostingProvider.GitHub] = new GitHubClient(_http, _credentials),
            [HostingProvider.AzureDevOps] = new AzureDevOpsClient(_http, _credentials),
        };
    }

    public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        Services.Tasks.Run("read the origins of the projects", async token => await _projects!.RefreshAsync(token));
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _http.Dispose();
        return base.DisposeAsync();
    }

    public override IEnumerable<PluginSettingContribution> GetSettings()
    {
        yield return Setting.Path("clone_root", "Clone folder", "The folder repositories are cloned into.", "~/git") with { Keywords = ["git", "clone", "folder"] };
        yield return Setting.Choice("layout", "Folder layout", "flat: <clone folder>/<repository>; owner: <clone folder>/<owner>/<repository>.", ["flat", "owner"], "flat");
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.Button(PluginButtonPlace.TitleBar, "repositories", icon: "folder-git-2", label: "Repositories") with { Canvas = "repositories" };
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("repos", "Opens the repositories of your accounts.", async (_, cancellationToken) =>
        {
            await Services.Canvases.OpenAsync("repositories", cancellationToken: cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Repositories: open" };
    }

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "settings", Title = "Repositories", Icon = "folder-git-2", Placement = PluginCanvasPlacement.Settings,
            Description = "The hosting accounts whose repositories the Repositories tab lists, and where their tokens come from.",
            Open = (canvas, _) =>
            {
                canvas.Rpc.JsonOptions = _json;
                canvas.Rpc.Handle<NoInput, IReadOnlyList<HostingAccount>>("accounts.get", (_, _) => ValueTask.FromResult(Accounts.Accounts));
                canvas.Rpc.Handle<SaveAccounts, IReadOnlyList<HostingAccount>>("accounts.save", (request, ct) => Guard(() => SaveAccountsAsync(request?.Accounts, ct)));
                canvas.Rpc.Handle<CheckRequest, AccountCheck>("accounts.check", (request, ct) => CheckAsync(request?.Account, ct));
                canvas.Rpc.Handle<NoInput, ImportResult>("accounts.import", (_, ct) => Guard(() => ImportAsync(canvas.SpaceId, ct)));
                return ValueTask.FromResult(PluginCanvasView.Html("<p class=\"alta-muted\">Loading…</p>") with { Script = PluginScript.File("ui/settings.js") });
            },
        };

        yield return new PluginCanvasContribution
        {
            Id = "repositories", Title = "Repositories", Icon = "folder-git-2", Scope = PluginCanvasScope.Application,
            Description = AgentsCanvasDescription,
            Open = (canvas, _) =>
            {
                canvas.Rpc.JsonOptions = _json;
                canvas.Rpc.Handle<NoInput, TabState>("tab.get", (_, ct) => Guard(() => TabStateAsync(canvas.SpaceId, ct)));
                canvas.Rpc.Stream<LoadRequest, LoadItem>("repos.load", (request, ct) => LoadStreamAsync(request ?? new LoadRequest(), ct));
                canvas.Rpc.Handle<StatusRequest, IReadOnlyList<RepositoryStatus>>("repos.status", (request, ct) => Guard(() => StatusAsync(request?.AccountId, ct)));
                canvas.Rpc.Handle<CloneRequest, CloneSnapshot>("repos.clone", (request, ct) => Guard(() => StartCloneAsync(request, canvas.SpaceId, ct)));
                canvas.Rpc.Handle<CancelRequest, bool>("repos.cancel", (request, _) => ValueTask.FromResult(Clones.Cancel(request?.Key ?? "")));
                canvas.Rpc.Stream<NoInput, IReadOnlyList<CloneSnapshot>>("clones.watch", (_, ct) => Clones.WatchAsync(ct));
                return ValueTask.FromResult(PluginCanvasView.Html("<p class=\"alta-muted\">Loading…</p>") with { Script = PluginScript.File("ui/repositories.js") });
            },
            Describe = (_, _) => ValueTask.FromResult<string?>(DescribeMarkdown()),
            Actions =
            [
                new PluginCanvasActionContribution
                {
                    Name = "accounts", Description = "Lists the hosting accounts: id, provider, host and owners. No token is ever part of it.",
                    Handler = (_, _, _) => ValueTask.FromResult<JsonElement?>(ToJson(new { accounts = Accounts.Accounts.Select(AccountSummary) })),
                },
                new PluginCanvasActionContribution
                {
                    Name = "list", Description = "Lists the repositories of an account with where each is on this computer (project, onDisk, conflict, none); filters by text and owner.",
                    InputSchema = """
                        { "type": "object", "properties": {
                            "account": { "type": "string", "description": "The id of the account (see the accounts action)." },
                            "query": { "type": "string", "description": "Only the repositories whose name or description has this text." },
                            "owner": { "type": "string", "description": "Only the repositories of this owner (GitHub user or organization, Azure DevOps org/project)." },
                            "includeArchived": { "type": "boolean", "description": "Also list archived repositories. False by default." },
                            "refresh": { "type": "boolean", "description": "Read the list from the service again instead of the one kept in memory." },
                            "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "The most repositories to return; 100 by default." } },
                          "required": ["account"], "additionalProperties": false }
                        """,
                    Handler = (_, input, ct) => Act(() => ListActionAsync(input, ct)),
                },
                new PluginCanvasActionContribution
                {
                    Name = "clone", Description = "Clones a repository of an account into the clone folder (or uses the clone already there) and adds it as a project of a space; waits for the end.",
                    InputSchema = """
                        { "type": "object", "properties": {
                            "account": { "type": "string", "description": "The id of the account." },
                            "repository": { "type": "string", "description": "The full name of the repository, such as owner/name (Azure DevOps: org/project/name), or its name when it is unique." },
                            "space": { "type": "string", "description": "The id or name of the space the project goes to; the space the window shows by default." } },
                          "required": ["account", "repository"], "additionalProperties": false }
                        """,
                    Handler = (canvas, input, ct) => Act(() => CloneActionAsync(input, canvas.SpaceId, ct)),
                },
            ],
        };
    }

    // Accounts

    private async ValueTask<IReadOnlyList<HostingAccount>> SaveAccountsAsync(IReadOnlyList<HostingAccount>? accounts, CancellationToken ct)
    {
        var saved = await Accounts.SaveAsync(accounts, ct);
        // A changed account may list other repositories, with another token: both are read again.
        _credentials.Clear();
        _lists.Clear();
        return saved;
    }

    private async ValueTask<AccountCheck> CheckAsync(HostingAccount? given, CancellationToken ct)
    {
        try
        {
            var account = AccountStore.Normalize(given);
            return await ProviderOf(account).CheckAsync(account, ct);
        }
        catch (RepoBrowserException error)
        {
            return new AccountCheck(false, error.Message);
        }
    }

    private async ValueTask<ImportResult> ImportAsync(string? canvasSpaceId, CancellationToken ct)
    {
        var identity = await Services.Identity.GetIdentityAsync(null, ct);
        var spaces = await SpacesAsync(ct);
        var spaceId = identity.SpaceId ?? spaces.FirstOrDefault(item => item.Current)?.Id ?? canvasSpaceId;
        var space = spaces.FirstOrDefault(item => item.Id == spaceId);
        var name = space?.Name ?? spaceId ?? "space";
        var proposals = SpaceImport.Propose(identity with { SpaceId = spaceId }, name)
            .Where(proposal => Accounts.Accounts.All(account => !string.Equals(account.Id, proposal.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new ImportResult(spaceId, name, proposals);
    }

    // Lists

    private async ValueTask<TabState> TabStateAsync(string? canvasSpaceId, CancellationToken ct)
    {
        var spaces = await SpacesAsync(ct);
        var current = spaces.FirstOrDefault(space => space.Current)?.Id ?? canvasSpaceId;
        return new TabState([.. Accounts.Accounts.Select(AccountSummary)], spaces, current, Clones.CloneRoot, Services.Settings.GetString("layout") ?? "flat");
    }

    private async IAsyncEnumerable<LoadItem> LoadStreamAsync(LoadRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        HostingAccount? account = null;
        IHostingProvider? provider = null;
        LoadItem? refused = null;
        try
        {
            account = Accounts.Get(request.AccountId);
            provider = ProviderOf(account);
        }
        catch (RepoBrowserException error)
        {
            refused = LoadItem.Failed(error);
        }

        if (refused is not null || account is null || provider is null)
        {
            yield return refused!;
            yield break;
        }

        if (!request.Refresh && _lists.TryGetValue(account.Id, out var cached))
        {
            foreach (var chunk in cached.Chunk(200)) yield return new LoadItem("page", chunk, cached.Count, null, null);
            yield return new LoadItem("done", [], cached.Count, null, null);
            yield break;
        }

        var all = new List<RemoteRepository>();
        await using var pages = provider.ListAsync(account, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            LoadItem? failure = null;
            var more = false;
            try { more = await pages.MoveNextAsync(); }
            catch (RepoBrowserException error) { failure = LoadItem.Failed(error); }

            if (failure is not null)
            {
                yield return failure;
                yield break;
            }

            if (!more) break;
            all.AddRange(pages.Current);
            yield return new LoadItem("page", pages.Current, all.Count, null, null);
        }

        _lists[account.Id] = all;
        yield return new LoadItem("done", [], all.Count, null, null);
    }

    private async ValueTask<IReadOnlyList<RemoteRepository>> ListAsync(HostingAccount account, bool refresh, CancellationToken ct)
    {
        if (!refresh && _lists.TryGetValue(account.Id, out var cached)) return cached;
        var all = new List<RemoteRepository>();
        await foreach (var page in ProviderOf(account).ListAsync(account, ct)) all.AddRange(page);
        _lists[account.Id] = all;
        return all;
    }

    private async ValueTask<IReadOnlyList<RepositoryStatus>> StatusAsync(string? accountId, CancellationToken ct)
    {
        var account = Accounts.Get(accountId);
        return _lists.TryGetValue(account.Id, out var list) ? await Clones.StatusAsync(list, ct) : [];
    }

    // Clones

    private async ValueTask<CloneSnapshot> StartCloneAsync(CloneRequest? request, string? canvasSpaceId, CancellationToken ct)
    {
        var account = Accounts.Get(request?.AccountId);
        var list = await ListAsync(account, false, ct);
        var repository = list.FirstOrDefault(item => item.Key == request?.Key)
                         ?? throw new RepoBrowserException("not_found", "This repository is not in the list of the account any more: refresh the list.");
        var spaceId = await SpaceOrCurrentAsync(request?.SpaceId ?? canvasSpaceId, ct);
        return Clones.Start(account, repository, spaceId).Snapshot();
    }

    private async ValueTask<object> ListActionAsync(JsonElement? input, CancellationToken ct)
    {
        var account = Accounts.Get(Read(input, "account"));
        var list = await ListAsync(account, ReadBool(input, "refresh"), ct);
        var query = Read(input, "query");
        var owner = Read(input, "owner");
        var includeArchived = ReadBool(input, "includeArchived");
        var limit = input is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var given)
            ? Math.Clamp(given, 1, 500) : 100;
        var matches = list
            .Where(item => includeArchived || !item.IsArchived)
            .Where(item => owner is null || string.Equals(item.Owner, owner, StringComparison.OrdinalIgnoreCase))
            .Where(item => query is null || item.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        var shown = matches.Take(limit).ToList();
        var statuses = (await Clones.StatusAsync(shown, ct)).ToDictionary(status => status.Key, StringComparer.Ordinal);
        return new
        {
            account = account.Id,
            total = list.Count,
            matching = matches.Count,
            repositories = shown.Select(item => new
            {
                item.FullName, item.Owner, item.Name, item.Description, @private = item.IsPrivate, archived = item.IsArchived, fork = item.IsFork, item.WebUrl,
                status = statuses[item.Key].State, path = statuses[item.Key].ProjectPath ?? statuses[item.Key].Target, message = statuses[item.Key].Message,
            }),
        };
    }

    private async ValueTask<object> CloneActionAsync(JsonElement? input, string? canvasSpaceId, CancellationToken ct)
    {
        var account = Accounts.Get(Read(input, "account"));
        var name = Read(input, "repository") ?? throw new RepoBrowserException("invalid_input", "Name the repository.");
        var list = await ListAsync(account, false, ct);
        var repository = list.FirstOrDefault(item => string.Equals(item.FullName, name, StringComparison.OrdinalIgnoreCase));
        if (repository is null)
        {
            var byName = list.Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            repository = byName.Count switch
            {
                1 => byName[0],
                0 => throw new RepoBrowserException("not_found", $"The account {account.Id} has no repository '{name}'."),
                _ => throw new RepoBrowserException("ambiguous", $"Several repositories are named '{name}': {string.Join(", ", byName.Select(item => item.FullName))}. Give the full name."),
            };
        }

        var spaceId = await SpaceOrCurrentAsync(Read(input, "space") ?? canvasSpaceId, ct);
        var snapshot = await Clones.Start(account, repository, spaceId).Completion.WaitAsync(ct);
        return new { snapshot.State, snapshot.FullName, path = snapshot.Target, snapshot.Message, snapshot.ProjectAdded, space = spaceId };
    }

    // Helpers

    private string DescribeMarkdown()
    {
        var builder = new StringBuilder("# Repositories\n\n");
        if (Accounts.Accounts.Count == 0) builder.Append("No account yet: add one in Settings › Repositories.\n");
        foreach (var account in Accounts.Accounts)
        {
            var loaded = _lists.TryGetValue(account.Id, out var list) ? $"{list.Count} repositories loaded" : "not loaded";
            builder.Append($"- `{account.Id}`: {account.DisplayName}, {account.Provider} on {account.Host}" +
                           (account.Owners.Count > 0 ? $", owners {string.Join(", ", account.Owners)}" : "") + $" ({loaded})\n");
        }

        builder.Append($"\nClone folder: `{Clones.CloneRoot}` ({Services.Settings.GetString("layout") ?? "flat"}).\n");
        foreach (var clone in Clones.Snapshots()) builder.Append($"- clone {clone.FullName}: {clone.State} {clone.Message}\n");
        return builder.ToString();
    }

    private IHostingProvider ProviderOf(HostingAccount account)
        => _providers.TryGetValue(account.Provider, out var provider) ? provider : throw new RepoBrowserException("not_supported", $"{account.Provider} accounts are not supported yet.");

    private async ValueTask<IReadOnlyList<SpaceItem>> SpacesAsync(CancellationToken ct)
    {
        var result = await Services.Alta.InvokeAsync(["space", "list"], cancellationToken: ct);
        var spaces = new List<SpaceItem>();
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "alta.space.item" && root.TryGetProperty("id", out var id))
            {
                spaces.Add(new SpaceItem(id.GetString() ?? "", root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    root.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.True));
            }
        }

        return spaces;
    }

    private async ValueTask<string> SpaceOrCurrentAsync(string? spaceId, CancellationToken ct)
    {
        var spaces = await SpacesAsync(ct);
        if (spaceId is not null)
            return spaces.FirstOrDefault(space => string.Equals(space.Id, spaceId, StringComparison.OrdinalIgnoreCase) || string.Equals(space.Name, spaceId, StringComparison.OrdinalIgnoreCase))?.Id
                   ?? throw new RepoBrowserException("unknown_space", $"No space is named '{spaceId}'.");
        return spaces.FirstOrDefault(space => space.Current)?.Id ?? "default";
    }

    private static object AccountSummary(HostingAccount account)
        => new { account.Id, account.DisplayName, account.Provider, account.Host, account.Owners, credential = account.Credential.Kind, account.Protocol };

    private static async ValueTask<T> Guard<T>(Func<ValueTask<T>> work)
    {
        try { return await work(); }
        catch (RepoBrowserException error) { throw new PluginRpcException(error.Code, error.Message); }
    }

    private async ValueTask<JsonElement?> Act(Func<ValueTask<object>> work)
    {
        try { return ToJson(await work()); }
        catch (RepoBrowserException error) { return ToJson(new { error = error.Code, message = error.Message }); }
    }

    private JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, _json);

    private static string? Read(JsonElement? input, string name)
        => input is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
           && property.GetString() is { Length: > 0 } text ? text.Trim() : null;

    private static bool ReadBool(JsonElement? input, string name)
        => input is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}

public sealed record NoInput;

public sealed record SaveAccounts(IReadOnlyList<HostingAccount>? Accounts);

public sealed record CheckRequest(HostingAccount? Account);

public sealed record ImportResult(string? SpaceId, string SpaceName, IReadOnlyList<HostingAccount> Proposals);

public sealed record TabState(IReadOnlyList<object> Accounts, IReadOnlyList<SpaceItem> Spaces, string? CurrentSpaceId, string CloneRoot, string Layout);

public sealed record LoadRequest(string? AccountId = null, bool Refresh = false);

public sealed record LoadItem(string Kind, IReadOnlyList<RemoteRepository> Repositories, int Count, string? Code, string? Message)
{
    public static LoadItem Failed(RepoBrowserException error) => new("error", [], 0, error.Code, error.Message);
}

public sealed record StatusRequest(string? AccountId);

public sealed record CloneRequest(string? AccountId, string? Key, string? SpaceId);

public sealed record CancelRequest(string? Key);
