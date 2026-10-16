using System.Text.RegularExpressions;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Updates;

public sealed record AvailableUpdate(string Name, string Id, string InstalledVersion, string AvailableVersion, string Source);

/// <summary>Finds and installs updates with the Windows Package Manager (winget).</summary>
public static partial class SoftwareUpdater
{
    public static bool IsAvailable => FindWinget() is not null;

    private static string? FindWinget()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        var r = ProcessRunner.Run("where.exe", "winget", TimeSpan.FromSeconds(10));
        return r.ExitCode == 0 ? r.StdOut.Split('\n')[0].Trim() : null;
    }

    public static async Task<List<AvailableUpdate>> CheckAsync(CancellationToken ct)
    {
        var winget = FindWinget();
        if (winget is null) return [];
        var r = await ProcessRunner.RunAsync(winget, "upgrade --include-unknown --accept-source-agreements --disable-interactivity", TimeSpan.FromMinutes(3));
        ct.ThrowIfCancellationRequested();
        var updates = Parse(r.StdOut);
        UpdateIndex.Set(updates);
        return updates;
    }

    /// <summary>Parses winget's fixed-width table using the header's column offsets.</summary>
    internal static List<AvailableUpdate> Parse(string output)
    {
        var lines = output.Replace("\r", "").Split('\n').Select(StripProgress).ToList();
        var headerIdx = lines.FindIndex(l => l.Contains("Id") && l.Contains("Version") && l.Contains("Available"));
        if (headerIdx < 0) return [];
        var header = lines[headerIdx];
        int cId = header.IndexOf("Id", StringComparison.Ordinal);
        int cVer = header.IndexOf("Version", StringComparison.Ordinal);
        int cAvail = header.IndexOf("Available", StringComparison.Ordinal);
        int cSrc = header.IndexOf("Source", StringComparison.Ordinal);
        var list = new List<AvailableUpdate>();
        foreach (var line in lines.Skip(headerIdx + 1))
        {
            if (line.StartsWith("---") || line.Length < cAvail) continue;
            if (Regex.IsMatch(line, @"^\d+ upgrades? available")) break;
            string Col(int start, int end) => start < line.Length ? line[start..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim() : "";
            var name = Col(0, cId);
            var id = Col(cId, cVer);
            var ver = Col(cVer, cAvail);
            var avail = Col(cAvail, cSrc);
            var src = cSrc > 0 ? Col(cSrc, -1) : "";
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(avail) || id.Contains(' ')) continue;
            list.Add(new AvailableUpdate(name.TrimEnd('…'), id, ver, avail, src));
        }
        return list;
    }

    /// <summary>winget draws spinners with backspaces; keep only the text after the last one.</summary>
    private static string StripProgress(string line)
    {
        var idx = line.LastIndexOf('');
        return idx >= 0 ? line[(idx + 1)..] : line;
    }

    public static async Task<ProcessResult> UpgradeAsync(string id, CancellationToken ct)
    {
        var winget = FindWinget() ?? "winget.exe";
        var r = await ProcessRunner.RunAsync(winget,
            $"upgrade --id \"{id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            TimeSpan.FromMinutes(30));
        ct.ThrowIfCancellationRequested();
        return r;
    }
}

/// <summary>Remembers the last update check so the app list can show "update available" badges.</summary>
public static class UpdateIndex
{
    private static List<AvailableUpdate> _last = [];

    public static IReadOnlyList<AvailableUpdate> Last => _last;

    public static void Set(List<AvailableUpdate> updates) => _last = updates;

    public static void Apply(IEnumerable<InstalledApp> apps)
    {
        if (_last.Count == 0) return;
        foreach (var app in apps.Where(a => a.Source == AppSource.Win32))
        {
            var u = _last.FirstOrDefault(u => app.Name.StartsWith(u.Name.TrimEnd('…', '.'), StringComparison.OrdinalIgnoreCase)
                                              || PathUtil.Squash(u.Name) == PathUtil.Squash(app.Name));
            if (u is null) continue;
            app.Flags |= AppFlags.UpdateAvailable;
            app.AvailableVersion = u.AvailableVersion;
            app.WingetId = u.Id;
        }
    }
}
