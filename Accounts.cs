using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeAlta.Plugins.Abstractions;

namespace RepoBrowser;

/// <summary>The hosting services an account can be on. GitLab and Bitbucket are in the model only: nothing lists them yet.</summary>
public enum HostingProvider { GitHub, AzureDevOps, GitLab, Bitbucket }

/// <summary>Where the token of an account comes from. A token itself is never stored.</summary>
public enum CredentialKind { None, EnvironmentVariable, Cli }

/// <summary>How a repository is cloned.</summary>
public enum CloneProtocol { Https, Ssh }

/// <summary>An account of a hosting service: where it is, what to list, and how to authenticate.</summary>
public sealed record HostingAccount
{
    /// <summary>Gets the stable identifier, such as <c>github-work</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the name shown in the window.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the hosting service.</summary>
    public required HostingProvider Provider { get; init; }

    /// <summary>Gets the host: <c>github.com</c>, a GitHub Enterprise host, <c>dev.azure.com</c>.</summary>
    public required string Host { get; init; }

    /// <summary>Gets the owners to list: GitHub users or organizations (empty: what the token sees); Azure DevOps organizations (required).</summary>
    public IReadOnlyList<string> Owners { get; init; } = [];

    /// <summary>Gets where the token comes from.</summary>
    public CredentialSource Credential { get; init; } = CredentialSource.None;

    /// <summary>Gets how repositories are cloned.</summary>
    public CloneProtocol Protocol { get; init; } = CloneProtocol.Https;
}

/// <summary>The source of a token: nothing, the name of an environment variable, or a CLI profile.</summary>
public sealed record CredentialSource
{
    /// <summary>Gets the kind of source.</summary>
    public CredentialKind Kind { get; init; }

    /// <summary>Gets the name of the environment variable (never its value), for <see cref="CredentialKind.EnvironmentVariable"/>.</summary>
    public string? Variable { get; init; }

    /// <summary>Gets the variables the CLI runs with, such as <c>GH_CONFIG_DIR</c> or <c>AZURE_CONFIG_DIR</c>, for <see cref="CredentialKind.Cli"/>.</summary>
    public IReadOnlyDictionary<string, string> CliEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the source that gives no token.</summary>
    public static CredentialSource None { get; } = new();
}

/// <summary>An error the window shows as it is: a message for the user, never a token.</summary>
public sealed class RepoBrowserException(string code, string message) : Exception(message)
{
    /// <summary>Gets the code of the error (<c>a-z0-9_</c>).</summary>
    public string Code { get; } = code;
}

/// <summary>The JSON form of the plugin's records: camelCase, enums as camelCase strings.</summary>
public static class RepoJson
{
    /// <summary>Creates the options; each caller owns its instance.</summary>
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>Reads, checks and saves the accounts, in the plugin state of the user (<c>accounts.json</c>).</summary>
public sealed partial class AccountStore(IPluginStateStore state, JsonSerializerOptions json)
{
    private const string Name = "accounts";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<HostingAccount> _accounts = [];

    /// <summary>Gets the accounts as last read or saved.</summary>
    public IReadOnlyList<HostingAccount> Accounts => Volatile.Read(ref _accounts);

    /// <summary>Reads the accounts from the plugin state; a file that cannot be read gives none.</summary>
    public async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        var element = await state.ReadJsonAsync<JsonElement?>(PluginStateScope.User, Name, cancellationToken);
        IReadOnlyList<HostingAccount> accounts = [];
        if (element is { ValueKind: JsonValueKind.Array } array)
        {
            try { accounts = array.Deserialize<List<HostingAccount>>(json) ?? []; }
            catch (JsonException) { accounts = []; }
        }

        Volatile.Write(ref _accounts, accounts);
    }

