using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace RepoBrowser;

/// <summary>Where a repository of a list is on this computer.</summary>
public sealed record RepositoryStatus(string Key, string State, string Target, string? ProjectPath, string? Message);

/// <summary>A clone that runs or ran, as the window shows it.</summary>
public sealed record CloneSnapshot(string Key, string AccountId, string FullName, string State, string Target, string? Progress, string? Message, bool ProjectAdded);

/// <summary>A space of CodeAlta.</summary>
public sealed record SpaceItem(string Id, string Name, bool Current);

/// <summary>
/// The repositories the projects of CodeAlta are clones of: the key of each project's <c>origin</c>. It is read in background work at the
/// start, after each clone, and again at a status when the last read is older than half a minute; never on the path of a list.
/// </summary>
public sealed class ProjectIndex(IPluginServices services)
{
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string> _byKey = new Dictionary<string, string>();
    private Task _refresh = Task.CompletedTask;
    // Environment.TickCount64 counts from the start of the system: 0 is "long ago", and a difference with it cannot overflow
    // (long.MinValue did, so a first read that failed at the start was never tried again).
    private long _readAt;

    /// <summary>Gets the path of the project that is a clone of a repository, or null.</summary>
    public string? Find(string key) => Volatile.Read(ref _byKey).TryGetValue(key, out var path) ? path : null;

    /// <summary>Reads the projects again, after the read that runs (which may not see a project just added).</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return _refresh = AfterAsync(_refresh, cancellationToken);
    }

    /// <summary>Waits for the read that runs, if any; starts one when the last read is older than <paramref name="maximumAge"/>.</summary>
    public Task WhenReadAsync(CancellationToken cancellationToken, TimeSpan? maximumAge = null)
    {
        Task current;
        lock (_gate)
        {
            // A project removed or added in the window since the last read is seen at the next status.
            if (maximumAge is { } age && _refresh.IsCompleted && Environment.TickCount64 - Volatile.Read(ref _readAt) > (long)age.TotalMilliseconds)
                _refresh = AfterAsync(_refresh, CancellationToken.None);
            current = _refresh;
        }

        return current.WaitAsync(cancellationToken);
    }

    private async Task AfterAsync(Task previous, CancellationToken cancellationToken)
    {
        await previous;
        await Task.Yield();
        try { await ReadAsync(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The index stays as it was: rows show what was known, and the next read tries again.
            services.Logger.Warn(error, "The origins of the projects could not be read.");
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var result = await services.Alta.InvokeAsync(["project", "list", "--all"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0 || CliLocator.Find("git") is not { } git) return;
        var paths = new List<string>();
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array) continue;
            foreach (var project in projects.EnumerateArray())
            {
                if (project.ValueKind == JsonValueKind.Array && project.GetArrayLength() >= 2 && project[1].GetString() is { } path) paths.Add(path);
            }
        }

        var byKey = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (path, token) =>
        {
            if (!Directory.Exists(path)) return;
            if (await Git.OriginKeyAsync(git, path, token) is { } key) byKey.TryAdd(key, path);
        });
        Volatile.Write(ref _byKey, new Dictionary<string, string>(byKey, StringComparer.Ordinal));
        Volatile.Write(ref _readAt, Environment.TickCount64);
    }
}


/// <summary>The git commands the plugin runs.</summary>
public static class Git
{
    /// <summary>The key of the <c>origin</c> of a folder, or null.</summary>
    public static async Task<string?> OriginKeyAsync(CliLocator.CliCommand git, string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(git, ["-C", path, "remote", "get-url", "origin"], path, Quiet, TimeSpan.FromSeconds(5), null, cancellationToken);
        return result.ExitCode == 0 ? RepositoryKey.FromRemote(result.Stdout.Trim()) : null;
    }

    /// <summary>The variables that keep git from asking anything.</summary>
    public static IReadOnlyDictionary<string, string?> Quiet { get; } = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" };
}

/// <summary>
/// Decides where a repository goes, finds what is there already, clones it with git and adds it as a project.
/// A clone runs as background work of the plugin, so a tab that is hidden or closed does not stop it; Cancel does.
/// </summary>
public sealed partial class CloneService(IPluginServices services, Action<string, Func<CancellationToken, ValueTask>> run, CredentialResolver credentials, ProjectIndex projects)
{
    private readonly ConcurrentDictionary<string, CloneJob> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _watchGate = new();
    private readonly List<Channel<IReadOnlyList<CloneSnapshot>>> _watchers = [];

    /// <summary>Gets the folder repositories are cloned into.</summary>
    public string CloneRoot => Path.GetFullPath(PathText.Expand(services.Settings.GetString("clone_root") is { Length: > 0 } root ? root : "~/git"));

