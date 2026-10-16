using System.Text.RegularExpressions;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Leftovers;

/// <summary>
/// Everything known about an app that helps recognise its leftovers. It is captured before the built-in
/// uninstaller runs, because afterwards the install folder (and its exe names) may already be gone.
/// </summary>
public sealed partial class AppFingerprint
{
    public required string DisplayName { get; init; }
    public string NameKey { get; init; } = "";
    public string? PublisherKey { get; init; }
    public string? InstallDir { get; init; }
    public string? InstallDirLeafKey { get; init; }
    public IReadOnlySet<string> NameTokens { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ExeNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string? ProductCode { get; init; }
    public string? PackedProductCode { get; init; }
    public string? UninstallKeyPath { get; init; }
    public string? PackageFamilyName { get; init; }
    public IReadOnlyList<string> ExtraPaths { get; init; } = [];

    /// <summary>Squashed names that count as an exact identity match.</summary>
    public IReadOnlySet<string> IdentityKeys { get; init; } = new HashSet<string>();

    /// <summary>Generic words that never identify a program on their own.</summary>
    public static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "applications", "bin", "cache", "caches", "client", "common", "components", "config",
        "configuration", "core", "data", "default", "desktop", "driver", "drivers", "edition", "files", "free", "help",
        "helper", "install", "installer", "launcher", "lib", "library", "local", "logs", "manager", "microsoft", "module",
        "modules", "pack", "package", "packages", "plugin", "plugins", "portable", "pro", "program", "programs", "runtime",
        "service", "services", "settings", "setup", "shared", "software", "studio", "system", "temp", "tmp", "tool",
        "tools", "uninstall", "update", "updater", "user", "users", "utilities", "utility", "version", "web", "windows",
        "x64", "x86", "64bit", "32bit", "bit", "the", "and", "for", "inc", "llc", "ltd", "corp", "corporation", "company",
        "gmbh", "home", "professional", "standard", "ultimate", "premium", "trial", "beta", "release", "stable", "player",
        "google", "mozilla", "adobe", "intel", "nvidia", "amd", "apple", "oracle", "java", "python", "node", "net", "sdk",
    };

    public static AppFingerprint FromApp(InstalledApp app)
    {
        var cleanName = CleanName(app.Name);
        var tokens = Tokenize(cleanName);
        var publisher = CleanPublisher(app.Publisher);
        var installDir = ResolveInstallDir(app);
        var exeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (installDir is not null && Directory.Exists(installDir))
        {
            try
            {
                foreach (var exe in Directory.EnumerateFiles(installDir, "*.exe", new EnumerationOptions
                         {
                             RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true,
                         }).Take(60))
                {
                    var n = Path.GetFileName(exe);
                    if (!n.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !n.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase)
                        && !n.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
                        exeNames.Add(n);
                }
            }
            catch { }
        }
        if (app.MainExecutable is { } main) exeNames.Add(Path.GetFileName(main));

        var identity = new HashSet<string>();
        var nameKey = PathUtil.Squash(cleanName);
        if (nameKey.Length >= 3) identity.Add(nameKey);
        // "Contoso Widget" from "Contoso" → also accept "Widget" alone when it is distinctive enough.
        if (publisher is not null && nameKey.StartsWith(publisher) && nameKey.Length - publisher.Length >= 4)
            identity.Add(nameKey[publisher.Length..]);
        var leafKey = installDir is null ? null : PathUtil.Squash(Path.GetFileName(installDir));
        if (leafKey is { Length: >= 3 } && !GenericTokens.Contains(leafKey)) identity.Add(leafKey);
        if (app.Extra.TryGetValue("KeyName", out var keyName) && !Guid.TryParse(keyName, out _))
        {
            var k = PathUtil.Squash(CleanName(keyName));
            if (k.Length >= 4 && !GenericTokens.Contains(k)) identity.Add(k);
        }
        if (app.Extra.TryGetValue("PackageFamilyName", out var pfn))
        {
            var pkgName = pfn.Contains('_') ? pfn[..pfn.LastIndexOf('_')] : pfn;
            identity.Add(PathUtil.Squash(pkgName));
        }

        return new AppFingerprint
        {
            DisplayName = app.Name,
            NameKey = nameKey,
            PublisherKey = publisher,
            InstallDir = installDir,
            InstallDirLeafKey = leafKey,
            NameTokens = tokens,
            ExeNames = exeNames,
            ProductCode = app.ProductCode,
            PackedProductCode = app.ProductCode is null ? null : PackGuid(app.ProductCode),
            UninstallKeyPath = app.RegistryKeyPath,
            PackageFamilyName = app.Extra.GetValueOrDefault("PackageFamilyName"),
            IdentityKeys = identity,
        };
    }

    /// <summary>Install folder from InstallLocation, else the uninstaller or icon folder when it is a dedicated app folder.</summary>
    internal static string? ResolveInstallDir(InstalledApp app)
    {
        var candidates = new[]
        {
            app.InstallLocation,
            Path.GetDirectoryName(PathUtil.Clean(app.DisplayIcon) ?? ""),
            Path.GetDirectoryName(PathUtil.ExecutableFromCommand(app.UninstallString) ?? ""),
        };
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            var dir = c.TrimEnd('\\');
            if (ProtectionGuard.IsProtectedPath(dir)) continue;
            if (dir.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase) && dir.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) continue;
            if (dir.Contains("Package Cache", StringComparison.OrdinalIgnoreCase)) continue;
            if (dir.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) continue;
            return dir;
        }
        return null;
    }

    public static string CleanName(string name)
    {
        var s = name;
        s = Brackets().Replace(s, " ");
        s = VersionLike().Replace(s, " ");
        s = Arch().Replace(s, " ");
        s = s.Replace("™", "").Replace("®", "").Replace("©", "");
        return Spaces().Replace(s, " ").Trim(' ', '-', '_', '.');
    }

    public static string? CleanPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var s = CorpSuffix().Replace(publisher, " ");
        var key = PathUtil.Squash(s);
        return key.Length >= 3 && !GenericTokens.Contains(key) ? key : null;
    }

    public static HashSet<string> Tokenize(string cleanName) =>
        WordSplit().Split(cleanName)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3 && !GenericTokens.Contains(t) && !t.All(char.IsDigit))
            .ToHashSet();

    /// <summary>Windows Installer "packed" GUID used under Installer\Products and UserData.</summary>
    public static string PackGuid(string guid)
    {
        var g = guid.Trim('{', '}').Replace("-", "").ToUpperInvariant();
        if (g.Length != 32) return guid;
        static string Rev(string s) => new(s.Reverse().ToArray());
        var sb = new System.Text.StringBuilder();
        sb.Append(Rev(g[..8])).Append(Rev(g[8..12])).Append(Rev(g[12..16]));
        for (var i = 16; i < 32; i += 2) sb.Append(g[i + 1]).Append(g[i]);
        return sb.ToString();
    }

    [GeneratedRegex(@"\(.*?\)|\[.*?\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+[a-z]?\b|\b(19|20)\d{2}\b|\bversion\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLike();

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|win64|win32)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Arch();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"\b(inc|llc|ltd|limited|corp|corporation|co|gmbh|s\.?a|b\.?v|pty|ag|srl|s\.r\.l|technologies|software|studios?)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex CorpSuffix();
}
