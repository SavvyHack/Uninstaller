using System.Diagnostics;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Models;

namespace Reclaim.Core.Uninstall;

/// <summary>
/// Builds a removable target from whatever the user knows about a broken or half-removed program:
/// a name, its folder, or one of its executables. The leftover scanner then does the actual work.
/// </summary>
public static class ForcedUninstall
{
    public static InstalledApp Create(string nameOrPath, IEnumerable<InstalledApp>? knownApps = null)
    {
        var input = nameOrPath.Trim().Trim('"');

        if (File.Exists(input))
        {
            var owner = knownApps is null ? null : AppCatalog.FindOwner(knownApps, input);
            if (owner is not null) return owner;
            var info = FileVersionInfo.GetVersionInfo(input);
            var dir = Path.GetDirectoryName(input)!;
            return new InstalledApp
            {
                Id = "forced:" + input,
                Name = FirstNonEmpty(info.ProductName, info.FileDescription, Path.GetFileNameWithoutExtension(input))!,
                Publisher = info.CompanyName,
                Version = info.ProductVersion,
                InstallLocation = IsTooBroad(dir) ? null : dir,
                DisplayIcon = input,
                Source = AppSource.Forced,
            };
        }

        if (Directory.Exists(input))
        {
            var exe = SafeFirstExe(input);
            var info = exe is null ? null : FileVersionInfo.GetVersionInfo(exe);
            return new InstalledApp
            {
                Id = "forced:" + input,
                Name = FirstNonEmpty(info?.ProductName, Path.GetFileName(input.TrimEnd('\\')))!,
                Publisher = info?.CompanyName,
                Version = info?.ProductVersion,
                InstallLocation = IsTooBroad(input) ? null : input,
                DisplayIcon = exe,
                Source = AppSource.Forced,
            };
        }

        // Name only: reuse a (possibly broken) registry entry if one matches, else a bare name target.
        var match = knownApps?.FirstOrDefault(a => a.Name.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        return new InstalledApp { Id = "forced:" + input, Name = input, Source = AppSource.Forced };
    }

    private static bool IsTooBroad(string dir) =>
        dir.TrimEnd('\\').Length <= 3
        || PathUtil.SamePath(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
        || PathUtil.SamePath(dir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
        || PathUtil.SamePath(dir, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        || PathUtil.SamePath(dir, Environment.GetFolderPath(Environment.SpecialFolder.System));

    private static string? SafeFirstExe(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
