using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent.Mcp;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent;

/// <summary>Installed / signed-in state of the Claude Code CLI.</summary>
internal sealed record ClaudeCodeStatus(string? ExecutablePath, string? Version, bool SignedIn, string? AuthMethod, string? Account)
{
    public bool Installed => this.ExecutablePath != null;

    public string Summary =>
        !this.Installed ? "Claude Code isn't installed."
        : !this.SignedIn ? $"Claude Code {this.Version} is installed but not signed in."
        : $"Signed in{(this.Account is null ? "" : " as " + this.Account)} ({DescribeAuth(this.AuthMethod)}) · Claude Code {this.Version}";

    private static string DescribeAuth(string? method) => method switch
    {
        "claude.ai" => "Claude subscription",
        "oauth_token" => "subscription token",
        "api_key" or "api_key_helper" => "API key",
        "third_party" => "cloud provider",
        _ => method ?? "unknown",
    };
}

/// <summary>
/// Finds, installs and signs in to the Claude Code CLI. Installing and signing in run Anthropic's own
/// installer and login flow in a visible window; this extension never stores Claude credentials
/// (<see cref="PlanUsageClient"/> only reads Claude Code's token to fetch plan usage).
/// </summary>
internal static class ClaudeCodeCli
{
    private const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>Finds claude.exe: PATH first, then the native installer's and WinGet's default locations.</summary>
    public static string? FindExecutable()
    {
        var candidates = new List<string>();
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
            }
            catch (ArgumentException)
            {
            }
        }

        // Visual Studio's PATH is fixed at launch, so look where the installers put it too.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(profile, @".local\bin\claude.exe"));
        candidates.Add(Path.Combine(localAppData, @"Microsoft\WinGet\Links\claude.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<ClaudeCodeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        string? exe = FindExecutable();
        if (exe is null)
        {
            return new ClaudeCodeStatus(null, null, false, null, null);
        }

        (_, string versionOutput) = await RunAsync(exe, ["--version"], cancellationToken).ConfigureAwait(false);
        string version = versionOutput.Trim().Split(' ').FirstOrDefault() ?? "?";

        (int exitCode, string statusJson) = await RunAsync(exe, ["auth", "status"], cancellationToken).ConfigureAwait(false);
        string? method = null;
        string? account = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(statusJson);
            method = FindString(doc.RootElement, "authMethod");
            account = FindString(doc.RootElement, "email") ?? FindString(doc.RootElement, "organizationName");
        }
        catch (JsonException)
        {
        }

        return new ClaudeCodeStatus(exe, version, exitCode == 0 && method is not null and not "none", method, account);
    }

    /// <summary>Runs Anthropic's official Windows installer in a visible PowerShell window and waits for it to close.</summary>
    public static Task InstallAsync()
    {
        string script = $"{InstallCommand}; Write-Host ''; Read-Host 'Finished. Press Enter to close this window'";
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
        {
            UseShellExecute = true,
        };
        return WaitForExitAsync(Process.Start(psi)!);
    }

    /// <summary>Runs `claude auth login` (Anthropic's browser sign-in) in a visible console window and waits for it.</summary>
    public static Task SignInAsync(string exe)
    {
        var psi = new ProcessStartInfo(exe, "auth login") { UseShellExecute = false };
        RemoveApiKeyVariables(psi);
        return WaitForExitAsync(Process.Start(psi)!);
    }

    /// <summary>
    /// Claude Code prefers ANTHROPIC_API_KEY over the subscription login when it's set, so child processes
    /// started for subscription use must not inherit it.
    /// </summary>
    public static void RemoveApiKeyVariables(ProcessStartInfo psi)
    {
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_AUTH_TOKEN");
    }

    // The npm package's "latest" tag is the newest release; the native installer's `claude update` installs the same versions.
    private const string LatestVersionUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The installed version, e.g. "2.1.296", or null if `claude --version` didn't say.</summary>
    public static async Task<string?> GetVersionAsync(string exe, CancellationToken cancellationToken)
    {
        (int exitCode, string output) = await RunAsync(exe, ["--version"], cancellationToken).ConfigureAwait(false);
        string version = output.Trim().Split(' ').FirstOrDefault() ?? string.Empty;
        return exitCode == 0 && version.Length > 0 && char.IsDigit(version[0]) ? version : null;
    }

    /// <summary>The newest published version, or null if it can't be found out (offline, say).</summary>
    public static async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(LatestVersionUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares dotted version numbers ("2.1.296" &lt; "2.1.300" &lt; "2.2.0"): positive if <paramref name="a"/> is
    /// newer. Anything after a part's digits (a "-beta" tag) is ignored.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string version) => version.Trim().Split('.')
            .Select(p => new string(p.TakeWhile(char.IsDigit).ToArray()))
            .Select(p => int.TryParse(p, out int n) ? n : 0)
            .ToArray();

        int[] x = Parts(a), y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int left = i < x.Length ? x[i] : 0, right = i < y.Length ? y[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    /// <summary>
    /// True if the user has turned Claude Code's updates off with DISABLE_AUTOUPDATER, in Visual Studio's environment
    /// or the "env" section of ~/.claude/settings.json; then updates are offered rather than installed.
    /// </summary>
    public static bool AutoUpdateDisabled()
    {
        static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value) && value!.Trim() is not "0" and not "false";

        if (IsSet(Environment.GetEnvironmentVariable("DISABLE_AUTOUPDATER")))
        {
            return true;
        }

        string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("env", out JsonElement env) && env.ValueKind == JsonValueKind.Object
                && env.TryGetProperty("DISABLE_AUTOUPDATER", out JsonElement value)
                && IsSet(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Runs `claude update` without a window. Returns its exit code and what it printed.</summary>
    public static async Task<(int ExitCode, string Output)> UpdateAsync(string exe, CancellationToken cancellationToken)
    {
        // It never asks anything, but don't let a stuck download hang the check forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        return await RunAsync(exe, ["update"], timeout.Token, includeStderr: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Every MCP server Claude Code connects to in the repository, with its health: the configured ones plus claude.ai
    /// connectors and plugins' servers, as `claude mcp list` prints them. It starts or connects to each, so it takes a while.
    /// </summary>
    public static async Task<IReadOnlyList<ReportedMcpServer>> ListMcpServersAsync(string exe, string repoRoot, CancellationToken cancellationToken)
    {
        (int exitCode, string output) = await RunAsync(exe, ["mcp", "list"], cancellationToken, workingDirectory: repoRoot).ConfigureAwait(false);
        return exitCode == 0 ? McpServerList.Parse(output) : throw new InvalidOperationException(output.Trim() is { Length: > 0 } o ? o : $"claude mcp list exited with code {exitCode}.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken cancellationToken, bool includeStderr = false, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(GitRunner.QuoteArgument)))
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        RemoveApiKeyVariables(psi);

        using var process = Process.Start(psi)!;
        if (args[0] != "update")
        {
            Processes.CloseWithVisualStudio(process); // an update is left to finish, rather than stopped half installed
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() => Processes.Kill(process)))
        {
            await WaitForExitAsync(process).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string text = await output.ConfigureAwait(false);
        if (includeStderr && (await errors.ConfigureAwait(false)).Trim() is { Length: > 0 } err)
        {
            text = text.TrimEnd() + "\n" + err;
        }

        return (process.ExitCode, text);
    }

    private static Task WaitForExitAsync(Process process)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(true);
        if (process.HasExited)
        {
            tcs.TrySetResult(true);
        }

        return tcs.Task;
    }

    /// <summary>Finds a string property anywhere in a JSON object (the status shape varies by version).</summary>
    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }

            if (FindString(property.Value, name) is string nested)
            {
                return nested;
            }
        }

        return null;
    }
}
