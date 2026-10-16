using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Reclaim.App.Services;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Reclaim.Core.Uninstall;

namespace Reclaim.App.ViewModels;

public sealed record Badge(string Text, string Tone, string? ToolTip = null);

/// <summary>One row in the app list. Wraps the core model and adds selection, icon and badges.</summary>
public sealed partial class AppItemViewModel : ObservableObject
{
    public InstalledApp Model { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private IReadOnlyList<Badge> _badges = [];

    private bool _iconRequested;

    public AppItemViewModel(InstalledApp model)
    {
        Model = model;
        RebuildBadges();
    }

    public string Name => Model.Name;
    public string Publisher => string.IsNullOrWhiteSpace(Model.Publisher) ? "Unknown publisher" : Model.Publisher!;
    public string? Version => Model.Version;
    public string SizeText => Model.SizeText;
    public long SizeBytes => Model.SizeBytes;
    public DateTime? InstallDate => Model.InstallDate;
    public string DateText => Model.InstallDate?.ToString("d MMM yyyy") ?? "—";
    public string Initial => string.IsNullOrEmpty(Model.Name) ? "?" : Model.Name[..1].ToUpperInvariant();

    public string SourceText => Model.Source switch
    {
        AppSource.Win32 => "Desktop program",
        AppSource.Store => "Windows app",
        AppSource.BrowserExtension => $"{Model.Extra.GetValueOrDefault("Type") ?? "Extension"} · {Model.Extra.GetValueOrDefault("Browser")}",
        AppSource.WindowsUpdate => "Windows update",
        AppSource.Scoop => "Scoop package",
        AppSource.Chocolatey => "Chocolatey package",
        AppSource.Steam => "Steam game",
        AppSource.Orphan => "Leftover folder",
        _ => Model.Source.ToString(),
    };

    public string UninstallerText => UninstallerTypeDetector.Detect(Model) switch
    {
        UninstallerType.Msi => "Windows Installer (MSI)",
        UninstallerType.Nsis => "NSIS",
        UninstallerType.InnoSetup => "Inno Setup",
        UninstallerType.InstallShield => "InstallShield",
        UninstallerType.WixBurn => "WiX bundle",
        UninstallerType.Squirrel => "Squirrel",
        UninstallerType.Steam => "Steam",
        UninstallerType.Store => "Windows app package",
        UninstallerType.BrowserExtension => "Browser extension",
        UninstallerType.WindowsUpdate => "Windows Update (wusa)",
        UninstallerType.PackageManager => "Package manager",
        UninstallerType.None => "None (leftovers only)",
        _ => "Custom uninstaller",
    };

    public bool SupportsQuiet => SilentArgs.Build(Model) is not null || Model.Source is AppSource.Store or AppSource.BrowserExtension;

    public string SignatureText => Model.Signature switch
    {
        null => "Not checked",
        { Status: SignatureStatus.Signed, Signer: { } s } => $"Signed by {s}",
        { Status: SignatureStatus.Signed } => "Signed",
        { Status: SignatureStatus.Unsigned } => "Not signed",
        { Status: SignatureStatus.Invalid } => "Invalid signature",
        { Status: SignatureStatus.Missing } => "File missing",
        _ => "Unknown",
    };

    public string BitnessText => Model.Bitness switch { Bitness.X64 => "64-bit", Bitness.X86 => "32-bit", _ => "—" };

    /// <summary>Loads the icon the first time the row is shown.</summary>
    public async void EnsureIcon()
    {
        if (_iconRequested) return;
        _iconRequested = true;
        Icon = await IconService.LoadAsync(Model.DisplayIcon, Model.MainExecutable ?? PathUtil.ExecutableFromCommand(Model.UninstallString));
    }

    public void Refresh()
    {
        RebuildBadges();
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(SignatureText));
    }

    private void RebuildBadges()
    {
        var b = new List<Badge>();
        var m = Model;
        switch (m.Source)
        {
            case AppSource.Store: b.Add(new("Windows app", "Info")); break;
            case AppSource.BrowserExtension: b.Add(new(m.Extra.GetValueOrDefault("Browser") ?? "Browser", "Info")); break;
            case AppSource.WindowsUpdate: b.Add(new("Update", "Neutral")); break;
            case AppSource.Steam: b.Add(new("Steam", "Info")); break;
            case AppSource.Scoop or AppSource.Chocolatey: b.Add(new(m.Source.ToString(), "Info")); break;
            case AppSource.Orphan: b.Add(new("Leftover folder", "Caution", "No installed program owns this folder")); break;
        }
        if (m.Has(AppFlags.UpdateAvailable)) b.Add(new($"Update {m.AvailableVersion}", "Success", "A newer version is available through winget"));
        if (m.Has(AppFlags.Orphaned) && m.Source != AppSource.Orphan) b.Add(new("Broken", "Caution", "The uninstaller and program folder are missing"));
        if (m.Has(AppFlags.Bundleware)) b.Add(new("Bundleware", "Critical", "Often installed as an unwanted extra"));
        if (m.Has(AppFlags.Unsigned)) b.Add(new("Unsigned", "Caution", "The program's files have no valid digital signature"));
        if (m.Has(AppFlags.Traced)) b.Add(new("Traced", "Accent", "Reclaim recorded this installation"));
        if (m.Has(AppFlags.Relocated)) b.Add(new("Moved", "Accent", $"Moved to {m.Extra.GetValueOrDefault("RelocatedTo")}"));
        if (m.Has(AppFlags.SystemComponent)) b.Add(new("System", "Neutral"));
        if (m.Extra.GetValueOrDefault("Enabled") == "False") b.Add(new("Disabled", "Neutral"));
        if (m.Bitness == Bitness.X86 && m.Source == AppSource.Win32) b.Add(new("32-bit", "Neutral"));
        Badges = b;
    }
}
