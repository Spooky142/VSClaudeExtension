using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace ClaudeForVS.ToolWindows;

/// <summary>What the update window needs: the release, its .vsix if that's already downloaded, and what restarting affects.</summary>
/// <param name="SolutionName">The solution (or folder) Visual Studio opens again; null for none.</param>
/// <param name="ClaudeIsReplying">Whether a chat is in the middle of a reply, which closing would stop.</param>
internal sealed record ExtensionUpdateRequest(ExtensionRelease Release, string? Downloaded, string? SolutionName, Func<bool> ClaudeIsReplying);

/// <summary>How the update window ended: restart to install the .vsix, keep it for later, or the download failed.</summary>
internal sealed record ExtensionUpdateResult(bool Restart, string? VsixPath, string? Error);

/// <summary>
/// Downloads an update of this extension, showing its progress, then explains what installing it involves and asks
/// whether to restart Visual Studio now. ShowModal(), then read <see cref="Result"/>.
/// </summary>
public partial class ExtensionUpdateDialog : DialogWindow
{
    private enum Stage
    {
        Downloading,
        Ready,
        Failed,
    }

    private readonly ExtensionUpdateRequest request;
    private readonly JoinableTaskFactory jtf;
    private CancellationTokenSource? download;
    private Stage stage;
    private string? vsixPath;
    private string? error;
    private bool restart;

    internal ExtensionUpdateDialog(ExtensionUpdateRequest request, JoinableTaskFactory jtf)
    {
        this.InitializeComponent();
        HighContrast.Follow(this);
        this.request = request;
        this.jtf = jtf;
        this.vsixPath = request.Downloaded is string downloaded && File.Exists(downloaded) ? downloaded : null;
        this.Loaded += (_, _) =>
        {
            if (this.vsixPath is null)
            {
                this.StartDownload();
            }
            else
            {
                this.ShowReady();
            }
        };

        // Closing it while downloading stops the download.
        this.Closing += (_, _) => this.download?.Cancel();
    }

    internal ExtensionUpdateResult Result => new(this.restart, this.vsixPath, this.stage == Stage.Failed ? this.error : null);

    private string Version => this.request.Release.Version;

    private void StartDownload()
    {
        this.stage = Stage.Downloading;
        this.SetHeading("", "Accent", $"Downloading Claude for VS {this.Version}");
        this.Message.Text = $"You have {ExtensionUpdates.InstalledVersion}. Nothing changes until Visual Studio restarts.";
        this.Warning.Visibility = Visibility.Collapsed;
        this.ProgressPanel.Visibility = Visibility.Visible;
        this.Progress.Value = 0;
        this.ProgressText.Text = "Connecting to GitHub…";
        this.PrimaryAction.Visibility = Visibility.Collapsed;
        this.SecondaryAction.Content = "Cancel";

        var cts = new CancellationTokenSource();
        this.download = cts;
        var progress = new Progress<(long Received, long? Total)>(this.ShowProgress);
        this.jtf.RunAsync(async () =>
        {
            try
            {
                string path = await ExtensionUpdates.DownloadAsync(this.request.Release, progress, cts.Token);
                await this.jtf.SwitchToMainThreadAsync();
                this.vsixPath = path;
                this.ShowReady();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The window was closed.
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException
                or InvalidOperationException)
            {
                await this.jtf.SwitchToMainThreadAsync();
                this.ShowFailed(ex is OperationCanceledException ? "It took too long." : ex.Message);
            }
            finally
            {
                if (this.download == cts)
                {
                    this.download = null;
                }

                cts.Dispose();
            }
        }).Task.FileAndForget("ClaudeForVS/ExtensionUpdate");
    }

    private void ShowProgress((long Received, long? Total) progress)
    {
        if (this.stage != Stage.Downloading)
        {
            return;
        }

        if (progress.Total is long total && total > 0)
        {
            this.Progress.Maximum = total;
            this.Progress.Value = Math.Min(progress.Received, total);
            this.ProgressText.Text = $"{Megabytes(progress.Received)} of {Megabytes(total)}";
        }
        else
        {
            this.ProgressText.Text = Megabytes(progress.Received);
        }
    }

    private void ShowReady()
    {
        this.stage = Stage.Ready;
        this.SetHeading("", "AddedGreen", $"Claude for VS {this.Version} is ready to install");
        string reopen = this.request.SolutionName is string name ? $" with {name}" : string.Empty;
        this.Message.Text = "To install it, Visual Studio has to close. The update then installs by itself, and Visual Studio "
            + $"opens again{reopen}.\n\nIf you have unsaved files, Visual Studio asks whether to save them before it closes.";
        this.Warning.Text = "Claude is still replying in a chat. Restarting now stops that reply.";
        this.Warning.Visibility = this.request.ClaudeIsReplying() ? Visibility.Visible : Visibility.Collapsed;
        this.ProgressPanel.Visibility = Visibility.Collapsed;
        this.PrimaryAction.Content = "Restart now";
        this.PrimaryAction.Visibility = Visibility.Visible;
        this.PrimaryAction.Focus();
        this.SecondaryAction.Content = "Later";
    }

    private void ShowFailed(string detail)
    {
        this.stage = Stage.Failed;
        this.error = detail;
        this.SetHeading("", "ErrorRed", $"Couldn't download Claude for VS {this.Version}");
        this.Message.Text = detail;
        this.Warning.Visibility = Visibility.Collapsed;
        this.ProgressPanel.Visibility = Visibility.Collapsed;
        this.PrimaryAction.Content = "Try again";
        this.PrimaryAction.Visibility = Visibility.Visible;
        this.SecondaryAction.Content = "Close";
    }

    private void SetHeading(string icon, string brush, string text)
    {
        this.HeadingIcon.Text = icon;
        this.HeadingIcon.SetResourceReference(ForegroundProperty, brush);
        this.Heading.Text = text;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (this.stage == Stage.Ready)
        {
            this.restart = true;
            this.DialogResult = true;
        }
        else if (this.stage == Stage.Failed)
        {
            this.StartDownload();
        }
    }

    private void OnNotes(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(this.request.Release.PageUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                this.NotesLink.ToolTip = $"Couldn't open a browser ({ex.Message}). {uri.AbsoluteUri}";
            }
        }
    }

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}
