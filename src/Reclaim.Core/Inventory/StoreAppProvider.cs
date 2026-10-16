using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Reclaim.Core.Inventory;

/// <summary>Lists Microsoft Store / MSIX / built-in Windows apps for the current user.</summary>
public sealed class StoreAppProvider : IAppProvider
{
    public string Name => "Windows apps";

    public bool IncludeFrameworks { get; init; }

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<InstalledApp>();
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(string.Empty))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (pkg.IsResourcePackage) continue;
                    if (pkg.IsFramework && !IncludeFrameworks) continue;

                    var id = pkg.Id;
                    string? display = null, publisher = null, logo = null, location = null;
                    try { display = pkg.DisplayName; } catch { }
                    try { publisher = pkg.PublisherDisplayName; } catch { }
                    try { logo = ResolveLogo(pkg.Logo?.LocalPath); } catch { }
                    try { location = pkg.InstalledLocation?.Path; } catch { }
                    if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        display = id.Name;

                    var flags = AppFlags.PerUser;
                    if (pkg.IsFramework) flags |= AppFlags.Framework;
                    if (pkg.SignatureKind == PackageSignatureKind.System) flags |= AppFlags.SystemComponent | AppFlags.NonRemovable;
                    else if (pkg.IsBundle) { }

                    var app = new InstalledApp
                    {
                        Id = "store:" + id.FullName,
                        Name = display,
                        Publisher = publisher,
                        Version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}",
                        InstallDate = SafeDate(pkg),
                        InstallLocation = location,
                        DisplayIcon = logo,
                        Source = AppSource.Store,
                        Bitness = id.Architecture switch
                        {
                            Windows.System.ProcessorArchitecture.X86 => Bitness.X86,
                            Windows.System.ProcessorArchitecture.X64 => Bitness.X64,
                            _ => Bitness.Unknown,
                        },
                        Flags = flags,
                        Signature = new SignatureInfo(pkg.SignatureKind is PackageSignatureKind.None or PackageSignatureKind.Developer
                            ? SignatureStatus.Unsigned : SignatureStatus.Signed, publisher),
                    };
                    app.Extra["PackageFullName"] = id.FullName;
                    app.Extra["PackageFamilyName"] = id.FamilyName;
                    app.Extra["SignatureKind"] = pkg.SignatureKind.ToString();
                    list.Add(app);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipping package: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Store app enumeration failed", ex);
        }
        return (IReadOnlyList<InstalledApp>)list;
    }, ct);

    private static DateTime? SafeDate(Windows.ApplicationModel.Package pkg)
    {
        try { return pkg.InstalledDate.LocalDateTime; } catch { return null; }
    }

    /// <summary>Store logos are declared as "Assets\Logo.png" but shipped as "Logo.scale-100.png" etc.</summary>
    private static string? ResolveLogo(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var candidates = Directory.GetFiles(dir, stem + ".*" + Path.GetExtension(path));
        return candidates.OrderBy(c => c.Contains("scale-100") ? 0 : c.Contains("scale-200") ? 1 : 2).FirstOrDefault();
    }
}