    /// <summary>Gets the folder a repository is cloned into.</summary>
    /// <exception cref="RepoBrowserException">The name of the repository cannot be a folder name.</exception>
    public string TargetOf(RemoteRepository repository)
    {
        var root = CloneRoot;
        var segments = new List<string>();
        if (services.Settings.GetString("layout") == "owner") segments.AddRange(repository.Owner.Split('/'));
        segments.Add(repository.Name);
        foreach (var segment in segments)
        {
            if (!SafeName().IsMatch(segment) || segment is "." or "..")
                throw new RepoBrowserException("bad_name", $"'{segment}' cannot be the name of a folder.");
        }

        var target = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new RepoBrowserException("bad_name", $"'{repository.FullName}' would be cloned outside {root}.");
        return target;
    }

    /// <summary>Says for each repository whether it is a project, on disk, in the way of a folder, or not here.</summary>
    public async Task<IReadOnlyList<RepositoryStatus>> StatusAsync(IEnumerable<RemoteRepository> repositories, CancellationToken cancellationToken)
    {
        await projects.WhenReadAsync(cancellationToken, TimeSpan.FromSeconds(30));
        var git = CliLocator.Find("git");
        var statuses = new List<RepositoryStatus>();
        foreach (var repository in repositories)
        {
            string target;
            try { target = TargetOf(repository); }
            catch (RepoBrowserException error) { statuses.Add(new RepositoryStatus(repository.Key, "conflict", "", null, error.Message)); continue; }

            if (projects.Find(repository.Key) is { } project)
            {
                statuses.Add(new RepositoryStatus(repository.Key, "project", target, project, null));
                continue;
            }

            var (state, message) = await InspectAsync(git, target, repository.Key, cancellationToken);
            statuses.Add(new RepositoryStatus(repository.Key, state, target, null, message));
        }

        return statuses;
    }

    /// <summary>Gets the clones that run or ran since the start.</summary>
    public IReadOnlyList<CloneSnapshot> Snapshots() => [.. _jobs.Values.Select(job => job.Snapshot()).OrderBy(job => job.FullName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Starts a clone, or gives the one that runs for the same repository.</summary>
    public CloneJob Start(HostingAccount account, RemoteRepository repository, string spaceId)
    {
        var target = TargetOf(repository);
        if (_jobs.TryGetValue(repository.Key, out var running) && running.IsRunning) return running;
        var job = new CloneJob(account, repository, target, spaceId, Changed);
        _jobs[repository.Key] = job;
        run($"clone {repository.FullName}", token => new ValueTask(RunAsync(job, token)));
        Changed();
        return job;
    }

    /// <summary>Cancels the clone of a repository.</summary>
    public bool Cancel(string key) => _jobs.TryGetValue(key, out var job) && job.Cancel();

    /// <summary>The clones after each change; the first item is the list now.</summary>
    public async IAsyncEnumerable<IReadOnlyList<CloneSnapshot>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<IReadOnlyList<CloneSnapshot>>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_watchGate) _watchers.Add(channel);
        channel.Writer.TryWrite(Snapshots());
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally
        {
            lock (_watchGate) _watchers.Remove(channel);
        }
    }

    private void Changed()
    {
        var snapshot = Snapshots();
        lock (_watchGate)
        {
            foreach (var watcher in _watchers) watcher.Writer.TryWrite(snapshot);
        }
    }

    private async Task RunAsync(CloneJob job, CancellationToken pluginToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pluginToken, job.CancelToken);
        var cancellationToken = linked.Token;
        var created = false;
        try
        {
            if (CliLocator.Find("git") is not { } git) throw new RepoBrowserException("git_missing", "git is not installed or not on the PATH of CodeAlta.");
            await projects.WhenReadAsync(cancellationToken, TimeSpan.FromSeconds(30));
            if (projects.Find(job.Repository.Key) is { } project)
            {
                job.Finish("done", $"{job.Repository.FullName} is already the project {project}: nothing was cloned.", false);
                return;
            }

            var (state, message) = await InspectAsync(git, job.Target, job.Repository.Key, cancellationToken);
            if (state == "conflict") throw new RepoBrowserException("conflict", message ?? "The folder is in the way.");
            if (state == "none")
            {
                created = !Directory.Exists(job.Target);
                Directory.CreateDirectory(Path.GetDirectoryName(job.Target)!);
                job.Update("cloning", $"Cloning into {job.Target}…", null);
                var url = job.Account.Protocol == CloneProtocol.Ssh && job.Repository.SshUrl is { } ssh ? ssh : job.Repository.HttpsUrl;
                var environment = new Dictionary<string, string?>(Git.Quiet);
                if (job.Account.Protocol == CloneProtocol.Https && await credentials.ResolveAsync(job.Account, cancellationToken) is { } token)
                {
                    // The token reaches git only through the environment of this child, as a header for the account's host:
                    // it is not in the URL, the arguments or .git/config.
                    environment["GIT_CONFIG_COUNT"] = "1";
                    environment["GIT_CONFIG_KEY_0"] = $"http.https://{new Uri(url).Authority}/.extraHeader";
                    environment["GIT_CONFIG_VALUE_0"] = "Authorization: " + GitAuthorization(job.Account, token);
                }

                var result = await ProcessRunner.RunAsync(git, ["clone", "--progress", "--", url, job.Target], CloneRoot, environment, null,
                    line => job.Progress(line), cancellationToken);
                if (result.ExitCode != 0)
                    throw new RepoBrowserException("clone_failed", "git clone failed: " + ProcessRunner.Bound(LastLines(result.Stderr), 400));
                created = false;
            }
            else
            {
                job.Update("adding", $"{job.Target} is already a clone of {job.Repository.FullName}: it is only added.", null);
            }

            job.Update("adding", "Adding the project…", null);
            var add = await services.Alta.InvokeAsync(["project", "add", job.Target, "--space", job.SpaceId], cancellationToken: cancellationToken);
            if (add.ExitCode != 0)
                throw new RepoBrowserException("project_add_failed", $"Cloned into {job.Target}, but adding the project failed: {add.Error ?? ProcessRunner.Bound(add.TranscriptJsonl, 300)}");
            await projects.RefreshAsync(CancellationToken.None);
            job.Finish("done", state == "none" ? $"Cloned into {job.Target} and added to the space." : $"Added {job.Target} to the space.", true);
        }
        catch (OperationCanceledException)
        {
            Remove(job.Target, created);
            job.Finish("cancelled", "Cancelled." + (created ? " The partial folder was removed." : ""), false);
        }
        catch (RepoBrowserException error)
        {
            Remove(job.Target, created);
            job.Finish("failed", error.Message, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Remove(job.Target, created);
            job.Finish("failed", error.Message, false);
        }
    }

