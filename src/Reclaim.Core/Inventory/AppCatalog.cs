using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

public sealed class CatalogOptions
{
    public bool IncludeSystemComponents { get; init; }
    public bool IncludeWindowsUpdates { get; init; }
    public bool IncludeStoreApps { get; init; } = true;
    public bool IncludeBrowserExtensions { get; init; } = true;
    public bool IncludePackageManagers { get; init; } = true;
    public bool IncludeOrphanFolders { get; init; } = true;

    public static CatalogOptions FromSettings(ReclaimSettings s) => new()
    {
        IncludeSystemComponents = s.ShowSystemComponents,
        IncludeWindowsUpdates = s.ShowWindowsUpdates,
    };
}

/// <summary>Aggregates every provider into one de-duplicated list.</summary>
public sealed class AppCatalog
{
    public async Task<List<InstalledApp>> LoadAsync(CatalogOptions options, CancellationToken ct = default)
    {
        var providers = new List<IAppProvider>
        {
            new RegistryUninstallProvider { IncludeSystemComponents = options.IncludeSystemComponents },
        };
        if (options.IncludeStoreApps) providers.Add(new StoreAppProvider());
        if (options.IncludeBrowserExtensions) providers.Add(new BrowserExtensionProvider());
        if (options.IncludePackageManagers)
        {
            providers.Add(new ScoopProvider());
            providers.Add(new ChocolateyProvider());
        }
        if (options.IncludeWindowsUpdates) providers.Add(new WindowsUpdateProvider());

        var results = await Task.WhenAll(providers.Select(async p =>
        {
            try { return await p.GetAppsAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"Provider {p.Name} failed", ex);
                return (IReadOnlyList<InstalledApp>)[];
            }
        }));

        var all = new List<InstalledApp>();
        var win32Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in results)
        {
            foreach (var app in batch)
            {
                if (app.Source == AppSource.Win32) win32Names.Add(PathUtil.Squash(app.Name));
                // Package-manager entries that also registered in Programs and Features show once (the registry one).
                if (app.Source == AppSource.Chocolatey && win32Names.Contains(PathUtil.Squash(app.Name))) continue;
                all.Add(app);
            }
        }

        OrphanDetector.MarkBrokenEntries(all);
        AppEnricher.FlagBundleware(all);
        Monitor.TraceStore.MarkTracedApps(all);
        Updates.UpdateIndex.Apply(all);
        SystemTools.SoftwareRelocator.MarkRelocated(all);

        if (options.IncludeOrphanFolders)
        {
            try
            {
                all.AddRange(await Task.Run(() => OrphanDetector.FindUnclaimedFolders(all, ct), ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Orphan folder scan failed: {ex.Message}");
            }
        }
        return all;
    }

    /// <summary>Best match for an executable path (used by Hunter mode and drag-and-drop).</summary>
    public static InstalledApp? FindOwner(IEnumerable<InstalledApp> apps, string exePath)
    {
        var dir = Path.GetDirectoryName(exePath) ?? "";
        InstalledApp? best = null;
        var bestScore = 0;
        foreach (var a in apps)
        {
            if (a.Source is AppSource.WindowsUpdate or AppSource.BrowserExtension) continue;
            var score = 0;
            if (PathUtil.SamePath(PathUtil.Clean(a.DisplayIcon), exePath)) score = 100;
            else if (a.InstallLocation is { Length: > 3 } loc && PathUtil.IsUnder(exePath, loc)) score = 50 + loc.Length;
            else if (PathUtil.ExecutableFromCommand(a.UninstallString) is { } u && Path.GetDirectoryName(u) is { Length: > 3 } ud
                     && PathUtil.IsUnder(dir, ud)) score = 40 + ud.Length;
            if (score > bestScore)
            {
                bestScore = score;
                best = a;
            }
        }
        return best;
    }
}
