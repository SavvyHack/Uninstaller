using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Monitor;

/// <summary>File-system snapshot of the places installers write to (used when real-time ETW capture is unavailable).</summary>
public sealed class FileSnapshot
{
    internal HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(Directory.GetParent(local)!.FullName, "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers"),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly string[] SkipFragments =
    [
        @"\Temp\", @"\INetCache\", @"\Microsoft\Windows\Explorer\", @"\Packages\", @"\WindowsApps\", @"\Package Cache\",
        @"\Microsoft\Windows\WER\", @"\CrashDumps\", @"\D3DSCache\", @"\NVIDIA\DXCache\", @"\Google\Chrome\User Data\",
        @"\Microsoft\Edge\User Data\", @"\Mozilla\Firefox\Profiles\",
    ];

    public static FileSnapshot Capture(IEnumerable<string>? roots = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var snap = new FileSnapshot();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in roots ?? DefaultRoots())
        {
            progress?.Report($"Recording {root}…");
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (SkipFragments.Any(f => entry.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                    snap.Paths.Add(entry);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return snap;
    }

    /// <summary>Top-most new paths (a new folder hides everything inside it).</summary>
    public List<string> CreatedSince(FileSnapshot before)
    {
        var created = Paths.Where(p => !before.Paths.Contains(p)).OrderBy(p => p.Length).ToList();
        var result = new List<string>();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in created)
        {
            var parent = Path.GetDirectoryName(p) ?? "";
            if (!set.Contains(parent)) result.Add(p);
            set.Add(p);
        }
        return result;
    }
}
