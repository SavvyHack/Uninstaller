using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

/// <summary>Reads classic "Programs and Features" entries from every Uninstall key: HKLM (32/64-bit), HKCU and loaded user hives.</summary>
public sealed class RegistryUninstallProvider : IAppProvider
{
    public const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallSubKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public string Name => "Programs";

    public bool IncludeSystemComponents { get; init; }
    public bool IncludeUpdates { get; init; }

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct) => Task.Run(() => (IReadOnlyList<InstalledApp>)Read(ct), ct);

    /// <summary>All Uninstall roots as full registry paths with their bitness and per-user marker.</summary>
    public static IEnumerable<(string Path, Bitness Bitness, bool PerUser)> UninstallRoots()
    {
        yield return (@"HKEY_LOCAL_MACHINE\" + UninstallSubKey, Bitness.X64, false);
        yield return (@"HKEY_LOCAL_MACHINE\" + UninstallSubKeyWow, Bitness.X86, false);
        yield return (@"HKEY_CURRENT_USER\" + UninstallSubKey, Bitness.Unknown, true);
        yield return (@"HKEY_CURRENT_USER\" + UninstallSubKeyWow, Bitness.X86, true);

        var current = WindowsIdentity.GetCurrent().User?.Value;
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        foreach (var sid in users.GetSubKeyNames())
        {
            if (!sid.StartsWith("S-1-5-21-") || sid.EndsWith("_Classes") || sid == current) continue;
            yield return ($@"HKEY_USERS\{sid}\{UninstallSubKey}", Bitness.Unknown, true);
        }
    }

    private List<InstalledApp> Read(CancellationToken ct)
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rootPath, bitness, perUser) in UninstallRoots())
        {
            using var root = RegistryUtil.Open(rootPath);
            if (root is null) continue;
            foreach (var keyName in root.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(keyName);
                if (key is null) continue;
                var app = FromKey(key, $@"{rootPath}\{keyName}", keyName, bitness, perUser);
                if (app is null) continue;
                if (app.Has(AppFlags.SystemComponent) && !IncludeSystemComponents) continue;
                if (app.Has(AppFlags.Update) && !IncludeUpdates) continue;
                // De-duplicate identical name+version listed in both views or both hives.
                var dedupe = $"{app.Name}|{app.Version}|{app.Publisher}";
                if (!apps.ContainsKey(dedupe)) apps[dedupe] = app;
            }
        }
        return apps.Values.ToList();
    }

    public static InstalledApp? FromKey(RegistryKey key, string keyPath, string keyName, Bitness bitness, bool perUser)
    {
        var name = RegistryUtil.GetString(key, "DisplayName")?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        var uninstall = RegistryUtil.GetString(key, "UninstallString");
        var quiet = RegistryUtil.GetString(key, "QuietUninstallString");
        var parent = RegistryUtil.GetString(key, "ParentKeyName");
        var releaseType = RegistryUtil.GetString(key, "ReleaseType") ?? "";
        var flags = AppFlags.None;

        if (key.GetValue("SystemComponent") is int sc && sc == 1) flags |= AppFlags.SystemComponent;
        if (!string.IsNullOrEmpty(parent) || releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) flags |= AppFlags.Update;
        if (key.GetValue("WindowsInstaller") is int wi && wi == 1) flags |= AppFlags.Msi;
        if (!string.IsNullOrEmpty(quiet)) flags |= AppFlags.HasQuietUninstall;
        if (key.GetValue("NoRemove") is int nr && nr == 1) flags |= AppFlags.NonRemovable;
        if (perUser) flags |= AppFlags.PerUser;
        if (string.IsNullOrEmpty(uninstall) && !flags.HasFlag(AppFlags.Msi)) flags |= AppFlags.NonRemovable;

        var productCode = Guid.TryParse(keyName, out _) ? keyName : null;
        if (productCode is null && uninstall is not null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(uninstall, @"\{[0-9A-Fa-f\-]{36}\}");
            if (m.Success && uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) productCode = m.Value;
        }

        var size = key.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
        var location = PathUtil.Clean(RegistryUtil.GetString(key, "InstallLocation"));

        var app = new InstalledApp
        {
            Id = "reg:" + keyPath,
            Name = name,
            Publisher = RegistryUtil.GetString(key, "Publisher")?.Trim(),
            Version = RegistryUtil.GetString(key, "DisplayVersion")?.Trim(),
            InstallDate = ParseDate(RegistryUtil.GetString(key, "InstallDate")),
            SizeBytes = size,
            InstallLocation = location,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            ModifyPath = RegistryUtil.GetString(key, "ModifyPath"),
            DisplayIcon = RegistryUtil.GetString(key, "DisplayIcon"),
            RegistryKeyPath = keyPath,
            ProductCode = productCode,
            HelpLink = RegistryUtil.GetString(key, "HelpLink"),
            AboutLink = RegistryUtil.GetString(key, "URLInfoAbout"),
            UpdateLink = RegistryUtil.GetString(key, "URLUpdateInfo"),
            Comments = RegistryUtil.GetString(key, "Comments"),
            Bitness = bitness,
            Source = AppSource.Win32,
            Flags = flags,
        };
        app.Extra["KeyName"] = keyName;

        if (keyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
        {
            app.Source = AppSource.Steam;
            app.Extra["SteamAppId"] = keyName["Steam App ".Length..];
        }

        if (app.Bitness == Bitness.Unknown)
        {
            var probe = app.InstallLocation ?? PathUtil.ExecutableFromCommand(uninstall);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (probe is not null) app.Bitness = PathUtil.IsUnder(probe, pf86) ? Bitness.X86 : Bitness.X64;
        }

        var written = InstallDateFromKey(key);
        if (written is not null) app.Extra["KeyWriteTime"] = written.Value.ToString("o");
        app.InstallDate ??= written;
        return app;
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    /// <summary>Falls back to the registry key's last write time (NtQueryKey) when the app didn't record InstallDate.</summary>
    private static DateTime? InstallDateFromKey(RegistryKey key)
    {
        try
        {
            if (RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ft) == 0)
                return DateTime.FromFileTime(ft);
        }
        catch { }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    private static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcClass,
        IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLen, IntPtr maxClassLen, IntPtr values, IntPtr maxValueNameLen,
        IntPtr maxValueLen, IntPtr securityDescriptor, out long lastWriteTime);
}
