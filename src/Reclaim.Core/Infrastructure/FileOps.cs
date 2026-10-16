namespace Reclaim.Core.Infrastructure;

public static class FileOps
{
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static long DirectorySize(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", AllFiles))
            {
                ct.ThrowIfCancellationRequested();
                try { total += f.Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return total;
    }

    public static long SizeOf(string path) =>
        File.Exists(path) ? SafeLength(path) : DirectorySize(path);

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    public static bool SendToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x40 | 0x10 | 0x400 | 0x4, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };
        var rc = SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Deletes a file or directory, clearing read-only attributes. Files in use are scheduled for removal at reboot.</summary>
    public static bool DeletePermanently(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    di.Delete(); // remove the link only, never its target
                    return true;
                }
                foreach (var f in di.EnumerateFiles("*", AllFiles))
                    try { f.Attributes = FileAttributes.Normal; } catch { }
                di.Delete(true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete failed for {path}: {ex.Message}; scheduling for reboot");
            ScheduleDeleteOnReboot(path);
            return false;
        }
    }

    public static void ScheduleDeleteOnReboot(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", AllFiles)) MoveFileEx(f, null, 4);
            foreach (var d in Directory.EnumerateDirectories(path, "*", AllFiles).OrderByDescending(d => d.Length)) MoveFileEx(d, null, 4);
        }
        MoveFileEx(path, null, 4); // MOVEFILE_DELAY_UNTIL_REBOOT
    }

    /// <summary>Moves a file/dir, falling back to copy+delete across volumes.</summary>
    public static void Move(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: true);
            return;
        }
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }
        CopyDirectory(source, destination);
        DeletePermanently(source);
    }

    public static void CopyDirectory(string source, string destination, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, overwrite: true);
            File.SetAttributes(target, File.GetAttributes(file));
            progress?.Report(file);
        }
    }

    public static ulong FreeSpace(string path) =>
        Native.GetDiskFreeSpaceEx(Path.GetPathRoot(path)!, out var free, out _, out _) ? free : 0;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);
}
