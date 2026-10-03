using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
/// installer and login flow in a visible window; this extension never sees or stores Claude credentials.
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

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(GitRunner.QuoteArgument)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        RemoveApiKeyVariables(psi);

        using var process = Process.Start(psi)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        using (cancellationToken.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } }))
        {
            await WaitForExitAsync(process).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, await output.ConfigureAwait(false));
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
