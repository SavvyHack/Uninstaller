using System.Text;
using System.Text.RegularExpressions;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Uninstall;

public enum UninstallerType
{
    Unknown,
    Msi,
    Nsis,
    InnoSetup,
    InstallShield,
    WixBurn,
    Squirrel,
    Steam,
    Store,
    BrowserExtension,
    WindowsUpdate,
    PackageManager,
    None,
}

public static partial class UninstallerTypeDetector
{
    public static UninstallerType Detect(InstalledApp app)
    {
        switch (app.Source)
        {
            case AppSource.Store: return UninstallerType.Store;
            case AppSource.BrowserExtension: return UninstallerType.BrowserExtension;
            case AppSource.WindowsUpdate: return UninstallerType.WindowsUpdate;
            case AppSource.Scoop or AppSource.Chocolatey: return UninstallerType.PackageManager;
            case AppSource.Orphan or AppSource.Forced: return UninstallerType.None;
        }
        return Detect(app.UninstallString, app.Has(AppFlags.Msi));
    }

    public static UninstallerType Detect(string? uninstallString, bool isMsi = false)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return isMsi ? UninstallerType.Msi : UninstallerType.None;
        var s = uninstallString;
        if (s.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || isMsi) return UninstallerType.Msi;
        if (s.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) || s.Contains("steam.exe\" steam://", StringComparison.OrdinalIgnoreCase)
            || s.Contains("steam://uninstall", StringComparison.OrdinalIgnoreCase)) return UninstallerType.Steam;
        if (s.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) && s.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
            return UninstallerType.Squirrel;
        if (s.Contains("Package Cache", StringComparison.OrdinalIgnoreCase) && s.Contains("/uninstall", StringComparison.OrdinalIgnoreCase))
            return UninstallerType.WixBurn;
        if (s.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase)
            || s.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase)
            || s.Contains("IsUninst", StringComparison.OrdinalIgnoreCase)) return UninstallerType.InstallShield;

        var exe = PathUtil.ExecutableFromCommand(s);
        var file = exe is null ? "" : Path.GetFileName(exe);
        if (InnoName().IsMatch(file)) return UninstallerType.InnoSetup;
        if (exe is not null && File.Exists(exe))
        {
            var sig = ScanSignature(exe);
            if (sig != UninstallerType.Unknown) return sig;
        }
        return UninstallerType.Unknown;
    }

    /// <summary>Looks for installer-framework markers in the first 1 MB of the uninstaller binary.</summary>
    internal static UninstallerType ScanSignature(string exe)
    {
        try
        {
            using var fs = File.OpenRead(exe);
            var buffer = new byte[Math.Min(fs.Length, 1024 * 1024)];
            var read = fs.Read(buffer, 0, buffer.Length);
            var text = Encoding.Latin1.GetString(buffer, 0, read);
            if (text.Contains("Nullsoft", StringComparison.Ordinal) || text.Contains("NullsoftInst", StringComparison.Ordinal)) return UninstallerType.Nsis;
            if (text.Contains("Inno Setup", StringComparison.Ordinal)) return UninstallerType.InnoSetup;
            if (text.Contains("InstallShield", StringComparison.Ordinal)) return UninstallerType.InstallShield;
            if (text.Contains("WixBundle", StringComparison.Ordinal) || text.Contains(".wixburn", StringComparison.Ordinal)) return UninstallerType.WixBurn;
        }
        catch { }
        return UninstallerType.Unknown;
    }

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoName();
}

public static partial class SilentArgs
{
    /// <summary>
    /// Builds a silent uninstall command. Returns null when no reliable silent switch is known
    /// (the caller then falls back to the interactive uninstaller).
    /// </summary>
    public static string? Build(InstalledApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.QuietUninstallString)) return app.QuietUninstallString;
        var type = UninstallerTypeDetector.Detect(app);
        return Build(type, app.UninstallString, app.ProductCode);
    }

    public static string? Build(UninstallerType type, string? uninstallString, string? productCode)
    {
        if (type == UninstallerType.Msi)
        {
            var code = productCode ?? (uninstallString is null ? null : Guid().Match(uninstallString) is { Success: true } m ? m.Value : null);
            return code is null ? null : $"MsiExec.exe /X{code} /qn /norestart REBOOT=ReallySuppress";
        }
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;
        var (exe, args) = CommandLine.Split(uninstallString);
        var q = CommandLine.Quote(exe);
        return type switch
        {
            UninstallerType.Nsis => Append(q, args, "/S"),
            UninstallerType.InnoSetup => Append(q, args, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"),
            UninstallerType.WixBurn => Append(q, args, "/quiet /norestart"),
            UninstallerType.Squirrel => Append(q, args, "-s"),
            UninstallerType.WindowsUpdate => Append(q, args, "/quiet /norestart"),
            _ => null,
        };
    }

    /// <summary>Converts an MSI "modify/repair" command (/I) into a true removal (/X) — Revo's "Forced MSI uninstall".</summary>
    public static string? ForcedMsi(InstalledApp app)
    {
        var code = app.ProductCode ?? (app.UninstallString is { } u && Guid().Match(u) is { Success: true } m ? m.Value : null);
        return code is null ? null : $"MsiExec.exe /X{code}";
    }

    private static string Append(string exe, string args, string extra)
    {
        var existing = args.Trim();
        foreach (var token in extra.Split(' '))
            if (existing.Contains(token, StringComparison.OrdinalIgnoreCase)) extra = extra.Replace(token, "").Trim();
        return $"{exe} {existing} {extra}".Replace("  ", " ").Trim();
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex Guid();
}
