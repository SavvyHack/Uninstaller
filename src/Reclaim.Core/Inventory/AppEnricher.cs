using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

/// <summary>Slow, per-app details computed after the list is shown: real folder size, signature, bundleware hints.</summary>
public static class AppEnricher
{
    private static readonly string[] BundlerPublishers =
    [
        "Mindspark", "Conduit", "Babylon", "Wajam", "Crossrider", "InstallCore", "OpenCandy", "Ask Partner Network",
        "APN, LLC", "Yontoo", "Superfish", "Segurazo", "ByteFence", "Spigot", "SweetLabs", "Somoto", "Linkury",
        "Montiera", "Perion", "IronSource", "Visual Tools", "Reimage", "Restoro", "Driver Support", "PC Accelerate",
        "OneLaunch", "WebDiscover", "Shopping Assistant",
    ];

    private static readonly string[] PupNameHints =
    [
        "toolbar", "search protect", "searchprotect", "coupon", "safe search", "websearch", "web bar", "browser helper",
        "deal finder", "shopping helper", "pc optimizer", "registry booster", "driver updater",
    ];

    private static readonly string[] RuntimeHints =
    [
        "redistributable", "runtime", "directx", ".net", "visual c++", "webview2", "vcredist", "physx", "openal", "framework",
    ];

    public static void FlagBundleware(IReadOnlyList<InstalledApp> apps)
    {
        foreach (var app in apps)
        {
            var pub = app.Publisher ?? "";
            var name = app.Name;
            if (BundlerPublishers.Any(b => pub.Contains(b, StringComparison.OrdinalIgnoreCase))
                || PupNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                app.Flags |= AppFlags.Bundleware;
        }

        // "Installed alongside" hint: registered within 3 minutes of a different publisher's app.
        var timed = apps
            .Where(a => a.Source == AppSource.Win32 && a.Extra.TryGetValue("KeyWriteTime", out _))
            .Select(a => (App: a, Time: DateTime.Parse(a.Extra["KeyWriteTime"], null, System.Globalization.DateTimeStyles.RoundtripKind)))
            .OrderBy(x => x.Time)
            .ToList();
        for (var i = 1; i < timed.Count; i++)
        {
            var (prev, prevTime) = timed[i - 1];
            var (cur, curTime) = timed[i];
            if ((curTime - prevTime).TotalMinutes > 3) continue;
            if (string.Equals(prev.Publisher, cur.Publisher, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsRuntime(cur) || IsRuntime(prev)) continue;
            if (IsMicrosoft(cur)) continue;
            cur.Extra["InstalledAlongside"] = prev.Name;
        }
    }

    private static bool IsRuntime(InstalledApp a) => RuntimeHints.Any(h => a.Name.Contains(h, StringComparison.OrdinalIgnoreCase));
    private static bool IsMicrosoft(InstalledApp a) => a.Publisher?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

    public static void Enrich(InstalledApp app, CancellationToken ct)
    {
        if (app.Source is AppSource.Win32 or AppSource.Orphan or AppSource.Steam or AppSource.Scoop or AppSource.Chocolatey or AppSource.Store)
        {
            if (app.SizeBytes == 0 && app.InstallLocation is { } loc && Directory.Exists(loc) && !IsSharedRoot(loc))
                app.SizeBytes = FileOps.DirectorySize(loc, ct);
        }

        if (app.Signature is null && app.Source is AppSource.Win32 or AppSource.Steam)
        {
            var exe = app.MainExecutable ?? PathUtil.ExecutableFromCommand(app.UninstallString);
            if (exe is not null && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe))
            {
                app.Signature = SignatureVerifier.Verify(exe);
                if (app.Signature.Status is SignatureStatus.Unsigned or SignatureStatus.Invalid) app.Flags |= AppFlags.Unsigned;
            }
        }
    }

    /// <summary>Never size up a whole drive or Program Files when an app reports a too-broad InstallLocation.</summary>
    private static bool IsSharedRoot(string path)
    {
        var p = path.TrimEnd('\\');
        return p.Length <= 3
               || PathUtil.SamePath(p, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
               || PathUtil.SamePath(p, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
               || PathUtil.SamePath(p, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
               || PathUtil.SamePath(p, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }
}