    /// <summary>Finds an account by its identifier.</summary>
    /// <exception cref="RepoBrowserException">No account has this identifier.</exception>
    public HostingAccount Get(string? id)
        => Accounts.FirstOrDefault(account => string.Equals(account.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? throw new RepoBrowserException("unknown_account", $"No account is named '{id}'. Add it in Settings › Repositories.");

    /// <summary>Checks and saves the whole list of accounts.</summary>
    /// <exception cref="RepoBrowserException">An account is not valid; the message says which and why.</exception>
    public async ValueTask<IReadOnlyList<HostingAccount>> SaveAsync(IReadOnlyList<HostingAccount>? accounts, CancellationToken cancellationToken)
    {
        var normalized = Validate(accounts ?? []);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await state.WriteJsonAsync(PluginStateScope.User, Name, JsonSerializer.SerializeToElement(normalized, json), cancellationToken);
            Volatile.Write(ref _accounts, normalized);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks a list of accounts and returns it trimmed, with the defaults filled in.</summary>
    /// <exception cref="RepoBrowserException">An account is not valid.</exception>
    public static IReadOnlyList<HostingAccount> Validate(IReadOnlyList<HostingAccount> accounts)
    {
        var result = new List<HostingAccount>(accounts.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var given in accounts)
        {
            var account = Normalize(given);
            if (!ids.Add(account.Id)) Fail(account, "another account has the same id.");
            result.Add(account);
        }

        return result;
    }

    /// <summary>Checks one account and returns it trimmed, with the defaults filled in.</summary>
    /// <exception cref="RepoBrowserException">The account is not valid.</exception>
    public static HostingAccount Normalize(HostingAccount? given)
    {
        if (given is null) throw new RepoBrowserException("invalid_account", "An account is empty.");
        var id = (given.Id ?? "").Trim();
        if (!IdPattern().IsMatch(id)) Fail(given, "the id is 1 to 64 lower-case letters, digits, '.', '_' or '-', starting with a letter or a digit.");
        var host = (given.Host ?? "").Trim().TrimEnd('/');
        if (host.Length == 0) host = given.Provider == HostingProvider.AzureDevOps ? "dev.azure.com" : given.Provider == HostingProvider.GitHub ? "github.com" : "";
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host["https://".Length..];
        host = host.ToLowerInvariant();
        if (!HostPattern().IsMatch(host)) Fail(given, "the host is a name such as github.com, without a path.");
        if (given.Provider == HostingProvider.AzureDevOps && host != "dev.azure.com") Fail(given, "Azure DevOps accounts are on dev.azure.com.");
        var owners = (given.Owners ?? []).Select(owner => owner.Trim().Trim('/')).Where(owner => owner.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (owners.Any(owner => !OwnerPattern().IsMatch(owner))) Fail(given, "an owner is a name of letters, digits, '.', '_' or '-'.");
        if (given.Provider == HostingProvider.AzureDevOps && owners.Count == 0) Fail(given, "name at least one organization.");

        var credential = given.Credential ?? CredentialSource.None;
        switch (credential.Kind)
        {
            case CredentialKind.EnvironmentVariable:
                var variable = (credential.Variable ?? "").Trim();
                if (!VariablePattern().IsMatch(variable)) Fail(given, "the token variable is the name of an environment variable, such as GITHUB_TOKEN.");
                credential = new CredentialSource { Kind = CredentialKind.EnvironmentVariable, Variable = variable };
                break;
            case CredentialKind.Cli:
                var environment = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in credential.CliEnvironment ?? new Dictionary<string, string>())
                {
                    var key = name.Trim();
                    if (key.Length == 0 && string.IsNullOrWhiteSpace(value)) continue;
                    if (!VariablePattern().IsMatch(key)) Fail(given, $"'{key}' is not the name of an environment variable.");
                    if (SecretWords().IsMatch(key)) Fail(given, $"'{key}' looks like a secret: a CLI profile names folders (GH_CONFIG_DIR, AZURE_CONFIG_DIR), never a token.");
                    environment[key] = (value ?? "").Trim();
                }

                credential = new CredentialSource { Kind = CredentialKind.Cli, CliEnvironment = environment };
                break;
            default:
                if (given.Provider == HostingProvider.AzureDevOps) Fail(given, "Azure DevOps needs a sign-in: choose a token variable or the Azure CLI.");
                if (owners.Count == 0) Fail(given, "without a credential, name the owners whose public repositories are listed.");
                credential = CredentialSource.None;
                break;
        }

        var displayName = string.IsNullOrWhiteSpace(given.DisplayName) ? id : given.DisplayName.Trim();
        return new HostingAccount
        {
            Id = id, DisplayName = displayName, Provider = given.Provider, Host = host, Owners = owners, Credential = credential, Protocol = given.Protocol,
        };
    }

    [DoesNotReturn]
    private static void Fail(HostingAccount account, string why)
        => throw new RepoBrowserException("invalid_account", $"Account '{(string.IsNullOrWhiteSpace(account.DisplayName) ? account.Id : account.DisplayName)}': {why}");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:\d{1,5})?$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex VariablePattern();

    [GeneratedRegex("TOKEN|PAT$|_PAT_|SECRET|PASSWORD|PASSWD|_KEY$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWords();
}

/// <summary>A token of an account and how it is sent.</summary>
/// <param name="Value">The token. It is kept in memory only.</param>
/// <param name="IsPersonalAccessToken">True for an Azure DevOps personal access token, which is sent as Basic with an empty user.</param>
public sealed record AccountToken(string Value, bool IsPersonalAccessToken)
{
    /// <summary>Gets the value of the Authorization header of an API call.</summary>
    public string ApiAuthorization => IsPersonalAccessToken ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Value)) : "Bearer " + Value;

    /// <inheritdoc />
    public override string ToString() => "AccountToken(***)";
}

/// <summary>
/// Resolves the token of an account from its source when it is needed, and keeps it in memory for a few minutes.
/// A token is never written, never logged and never part of a message.
/// </summary>
public sealed class CredentialResolver
{
    private const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] TokenVariables = ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"];
    private readonly ConcurrentDictionary<string, (AccountToken Token, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    /// <summary>Forgets every token, such as after the accounts changed.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Gets the token of an account, or null when it signs in with nothing (public repositories only).</summary>
    /// <exception cref="RepoBrowserException">The source gives no token; the message says why.</exception>
    public async ValueTask<AccountToken?> ResolveAsync(HostingAccount account, CancellationToken cancellationToken)
    {
        var key = account.Id + "\n" + Fingerprint(account);
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Token;

        var token = await ReadAsync(account, cancellationToken);
        if (token is not null) _cache[key] = (token, DateTimeOffset.UtcNow + Lifetime);
        return token;
    }

    private static async ValueTask<AccountToken?> ReadAsync(HostingAccount account, CancellationToken cancellationToken)
    {
        var credential = account.Credential;
        switch (credential.Kind)
        {
            case CredentialKind.EnvironmentVariable:
            {
                var value = Environment.GetEnvironmentVariable(credential.Variable ?? "");
                if (string.IsNullOrWhiteSpace(value))
                    throw new RepoBrowserException("no_credential", $"The variable {credential.Variable} is not set in the environment of CodeAlta. Set it and start CodeAlta again, or sign in with the CLI.");
                return new AccountToken(value.Trim(), account.Provider == HostingProvider.AzureDevOps);
            }

            case CredentialKind.Cli when account.Provider == HostingProvider.GitHub:
            {
                var value = await ReadCliTokenAsync(account, "gh", ["auth", "token", "--hostname", account.Host], TimeSpan.FromSeconds(5),
                    "Sign in with `gh auth login --hostname " + account.Host + "`", cancellationToken);
                return new AccountToken(value, false);
            }

            case CredentialKind.Cli when account.Provider == HostingProvider.AzureDevOps:
            {
                // The Azure CLI starts a Python interpreter: it needs more time than gh.
                var value = await ReadCliTokenAsync(account, "az",
                    ["account", "get-access-token", "--resource", AzureDevOpsResource, "--query", "accessToken", "--output", "tsv"], TimeSpan.FromSeconds(30),
                    "Sign in with `az login`", cancellationToken);
                return new AccountToken(value, false);
            }

            case CredentialKind.Cli:
                throw new RepoBrowserException("not_supported", $"{account.Provider} accounts are not supported yet.");

            default:
                if (account.Provider == HostingProvider.AzureDevOps)
                    throw new RepoBrowserException("no_credential", "Azure DevOps needs a sign-in: choose a token variable or the Azure CLI in Settings › Repositories.");
                return null;
        }
    }

    private static async ValueTask<string> ReadCliTokenAsync(HostingAccount account, string cli, IReadOnlyList<string> arguments, TimeSpan timeout, string signIn, CancellationToken cancellationToken)
    {
        if (CliLocator.Find(cli) is not { } command)
            throw new RepoBrowserException("cli_missing", $"`{cli}` is not installed. Set a token variable or sign in.");

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        // The profile of the account decides, not a token the application happens to have.
        foreach (var name in TokenVariables) environment[name] = null;
        foreach (var (name, value) in account.Credential.CliEnvironment) environment[name] = PathText.Expand(value);
        var result = await ProcessRunner.RunAsync(command, arguments, null, environment, timeout, null, cancellationToken);
        var token = result.ExitCode == 0 ? result.Stdout.Trim() : "";
        if (result.TimedOut) throw new RepoBrowserException("cli_timeout", $"`{cli}` did not answer within {timeout.TotalSeconds:0} seconds.");
        if (token.Length == 0 || token.Contains('\n'))
        {
            var why = result.ExitCode != 0 ? ProcessRunner.Bound(result.Stderr, 300) : "it printed no token";
            throw new RepoBrowserException("not_signed_in", $"`{cli}` gave no token ({why}). {signIn}{ProfileHint(account)}.");
        }

        return token;
    }

    private static string ProfileHint(HostingAccount account)
        => account.Credential.CliEnvironment.Count == 0 ? "" : " with " + string.Join(", ", account.Credential.CliEnvironment.Select(pair => $"{pair.Key}={pair.Value}"));

    private static string Fingerprint(HostingAccount account)
        => $"{account.Provider}|{account.Host}|{account.Credential.Kind}|{account.Credential.Variable}|" +
           string.Join(";", account.Credential.CliEnvironment.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
}

/// <summary>Proposes accounts from the identity of the space the window shows.</summary>
public static class SpaceImport
{
    /// <summary>The accounts the CLI profiles of an identity give; Azure DevOps proposals have no organization yet.</summary>
    public static IReadOnlyList<HostingAccount> Propose(PluginIdentity identity, string spaceName)
    {
        var proposals = new List<HostingAccount>();
        var suffix = Slug(identity.SpaceId ?? spaceName);
        if (identity.Environment.Get("GH_CONFIG_DIR") is { Length: > 0 } ghConfig)
        {
            var host = identity.Environment.Get("GH_HOST") is { Length: > 0 } ghHost ? ghHost.Trim().ToLowerInvariant() : "github.com";
            var environment = new Dictionary<string, string> { ["GH_CONFIG_DIR"] = ghConfig };
            if (host != "github.com") environment["GH_HOST"] = host;
            proposals.Add(new HostingAccount
            {
                Id = $"github-{suffix}", DisplayName = $"GitHub ({spaceName})", Provider = HostingProvider.GitHub, Host = host,
                Credential = new CredentialSource { Kind = CredentialKind.Cli, CliEnvironment = environment },
            });
        }

        if (identity.Environment.Get("AZURE_CONFIG_DIR") is { Length: > 0 } azConfig)
        {
            proposals.Add(new HostingAccount
            {
                Id = $"ado-{suffix}", DisplayName = $"Azure DevOps ({spaceName})", Provider = HostingProvider.AzureDevOps, Host = "dev.azure.com",
                Credential = new CredentialSource { Kind = CredentialKind.Cli, CliEnvironment = new Dictionary<string, string> { ["AZURE_CONFIG_DIR"] = azConfig } },
            });
        }

        return proposals;
    }

    private static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "space" : slug[..Math.Min(slug.Length, 40)];
    }
}

/// <summary>Paths as the user writes them: <c>~</c> is the home folder.</summary>
public static class PathText
{
    /// <summary>Replaces a leading <c>~</c> with the home folder.</summary>
    public static string Expand(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") return home;
        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
    }
}

/// <summary>Finds a program by its bare name on the PATH without starting it (<c>gh.exe</c>, <c>az.cmd</c> on Windows).</summary>
public static class CliLocator
{
    /// <summary>A program to start: the file, and the arguments that come before the caller's.</summary>
    public sealed record CliCommand(string FileName, IReadOnlyList<string> LeadingArguments);

    /// <summary>Finds a program on the PATH of the application; null when it is not there.</summary>
    public static CliCommand? Find(string name)
    {
        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(searchPath)) return null;
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(PathText.Expand(directory), name + extension)); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { break; }
                if (!File.Exists(candidate)) continue;
                if (extension is not (".cmd" or ".bat")) return new CliCommand(candidate, []);
                // A batch file parses its command line again; the Azure CLI's az.cmd only starts the Python beside it.
                if (!string.Equals(name, "az", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(candidate) is not { } folder) continue;
                var python = Path.GetFullPath(Path.Combine(folder, "..", "python.exe"));
                if (File.Exists(python)) return new CliCommand(python, ["-IBm", "azure.cli"]);
            }
        }

