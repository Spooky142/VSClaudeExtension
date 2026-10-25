using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

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

    /// <summary>
    /// Puts a process just started into a job object that Windows closes when Visual Studio exits, however it exits
    /// (a crash included), which stops the process and whatever it starts from then on. Without it a Claude Code run,
    /// an MCP server or a shell could carry on after Visual Studio has gone. Does nothing if that fails.
    /// </summary>
    public static void CloseWithVisualStudio(Process process)
    {
        try
        {
            IntPtr job = Job.Value;
            if (job != IntPtr.Zero)
            {
                AssignProcessToJobObject(job, process.Handle);
            }
        }
        catch (Exception)
        {
            // Exited already, or the job couldn't be made; the process just isn't tied to Visual Studio then.
        }
    }

    // One job for the whole of Visual Studio's process. Its handle is never closed: Windows closes it when the process
    // ends, which is what stops the processes in it.
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)size))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }

        return job;
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
