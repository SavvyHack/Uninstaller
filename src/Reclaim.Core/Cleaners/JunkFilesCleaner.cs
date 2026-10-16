using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Cleaners;

public sealed class JunkFile
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public required string Pattern { get; init; }
    public DateTime LastAccess { get; init; }
    public bool Selected { get; set; } = true;
    public string SizeText => Format.Bytes(SizeBytes);
}

/// <summary>Scans drives for leftover temporary/junk files by pattern, skipping system and excluded folders.</summary>
public static class JunkFilesCleaner
{
    public static readonly string[] DefaultPatterns =
    [
        "*.tmp", "*.temp", "~*.*", "*.~*", "*._mp", "*.$$$", "*.___", "*.chk", "*.gid", "*.dmp", "*.err", "*.fts", "*.syd",
        "*.wbk", "*.prv", "*.sik", "thumbs.db", "*.old", "*.bak",
    ];

    /// <summary>Patterns that may be user backups; found but not pre-selected.</summary>
    private static readonly HashSet<string> ReviewPatterns = new(StringComparer.OrdinalIgnoreCase) { "*.old", "*.bak", "*.wbk" };

    public static readonly string[] DefaultExcludedPaths =
    [
        @"?:\Windows\*", @"?:\Program Files\WindowsApps\*", @"?:\$Recycle.Bin\*", @"?:\System Volume Information\*", @"?:\Recovery\*",
        @"*\AppData\Local\Packages\*", @"*\node_modules\*", @"*\.git\*", @"*\.vs\*", @"*\OneDrive\*", @"*\Dropbox\*", @"*\Google Drive\*",
        @"?:\ProgramData\Microsoft\*", @"*\AppData\Local\Microsoft\Windows\*",
    ];

    public static async Task<List<JunkFile>> ScanAsync(IEnumerable<string> drives, IProgress<string>? progress, CancellationToken ct)
    {
        var settings = SettingsStore.Current;
        var patterns = settings.JunkPatterns;
        var exclusions = settings.JunkExcludedPaths;
        var ignoreRecent = settings.JunkIgnoreRecentlyAccessed;
        var cutoff = DateTime.Now.AddHours(-24);

        return await Task.Run(() =>
        {
            var found = new List<JunkFile>();
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var drive in drives)
            {
                var stack = new Stack<string>();
                stack.Push(drive);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = stack.Pop();
                    if (exclusions.Any(x => Wildcard.IsMatch(dir + "\\", x) || Wildcard.IsMatch(dir, x.TrimEnd('*', '\\')))) continue;
                    progress?.Report(dir);
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*", options))
                        {
                            var name = System.IO.Path.GetFileName(f);
                            var pattern = patterns.FirstOrDefault(p => Wildcard.IsMatch(name, p));
                            if (pattern is null) continue;
                            var info = new FileInfo(f);
                            if (ignoreRecent && info.LastAccessTime > cutoff) continue;
                            found.Add(new JunkFile
                            {
                                Path = f, SizeBytes = info.Length, Pattern = pattern, LastAccess = info.LastAccessTime,
                                Selected = !ReviewPatterns.Contains(pattern),
                            });
                        }
                        foreach (var d in Directory.EnumerateDirectories(dir, "*", options)) stack.Push(d);
                    }
                    catch { }
                }
            }
            return found;
        }, ct);
    }

    public static Task<(int Deleted, long Freed)> DeleteAsync(IEnumerable<JunkFile> files, bool toRecycleBin, CancellationToken ct) => Task.Run(() =>
    {
        int deleted = 0;
        long freed = 0;
        foreach (var f in files.Where(f => f.Selected))
        {
            ct.ThrowIfCancellationRequested();
            var ok = toRecycleBin ? FileOps.SendToRecycleBin(f.Path) : TryDelete(f.Path);
            if (!ok) continue;
            deleted++;
            freed += f.SizeBytes;
        }
        return (deleted, freed);
    }, ct);

    private static bool TryDelete(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}
