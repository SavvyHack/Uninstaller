using System.Runtime.InteropServices;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Uninstall;

/// <summary>
/// Starts a process suspended inside a Job object so every child it spawns (NSIS copies itself to %TEMP%,
/// InstallShield relaunches from temp, etc.) is tracked. Completion means the whole tree has exited.
/// </summary>
internal sealed class JobProcess : IDisposable
{
    private readonly IntPtr _job;
    private readonly IntPtr _process;
    private bool _disposed;

    public int ProcessId { get; }

    private JobProcess(IntPtr job, IntPtr process, int pid)
    {
        _job = job;
        _process = process;
        ProcessId = pid;
    }

    public static JobProcess Start(string commandLine, string? workingDir)
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject failed");

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        var cmd = new System.Text.StringBuilder(commandLine);
        if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB,
                IntPtr.Zero, workingDir, ref si, out var pi)
            && !CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, workingDir, ref si, out pi))
        {
            var err = Marshal.GetLastWin32Error();
            Native.CloseHandle(job);
            throw new System.ComponentModel.Win32Exception(err, $"Could not start: {commandLine}");
        }

        Native.AssignProcessToJobObject(job, pi.hProcess);
        ResumeThread(pi.hThread);
        Native.CloseHandle(pi.hThread);
        return new JobProcess(job, pi.hProcess, pi.dwProcessId);
    }

    public uint ActiveProcesses =>
        Native.QueryInformationJobObject(_job, Native.JobObjectBasicAccountingInformation, out var info,
            Marshal.SizeOf<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? info.ActiveProcesses
            : 0;

    public int? MainExitCode => GetExitCodeProcess(_process, out var code) && code != 259 /* STILL_ACTIVE */ ? (int)code : null;

    public async Task<int> WaitForTreeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ActiveProcesses > 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return MainExitCode ?? 0;
    }

    public void Kill() => Native.TerminateJobObject(_job, 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.CloseHandle(_process);
        Native.CloseHandle(_job);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
