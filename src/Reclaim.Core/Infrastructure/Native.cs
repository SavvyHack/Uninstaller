using System.Runtime.InteropServices;
using System.Text;

namespace Reclaim.Core.Infrastructure;

internal static partial class Native
{
    // ---------- Job objects (wait for a whole uninstaller process tree) ----------
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(IntPtr job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    internal const int JobObjectBasicAccountingInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    // ---------- Windows ----------
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    internal const uint GA_ROOT = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    internal static string? ProcessImagePath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // ---------- Junctions ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);

    // ---------- Recycle bin ----------
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    internal const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    // ---------- Clipboard ----------
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    // ---------- Shell notify (refresh after association changes) ----------
    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    internal const int SHCNE_ASSOCCHANGED = 0x08000000;

    // ---------- Registry change notification ----------
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree, uint filter, Microsoft.Win32.SafeHandles.SafeWaitHandle evt, bool async);

    internal const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
}
