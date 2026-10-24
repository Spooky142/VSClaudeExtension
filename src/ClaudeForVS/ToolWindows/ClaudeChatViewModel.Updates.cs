using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>A small note above the transcript, e.g. "Claude Code 2.1.300 is available", with an optional button.</summary>
internal sealed class NoticeBanner(string key, string text, string? actionText = null, Action? action = null)
{
    /// <summary>Identifies what the banner is about, so dismissing it hides it in every chat window.</summary>
    public string Key { get; } = key;

    public string Text { get; } = text;

    public string ActionText { get; } = actionText ?? string.Empty;

    public bool HasAction => action != null;

    public void Run() => action?.Invoke();
}

/// <summary>Checking for Claude Code updates (and installing them) and for newer model versions on the Claude API.</summary>
internal sealed partial class ClaudeChatViewModel
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    // Showing a chat window checks again, but not every time one is switched to.
    private static readonly TimeSpan ShownCheckInterval = TimeSpan.FromMinutes(15);

    private enum CodeUpdateState
    {
        Available,
        Updating,
        Updated,
        Failed,
    }

    private sealed record CodeUpdate(CodeUpdateState State, string Installed, string Latest, string Detail = "");

    private enum ExtensionUpdateState
    {
        Available,
        Downloading,
        Installing,
        Failed,
    }

    private sealed record ExtensionUpdate(ExtensionUpdateState State, ExtensionRelease Release, string Detail = "");

    // Shared by every chat window: a check when one is shown and then daily, one update at a time, and a banner dismissed in one window goes from all.
    private static DateTime lastUpdateCheckUtc = DateTime.MinValue;
    private static bool checkingForUpdates;
    private static bool installingUpdate;
    private static CodeUpdate? codeUpdate;
    private static ExtensionUpdate? extensionUpdate;
    private static readonly HashSet<string> DismissedBanners = new(StringComparer.Ordinal);

    public ObservableCollection<NoticeBanner> Banners { get; } = [];

    /// <summary>True when a day has passed since the last check (for Visual Studio left open), and none is running.</summary>
    public bool UpdateCheckDue => !checkingForUpdates && DateTime.UtcNow - lastUpdateCheckUtc >= UpdateCheckInterval;

    /// <summary>Checks for a newer Claude Code (installing it when no chat is busy), newer API models and a newer version of this extension. Never throws.</summary>
    /// <param name="shown">A chat window has been shown: check if the last check was more than a few minutes ago, rather than a day.</param>
    public async Task CheckForUpdatesAsync(bool shown = false)
    {
        if (checkingForUpdates || DateTime.UtcNow - lastUpdateCheckUtc < (shown ? ShownCheckInterval : UpdateCheckInterval))
        {
            return;
        }

        checkingForUpdates = true;
        lastUpdateCheckUtc = DateTime.UtcNow;
        try
        {
            await CheckExtensionAsync();
            await CheckApiModelsAsync();
            await CheckClaudeCodeAsync();
        }
        finally
        {
            checkingForUpdates = false;
        }
    }

    public void DismissBanner(NoticeBanner banner)
    {
        DismissedBanners.Add(banner.Key);
        RefreshAllBanners();
    }

    private static async Task CheckClaudeCodeAsync()
    {
        if (installingUpdate || ClaudeCodeCli.FindExecutable() is not string exe)
        {
            return;
        }

        string? installed, latest;
        try
        {
            installed = await ClaudeCodeCli.GetVersionAsync(exe, CancellationToken.None);
            latest = await ClaudeCodeCli.GetLatestVersionAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return;
        }

        if (installed is null || latest is null || ClaudeCodeCli.CompareVersions(latest, installed) <= 0)
        {
            return;
        }

        SetCodeUpdate(new CodeUpdate(CodeUpdateState.Available, installed, latest));
        if (!ClaudeCodeCli.AutoUpdateDisabled())
        {
            // It can wait a long while for the chats to finish; later checks shouldn't wait on it.
            _ = InstallUpdateAsync(exe, installed, latest);
        }
    }

    /// <summary>Runs `claude update` once no chat is in the middle of a reply, and reports how it went in the banner.</summary>
    private static async Task InstallUpdateAsync(string exe, string installed, string latest)
    {
        if (installingUpdate)
        {
            return;
        }

        installingUpdate = true;
        try
        {
            // Claude Code processes left idle between messages are fine, but don't swap it out under a running reply.
            while (Instances.Any(chat => chat.IsBusy))
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
            }

            SetCodeUpdate(new CodeUpdate(CodeUpdateState.Updating, installed, latest));
            (int exitCode, string output) = await ClaudeCodeCli.UpdateAsync(exe, CancellationToken.None);
            string? now = await ClaudeCodeCli.GetVersionAsync(exe, CancellationToken.None);
            if (now != null && ClaudeCodeCli.CompareVersions(now, installed) > 0)
            {
                SetCodeUpdate(new CodeUpdate(CodeUpdateState.Updated, now, latest));
                foreach (ClaudeChatViewModel chat in Instances.Where(c => c.CodeStatus is { Installed: true }))
                {
                    chat.CodeStatus = chat.CodeStatus! with { Version = now };
                }
            }
            else if (exitCode == 0)
            {
                // It succeeded without changing anything: the release channel Claude Code follows hasn't got this version yet.
                SetCodeUpdate(null);
            }
            else
            {
                SetCodeUpdate(new CodeUpdate(CodeUpdateState.Failed, installed, latest, LastLine(output)));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or OperationCanceledException)
        {
            SetCodeUpdate(new CodeUpdate(CodeUpdateState.Failed, installed, latest, ex is OperationCanceledException ? "It took too long." : ex.Message));
        }
        finally
        {
            installingUpdate = false;
        }
    }

    /// <summary>Asks GitHub whether a newer version of this extension has been released, unless that's turned off.</summary>
    private static async Task CheckExtensionAsync()
    {
        if (!AgentSettings.Load().CheckForExtensionUpdates || extensionUpdate is { State: not ExtensionUpdateState.Available })
        {
            return; // or one is already being downloaded or installed
        }

        if (await ExtensionUpdates.GetLatestAsync(CancellationToken.None) is ExtensionRelease release && ExtensionUpdates.IsNewer(release))
        {
            SetExtensionUpdate(new ExtensionUpdate(ExtensionUpdateState.Available, release));
        }
    }

    /// <summary>Downloads the release and opens Visual Studio's installer on it, which finishes once Visual Studio closes.</summary>
    private static async Task InstallExtensionUpdateAsync(ExtensionRelease release)
    {
        if (extensionUpdate is { State: ExtensionUpdateState.Downloading })
        {
            return;
        }

        SetExtensionUpdate(new ExtensionUpdate(ExtensionUpdateState.Downloading, release));
        try
        {
            string vsix = await ExtensionUpdates.DownloadAsync(release, CancellationToken.None);
            ExtensionUpdates.StartInstaller(vsix);
            SetExtensionUpdate(new ExtensionUpdate(ExtensionUpdateState.Installing, release));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.IO.IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SetExtensionUpdate(new ExtensionUpdate(ExtensionUpdateState.Failed, release, ex is TaskCanceledException ? "it took too long." : ex.Message));
        }
    }

    private static void SetExtensionUpdate(ExtensionUpdate? update)
    {
        extensionUpdate = update;
        RefreshAllBanners();
    }

    private static string LastLine(string output) =>
        output.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;

    /// <summary>Asks the Models API (with the saved key, if there is one) whether there are newer model versions.</summary>
    private static async Task CheckApiModelsAsync()
    {
        if (ApiKeyStore.Load() is not string apiKey)
        {
            return;
        }

        try
        {
            if (await ModelCatalog.RefreshApiAsync(apiKey, CancellationToken.None))
            {
                OnModelsChanged();
            }
        }
        catch (Exception)
        {
            // A courtesy check (a rejected key or no network shows up properly when the user sends a message).
        }
    }

    private static void SetCodeUpdate(CodeUpdate? update)
    {
        codeUpdate = update;
        RefreshAllBanners();
    }

    /// <summary>The names shown for the models may have changed.</summary>
    private static void OnModelsChanged()
    {
        foreach (ClaudeChatViewModel chat in Instances)
        {
            chat.OnPropertyChanged(nameof(chat.ModelSummary));
            chat.OnPropertyChanged(nameof(chat.SubagentSummary));
            chat.RefreshBanners();
        }
    }

    private static void RefreshAllBanners()
    {
        foreach (ClaudeChatViewModel chat in Instances)
        {
            chat.RefreshBanners();
        }
    }

    /// <summary>Rebuilds this window's banners from the shared state.</summary>
    private void RefreshBanners()
    {
        var banners = new List<NoticeBanner>();
        if (codeUpdate is CodeUpdate update)
        {
            string key = $"code:{update.State}:{update.Latest}";
            banners.Add(update.State switch
            {
                CodeUpdateState.Available when ClaudeCodeCli.AutoUpdateDisabled() => new NoticeBanner(
                    key,
                    $"Claude Code {update.Latest} is available (you have {update.Installed}). Automatic updates are turned off.",
                    "Update now",
                    () => this.RetryUpdate(update)),
                CodeUpdateState.Available => new NoticeBanner(key, $"Claude Code {update.Latest} is available. It will update once Claude isn't replying."),
                CodeUpdateState.Updating => new NoticeBanner(key, $"Updating Claude Code to {update.Latest}…"),
                CodeUpdateState.Updated => new NoticeBanner(key, $"Claude Code updated to {update.Installed}. New chats use it."),
                _ => new NoticeBanner(
                    key,
                    $"Couldn't update Claude Code to {update.Latest}" + (update.Detail.Length > 0 ? ": " + update.Detail : "."),
                    "Try again",
                    () => this.RetryUpdate(update)),
            });
        }

        if (extensionUpdate is ExtensionUpdate ours && this.settings.CheckForExtensionUpdates)
        {
            ExtensionRelease release = ours.Release;
            string key = $"extension:{ours.State}:{release.Version}";
            banners.Add(ours.State switch
            {
                ExtensionUpdateState.Available => new NoticeBanner(
                    key,
                    $"Claude for VS {release.Version} is available (you have {ExtensionUpdates.InstalledVersion}).",
                    "Update",
                    () => _ = InstallExtensionUpdateAsync(release)),
                ExtensionUpdateState.Downloading => new NoticeBanner(key, $"Downloading Claude for VS {release.Version}…"),
                ExtensionUpdateState.Installing => new NoticeBanner(
                    key,
                    $"Claude for VS {release.Version} is ready. Finish in the installer that opened; it completes once you close Visual Studio.",
                    "Open again",
                    () => _ = InstallExtensionUpdateAsync(release)),
                _ => new NoticeBanner(
                    key,
                    $"Couldn't download Claude for VS {release.Version}: {ours.Detail}",
                    "Try again",
                    () => _ = InstallExtensionUpdateAsync(release)),
            });
        }

        if (this.settings.Engine == ChatEngineKind.Api && !this.settings.UseNewestApiModels
            && ModelCatalog.NewerApiModel(this.settings.Model) is ModelVersion newer)
        {
            banners.Add(new NoticeBanner(
                "model:" + newer.Id,
                $"{newer.Name} is available on the Claude API. Chats use {AgentSettings.ModelName(this.settings.Model, ChatEngineKind.Api)}, the version this extension was built for.",
                "Use newest",
                () => this.UseNewestApiModels()));
        }

        if (this.LowPriorityBanner() is NoticeBanner lowPriority)
        {
            banners.Insert(0, lowPriority);
        }

        this.Banners.Clear();
        foreach (NoticeBanner banner in banners.Where(b => !DismissedBanners.Contains(b.Key)))
        {
            this.Banners.Add(banner);
        }
    }

    private void RetryUpdate(CodeUpdate update)
    {
        if (ClaudeCodeCli.FindExecutable() is string exe)
        {
            _ = InstallUpdateAsync(exe, update.Installed, update.Latest);
        }
    }

    /// <summary>Turns on <see cref="AgentSettings.UseNewestApiModels"/>, which starts a new chat if one is open.</summary>
    private void UseNewestApiModels()
    {
        if (this.IsBusy)
        {
            this.Status = "Switching versions starts a new chat; wait until Claude has finished replying.";
            return;
        }

        this.ApplySettings(this.settings with { UseNewestApiModels = true }, this.RepoPath, null);
        this.Status = $"Now using {this.ModelSummary}.";
    }

    /// <summary>After a reply: remembers which model Claude Code's alias resolved to, so its name shows everywhere.</summary>
    private static void ObserveModel(IChatEngine runEngine)
    {
        if (runEngine is ClaudeCodeEngine { ResolvedModelId: string id } && ModelCatalog.ObserveClaudeCode(id))
        {
            OnModelsChanged();
        }
    }
}