    private static string GitAuthorization(HostingAccount account, AccountToken token)
        => account.Provider == HostingProvider.GitHub
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token.Value))
            : token.ApiAuthorization;

    private static async Task<(string State, string? Message)> InspectAsync(CliLocator.CliCommand? git, string target, string key, CancellationToken cancellationToken)
    {
        if (File.Exists(target)) return ("conflict", $"{target} is a file.");
        if (!Directory.Exists(target)) return ("none", null);
        if (!Directory.EnumerateFileSystemEntries(target).Any()) return ("none", null);
        if (Directory.Exists(Path.Combine(target, ".git")) || File.Exists(Path.Combine(target, ".git")))
        {
            var origin = git is null ? null : await Git.OriginKeyAsync(git, target, cancellationToken);
            if (origin == key) return ("onDisk", null);
            return ("conflict", origin is null ? $"{target} is a git folder without the same origin." : $"{target} is a clone of another repository ({origin}).");
        }

        return ("conflict", $"{target} exists and holds other files: nothing is overwritten.");
    }

    private static void Remove(string target, bool created)
    {
        if (!created || !Directory.Exists(target)) return;
        try
        {
            // git marks the objects read-only on some systems: clear the marks before deleting.
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(target, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string LastLines(string text)
        => string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains('%')).TakeLast(3));

    [GeneratedRegex(@"^[A-Za-z0-9._ \-]{1,200}$")]
    private static partial Regex SafeName();
}

/// <summary>One clone: its state, and the progress of git.</summary>
public sealed class CloneJob(HostingAccount account, RemoteRepository repository, string target, string spaceId, Action changed)
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<CloneSnapshot> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private string _state = "starting";
    private string? _progress;
    private string? _message;
    private bool _projectAdded;
    private long _lastProgress;

    /// <summary>Gets the account.</summary>
    public HostingAccount Account { get; } = account;

    /// <summary>Gets the repository.</summary>
    public RemoteRepository Repository { get; } = repository;

    /// <summary>Gets the folder.</summary>
    public string Target { get; } = target;

    /// <summary>Gets the space the project is added to.</summary>
    public string SpaceId { get; } = spaceId;

    /// <summary>Gets the token that Cancel cancels.</summary>
    public CancellationToken CancelToken => _cancel.Token;

    /// <summary>Gets a value indicating whether the clone has not ended.</summary>
    public bool IsRunning => !_done.Task.IsCompleted;

    /// <summary>Gets the end of the clone.</summary>
    public Task<CloneSnapshot> Completion => _done.Task;

    /// <summary>Asks the clone to stop.</summary>
    public bool Cancel()
    {
        if (!IsRunning) return false;
        _cancel.Cancel();
        return true;
    }

    /// <summary>The clone as the window shows it.</summary>
    public CloneSnapshot Snapshot()
    {
        lock (_gate) return new CloneSnapshot(Repository.Key, Account.Id, Repository.FullName, _state, Target, _progress, _message, _projectAdded);
    }

    /// <summary>Sets the state and the message.</summary>
    public void Update(string state, string? message, string? progress)
    {
        lock (_gate) { _state = state; _message = message; _progress = progress; }
        changed();
    }

    /// <summary>Takes a line of git's progress, at most a few times a second.</summary>
    public void Progress(string line)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            _progress = line.Trim();
            if (now - _lastProgress < 250) return;
            _lastProgress = now;
        }

        changed();
    }

    /// <summary>Ends the clone.</summary>
    public void Finish(string state, string message, bool projectAdded)
    {
        lock (_gate) { _state = state; _message = message; _progress = null; _projectAdded = projectAdded; }
        changed();
        _done.TrySetResult(Snapshot());
    }
}
