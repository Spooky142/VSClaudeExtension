using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS;

/// <summary>
/// Windows notifications for when Claude needs the user and they aren't looking at the chat. Shown as a notification
/// area balloon, which Windows 10 and 11 show as a toast; its icon is only there while the notification is.
/// </summary>
internal sealed class DesktopNotifier : IDisposable
{
    private const int ShowMilliseconds = 10_000;

    private NotifyIcon? icon;
    private Action? onClick;

    /// <param name="onClick">Called on the UI thread when the user clicks the notification.</param>
    public void Show(string title, string message, Action onClick)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            this.icon ??= this.Create();
            this.onClick = onClick;
            this.icon.Visible = true;
            this.icon.ShowBalloonTip(ShowMilliseconds, title, message, ToolTipIcon.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            // Notifications are a nicety; the status bar and the flashing taskbar button still say Claude needs the user.
        }
    }

    public void Dispose()
    {
        this.icon?.Dispose();
        this.icon = null;
    }

    private NotifyIcon Create()
    {
        var created = new NotifyIcon { Text = "Claude", Icon = AppIcon() };
        // NotifyIcon raises its events on the thread that made it: Visual Studio's main thread.
        created.BalloonTipClicked += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.Clicked();
        };
        created.MouseClick += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.Clicked();
        };
        created.BalloonTipClosed += (_, _) => created.Visible = false;
        return created;
    }

    private void Clicked()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (this.icon != null)
        {
            this.icon.Visible = false;
        }

        this.onClick?.Invoke();
    }

    /// <summary>Visual Studio's own icon, so the notification says where it's from.</summary>
    private static Icon AppIcon()
    {
        try
        {
            string? exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null && Icon.ExtractAssociatedIcon(exe) is Icon icon)
            {
                return icon;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        return SystemIcons.Information;
    }
}
