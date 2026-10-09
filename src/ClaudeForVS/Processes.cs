using System;
using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeForVS;

/// <summary>Stopping the processes the extension starts (git, Claude Code, shells, MCP servers). Never throws.</summary>
internal static class Processes
{
    /// <summary>
    /// Stops a process and everything it started. Process.Kill on .NET Framework only stops the process itself, so
    /// this runs taskkill, which takes a moment: don't call it on the UI thread.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            using Process? taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/T /F /PID {process.Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            taskkill?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }

        Kill(process); // in case taskkill couldn't
    }

    /// <summary>Stops just this process, straight away; nothing happens if it has exited already.</summary>
    public static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Exited already, or exiting (which Windows reports as access denied).
        }
    }
}
