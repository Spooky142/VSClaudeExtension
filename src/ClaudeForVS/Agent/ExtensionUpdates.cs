using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    /// <summary>
    /// Opens Visual Studio's own extension installer on the .vsix. It shows what it will install, and finishes once
    /// Visual Studio has closed.
    /// </summary>
    public static void StartInstaller(string vsixPath)
    {
        // The installer of the Visual Studio that's running sits beside devenv.exe; double-clicking a .vsix picks one too.
        string? ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
        string installer = ide is null ? string.Empty : Path.Combine(ide, "VSIXInstaller.exe");
        ProcessStartInfo start = File.Exists(installer)
            ? new ProcessStartInfo(installer, "\"" + vsixPath + "\"") { UseShellExecute = false }
            : new ProcessStartInfo(vsixPath) { UseShellExecute = true };
        using Process? _ = Process.Start(start);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