        return null;
    }
}

/// <summary>Runs a program with an argument list (no shell), with a timeout and an environment of its own.</summary>
public static class ProcessRunner
{
    /// <summary>What a program did.</summary>
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>Runs a program to its end.</summary>
    /// <param name="command">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="workingDirectory">The folder it runs in; null for the current one.</param>
    /// <param name="environment">Variables to set (a null value removes one).</param>
    /// <param name="timeout">How long it may run; null for no limit.</param>
    /// <param name="onStderrLine">Called with each line or progress segment of the error output; null to only collect it.</param>
    /// <param name="cancellationToken">Stops the program.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled; the program was killed.</exception>
    public static async Task<Result> RunAsync(CliLocator.CliCommand command, IReadOnlyList<string> arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment, TimeSpan? timeout, Action<string>? onStderrLine, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in command.LeadingArguments) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null) start.Environment.Remove(name);
                else start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        using var timeoutSource = timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = ReadErrorAsync(process.StandardError, onStderrLine, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new Result(process.ExitCode, await stdoutTask, await stderrTask, false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            return new Result(-1, "", "", true);
        }
    }

    /// <summary>Cuts a text to a length, on one line.</summary>
    public static string Bound(string text, int length)
    {
        var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= length ? line : line[..length] + "…";
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                all.Append(character);
                // git writes its progress with carriage returns: each segment is a line of progress.
                if (character is '\r' or '\n')
                {
                    if (line.Length > 0) onLine?.Invoke(line.ToString());
                    line.Clear();
                }
                else line.Append(character);
            }

            if (all.Length > 64 * 1024) all.Remove(0, all.Length - 32 * 1024);
        }

        if (line.Length > 0) onLine?.Invoke(line.ToString());
        return all.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
