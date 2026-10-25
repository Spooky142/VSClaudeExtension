using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>A published version of this extension: its number, its .vsix and its release page.</summary>
internal sealed record ExtensionRelease(string Version, string VsixUrl, string PageUrl);

/// <summary>
/// This extension's own updates, from the GitHub releases of its repository (published by the Build workflow for each
/// version tag): finding a newer one, downloading its .vsix and handing it to Visual Studio's installer.
/// </summary>
internal static class ExtensionUpdates
{
    /// <summary>The GitHub repository the releases come from.</summary>
    public const string Repository = "Spooky142/VSClaudeExtension";

    private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    // Downloading the .vsix (a few MB) can take longer than asking about it.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>The installed version, e.g. "0.17.0".</summary>
    public static string InstalledVersion
    {
        get
        {
            Version version = typeof(ExtensionUpdates).Assembly.GetName().Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>The newest release, or null if it can't be found out (offline, GitHub's rate limit, no release with a .vsix).</summary>
    public static async Task<ExtensionRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("ClaudeForVS/" + InstalledVersion); // GitHub refuses requests without one
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? ParseRelease(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A release from GitHub's API: its tag (without the "v") and its .vsix; null if it isn't one, or has no .vsix.</summary>
    internal static ExtensionRelease? ParseRelease(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "tag_name") is not string tag
                || (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string version = tag.TrimStart('v', 'V');
            string? vsix = assets.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object && Str(a, "name")?.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase) == true)
                .Select(a => Str(a, "browser_download_url"))
                .FirstOrDefault(url => url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true);
            return version.Length > 0 && char.IsDigit(version[0]) && vsix != null
                ? new ExtensionRelease(version, vsix, Str(root, "html_url") ?? $"https://github.com/{Repository}/releases")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True if the release is newer than what's installed.</summary>
    public static bool IsNewer(ExtensionRelease release) => ClaudeCodeCli.CompareVersions(release.Version, InstalledVersion) > 0;

    /// <summary>Downloads the release's .vsix to a temporary folder and returns its path.</summary>
    public static async Task<string> DownloadAsync(ExtensionRelease release, CancellationToken cancellationToken)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ClaudeForVS", "updates");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"ClaudeForVS-{release.Version}.vsix");
        string partial = path + ".download";

        using var request = new HttpRequestMessage(HttpMethod.Get, release.VsixUrl);
        request.Headers.UserAgent.ParseAdd("ClaudeForVS/" + InstalledVersion);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (FileStream file = File.Create(partial))
        {
            await body.CopyToAsync(file, 81920, cancellationToken).ConfigureAwait(false);
        }

        // Only a complete download takes the final name, so a half-written one is never installed.
        File.Delete(path);
        File.Move(partial, path);
        return path;
    }

    // Run by a hidden PowerShell that outlives Visual Studio: waits for it to close, installs the .vsix and opens it again.
    private const string RestartScript = """
        param([int]$VsPid, [string]$Installer, [string]$Vsix, [string]$Devenv, [string]$Solution = '', [string]$RootSuffix = '')

        # Install whenever Visual Studio closes (closing can be cancelled at the save prompt), but only open it again if
        # that was soon after the update was asked for, not hours later.
        $started = Get-Date
        $vs = Get-Process -Id $VsPid -ErrorAction SilentlyContinue
        if ($vs) { $vs.WaitForExit() }
        $reopen = ((Get-Date) - $started).TotalMinutes -lt 10

        # The installer can't run while another window of the same Visual Studio is open.
        function Others { @(Get-Process devenv -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Devenv }) }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Others).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }

        $install = @()
        if ($RootSuffix) { $install += "/rootSuffix:$RootSuffix" }
        $install += "`"$Vsix`""
        $code = 1
        if ((Others).Count -eq 0) { $code = (Start-Process $Installer -ArgumentList (@('/quiet') + $install) -Wait -PassThru).ExitCode }

        # 1001: it's already installed. Otherwise the installer's own window says what's in the way and lets it finish.
        if ($code -ne 0 -and $code -ne 1001) { Start-Process $Installer -ArgumentList $install -Wait }

        if ($reopen) {
            $open = @()
            if ($RootSuffix) { $open += '/rootsuffix', $RootSuffix }
            if ($Solution) { $open += "`"$Solution`"" }
            if ($open.Count -gt 0) { Start-Process $Devenv -ArgumentList $open } else { Start-Process $Devenv }
        }
        """;

    /// <summary>
    /// Starts a hidden helper that, once Visual Studio has closed, installs the .vsix with Visual Studio's own installer
    /// and opens Visual Studio again on the solution. Close Visual Studio after calling this.
    /// </summary>
    /// <param name="solution">The solution (or folder) to open again; null for none.</param>
    public static void StartRestartingInstall(string vsixPath, string? solution)
    {
        using Process current = Process.GetCurrentProcess();
        string devenv = current.MainModule?.FileName ?? throw new InvalidOperationException("Couldn't find Visual Studio's devenv.exe.");

        // The installer of the Visual Studio that's running sits beside devenv.exe.
        string installer = Path.Combine(Path.GetDirectoryName(devenv)!, "VSIXInstaller.exe");
        if (!File.Exists(installer))
        {
            throw new InvalidOperationException("Couldn't find Visual Studio's VSIXInstaller.exe.");
        }

        string script = Path.Combine(Path.GetDirectoryName(vsixPath)!, "restart-and-install.ps1");
        File.WriteAllText(script, RestartScript);

        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string arguments = RestartArguments(script, current.Id, installer, vsixPath, devenv, solution, RootSuffix(Environment.GetCommandLineArgs()));
        using Process? _ = Process.Start(new ProcessStartInfo(powershell, arguments) { UseShellExecute = false, CreateNoWindow = true });
    }

    /// <summary>PowerShell's command line for the restart script.</summary>
    internal static string RestartArguments(string script, int vsPid, string installer, string vsix, string devenv, string? solution, string? rootSuffix)
    {
        var args = new StringBuilder("-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden");
        args.Append(" -File ").Append(Quote(script));
        args.Append(" -VsPid ").Append(vsPid);
        args.Append(" -Installer ").Append(Quote(installer));
        args.Append(" -Vsix ").Append(Quote(vsix));
        args.Append(" -Devenv ").Append(Quote(devenv));
        if (!string.IsNullOrEmpty(solution))
        {
            args.Append(" -Solution ").Append(Quote(solution!));
        }

        if (!string.IsNullOrEmpty(rootSuffix))
        {
            args.Append(" -RootSuffix ").Append(Quote(rootSuffix!));
        }

        return args.ToString();
    }

    /// <summary>The hive Visual Studio was started with (e.g. "Exp" for the experimental instance), or null.</summary>
    internal static string? RootSuffix(string[] commandLine)
    {
        for (int i = 0; i < commandLine.Length - 1; i++)
        {
            if (commandLine[i].TrimStart('/', '-').Equals("rootsuffix", StringComparison.OrdinalIgnoreCase))
            {
                return commandLine[i + 1];
            }
        }

        return null;
    }

    // Paths can't contain quotes, and a trailing backslash would escape the closing one.
    private static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
