using System.Security.Cryptography;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Cleaners;

public enum WipeMethod
{
    /// <summary>One pass of random data.</summary>
    Quick = 1,
    /// <summary>DoD 5220.22-M: zeros, ones, random.</summary>
    DoD3 = 3,
    /// <summary>Seven alternating passes.</summary>
    Thorough7 = 7,
}

/// <summary>Unrecoverable delete (shredder) and free-space wiping (evidence remover).</summary>
public static class SecureErase
{
    private const int Chunk = 4 * 1024 * 1024;

    public static bool IsSolidState(string path)
    {
        // Best-effort: query MSFT_PhysicalDisk MediaType 4 = SSD for the volume's disk.
        try
        {
            var letter = Path.GetPathRoot(path)![0];
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                $"SELECT * FROM MSFT_Partition WHERE DriveLetter='{letter}'");
            foreach (System.Management.ManagementObject part in s.Get())
            {
                var disk = part["DiskNumber"];
                using var d = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                    $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='{disk}'");
                foreach (System.Management.ManagementObject pd in d.Get())
                    return Convert.ToInt32(pd["MediaType"]) == 4;
            }
        }
        catch { }
        return false;
    }

    public static async Task<(int Files, int Failed)> ShredAsync(IEnumerable<string> paths, WipeMethod method, IProgress<string>? progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int done = 0, failed = 0;
            foreach (var root in paths)
            {
                var files = Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList()
                    : [root];
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Erasing {f}");
                    if (ShredFile(f, method, ct)) done++;
                    else failed++;
                }
                if (Directory.Exists(root))
                {
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                            Directory.Delete(ObscureName(d));
                        Directory.Delete(ObscureName(root));
                    }
                    catch (Exception ex) { Log.Warn($"Could not remove folder {root}: {ex.Message}"); }
                }
            }
            return (done, failed);
        }, ct);
    }

    public static bool ShredFile(string file, WipeMethod method, CancellationToken ct)
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            var length = new FileInfo(file).Length;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, Chunk, FileOptions.WriteThrough))
            {
                var buffer = new byte[(int)Math.Min(Chunk, Math.Max(length, 1))];
                for (var pass = 0; pass < (int)method; pass++)
                {
                    Fill(buffer, method, pass);
                    fs.Position = 0;
                    long written = 0;
                    while (written < length)
                    {
                        ct.ThrowIfCancellationRequested();
                        var n = (int)Math.Min(buffer.Length, length - written);
                        if (PassIsRandom(method, pass)) RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
                        fs.Write(buffer, 0, n);
                        written += n;
                    }
                    fs.Flush(true);
                }
                fs.SetLength(0);
            }
            var renamed = ObscureName(file);
            File.SetCreationTime(renamed, new DateTime(2000, 1, 1));
            File.SetLastWriteTime(renamed, new DateTime(2000, 1, 1));
            File.Delete(renamed);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Shred failed for {file}: {ex.Message}");
            return false;
        }
    }

    private static bool PassIsRandom(WipeMethod m, int pass) => m switch
    {
        WipeMethod.Quick => true,
        WipeMethod.DoD3 => pass == 2,
        _ => pass % 3 == 2,
    };

    private static void Fill(byte[] buffer, WipeMethod m, int pass)
    {
        if (PassIsRandom(m, pass)) RandomNumberGenerator.Fill(buffer);
        else Array.Fill(buffer, pass % 2 == 0 ? (byte)0x00 : (byte)0xFF);
    }

    /// <summary>Renames a file or folder to random names a few times so the original name is gone from the MFT.</summary>
    private static string ObscureName(string path)
    {
        var current = path;
        var dir = Path.GetDirectoryName(path)!;
        for (var i = 0; i < 3; i++)
        {
            var next = Path.Combine(dir, Path.GetRandomFileName());
            try
            {
                if (Directory.Exists(current)) Directory.Move(current, next);
                else File.Move(current, next);
                current = next;
            }
            catch { break; }
        }
        return current;
    }

    /// <summary>Overwrites all free space on a drive so already-deleted files can't be recovered.</summary>
    public static async Task WipeFreeSpaceAsync(string driveRoot, int passes, IProgress<(string Message, double Percent)>? progress, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var folder = Path.Combine(driveRoot, "ReclaimWipe.tmp");
            Directory.CreateDirectory(folder);
            try
            {
                for (var pass = 1; pass <= passes; pass++)
                {
                    var file = Path.Combine(folder, $"fill{pass}.bin");
                    var start = FileOps.FreeSpace(driveRoot);
                    const ulong reserve = 64UL * 1024 * 1024; // leave room so Windows keeps working
                    var buffer = new byte[Chunk];
                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, FileOptions.WriteThrough))
                    {
                        ulong written = 0;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            var free = FileOps.FreeSpace(driveRoot);
                            if (free <= reserve + Chunk) break;
                            RandomNumberGenerator.Fill(buffer);
                            try { fs.Write(buffer, 0, buffer.Length); }
                            catch (IOException) { break; } // disk full
                            written += Chunk;
                            if (written % (256UL * 1024 * 1024) == 0)
                                progress?.Report(($"Pass {pass} of {passes}: {Format.Bytes((long)written)} written", start == 0 ? 0 : Math.Min(100, written * 100.0 / start)));
                        }
                        fs.Flush(true);
                    }
                    File.Delete(file);
                }
                progress?.Report(("Done", 100));
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }, ct);
    }
}
