using System.Text.Json;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

/// <summary>Scoop apps live in ~/scoop/apps (or $SCOOP / $SCOOP_GLOBAL) and usually have no Uninstall registry entry.</summary>
public sealed class ScoopProvider : IAppProvider
{
    public string Name => "Scoop";

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<InstalledApp>();
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"),
            Environment.GetEnvironmentVariable("SCOOP_GLOBAL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop"),
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(root, "apps");
            if (!Directory.Exists(apps)) continue;
            foreach (var dir in Directory.EnumerateDirectories(apps))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (name.Equals("scoop", StringComparison.OrdinalIgnoreCase)) continue;
                var current = Path.Combine(dir, "current");
                string? version = null;
                var manifest = Path.Combine(current, "manifest.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                        if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();
                    }
                    catch { }
                }
                var app = new InstalledApp
                {
                    Id = "scoop:" + name,
                    Name = name,
                    Publisher = "Scoop",
                    Version = version,
                    InstallLocation = dir,
                    InstallDate = Directory.GetCreationTime(dir),
                    UninstallString = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"scoop uninstall {name}\"",
                    QuietUninstallString = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"scoop uninstall {name} --purge\"",
                    Source = AppSource.Scoop,
                    Flags = AppFlags.HasQuietUninstall | AppFlags.PerUser,
                };
                list.Add(app);
            }
        }
        return (IReadOnlyList<InstalledApp>)list;
    }, ct);
}

/// <summary>Chocolatey packages (lib folder). Packages that also register in Programs and Features are de-duplicated by the catalog.</summary>
public sealed class ChocolateyProvider : IAppProvider
{
    public string Name => "Chocolatey";

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<InstalledApp>();
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        var lib = Path.Combine(root, "lib");
        if (!Directory.Exists(lib)) return (IReadOnlyList<InstalledApp>)list;
        foreach (var dir in Directory.EnumerateDirectories(lib))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            if (id.StartsWith("chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
            string? version = null, title = null, authors = null;
            var nuspec = Directory.EnumerateFiles(dir, "*.nuspec").FirstOrDefault();
            if (nuspec is not null)
            {
                try
                {
                    var x = System.Xml.Linq.XDocument.Load(nuspec);
                    string? Get(string n) => x.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value;
                    version = Get("version");
                    title = Get("title");
                    authors = Get("authors");
                }
                catch { }
            }
            list.Add(new InstalledApp
            {
                Id = "choco:" + id,
                Name = string.IsNullOrWhiteSpace(title) ? id : title,
                Publisher = authors,
                Version = version,
                InstallLocation = dir,
                InstallDate = Directory.GetCreationTime(dir),
                UninstallString = $"choco.exe uninstall {id}",
                QuietUninstallString = $"choco.exe uninstall {id} -y",
                Source = AppSource.Chocolatey,
                Flags = AppFlags.HasQuietUninstall,
            });
        }
        return (IReadOnlyList<InstalledApp>)list;
    }, ct);
}
