using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Models;

public enum AppSource
{
    Win32,
    Store,
    BrowserExtension,
    WindowsUpdate,
    Scoop,
    Chocolatey,
    Steam,
    Orphan,
    Traced,
    Forced,
}

public enum Bitness { Unknown, X86, X64 }

[Flags]
public enum AppFlags
{
    None = 0,
    SystemComponent = 1 << 0,
    Update = 1 << 1,
    Orphaned = 1 << 2,
    Unsigned = 1 << 3,
    Bundleware = 1 << 4,
    UpdateAvailable = 1 << 5,
    Msi = 1 << 6,
    HasQuietUninstall = 1 << 7,
    Framework = 1 << 8,
    NonRemovable = 1 << 9,
    PerUser = 1 << 10,
    Relocated = 1 << 11,
    Traced = 1 << 12,
}

/// <summary>One removable item, regardless of where it came from (registry, Store, browser, package manager...).</summary>
public sealed class InstalledApp
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public DateTime? InstallDate { get; set; }
    public long SizeBytes { get; set; }
    public string? InstallLocation { get; set; }
    public string? UninstallString { get; set; }
    public string? QuietUninstallString { get; set; }
    public string? ModifyPath { get; set; }
    public string? DisplayIcon { get; set; }
    public string? RegistryKeyPath { get; set; }
    public string? ProductCode { get; set; }
    public string? HelpLink { get; set; }
    public string? AboutLink { get; set; }
    public string? UpdateLink { get; set; }
    public string? Comments { get; set; }
    public Bitness Bitness { get; set; }
    public AppSource Source { get; set; }
    public AppFlags Flags { get; set; }
    public SignatureInfo? Signature { get; set; }
    public string? AvailableVersion { get; set; }
    public string? WingetId { get; set; }

    /// <summary>Source-specific data, e.g. Store package full name, browser + profile + extension id, KB number.</summary>
    public Dictionary<string, string> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(AppFlags flag) => (Flags & flag) == flag;

    public string SizeText => SizeBytes > 0 ? Format.Bytes(SizeBytes) : "—";

    public string? MainExecutable
    {
        get
        {
            var icon = PathUtil.Clean(DisplayIcon);
            if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon)) return icon;
            return null;
        }
    }

    public override string ToString() => $"{Name} {Version}".Trim();
}
