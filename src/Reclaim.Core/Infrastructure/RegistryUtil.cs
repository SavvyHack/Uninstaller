using Microsoft.Win32;

namespace Reclaim.Core.Infrastructure;

/// <summary>Helpers for working with registry keys addressed by their full textual path.</summary>
public static class RegistryUtil
{
    public static (RegistryHive Hive, string SubKey) ParseHive(string fullPath)
    {
        var idx = fullPath.IndexOf('\\');
        var root = (idx < 0 ? fullPath : fullPath[..idx]).ToUpperInvariant();
        var sub = idx < 0 ? "" : fullPath[(idx + 1)..];
        var hive = root switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            "HKEY_CLASSES_ROOT" or "HKCR" => RegistryHive.ClassesRoot,
            "HKEY_USERS" or "HKU" => RegistryHive.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => RegistryHive.CurrentConfig,
            _ => throw new ArgumentException($"Unknown registry hive in '{fullPath}'"),
        };
        return (hive, sub);
    }

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
        _ => hive.ToString(),
    };

    /// <summary>Opens a key. Paths containing WOW6432Node are opened in the 64-bit view so they resolve literally.</summary>
    public static RegistryKey? Open(string fullPath, bool writable = false, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            var (hive, sub) = ParseHive(fullPath);
            using var root = RegistryKey.OpenBaseKey(hive, view);
            return sub.Length == 0 ? root : root.OpenSubKey(sub, writable);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static bool KeyExists(string fullPath)
    {
        using var k = Open(fullPath);
        return k is not null;
    }

    public static bool ValueExists(string keyPath, string valueName)
    {
        using var k = Open(keyPath);
        return k?.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase) == true;
    }

    public static void DeleteKeyTree(string fullPath)
    {
        var (hive, sub) = ParseHive(fullPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    public static void DeleteValue(string keyPath, string valueName)
    {
        using var k = Open(keyPath, writable: true);
        k?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static string? GetString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.None) switch
        {
            string s => s,
            string[] arr => string.Join(";", arr),
            null => null,
            var o => o.ToString(),
        };

    /// <summary>Exports a key (with subkeys) to a .reg file using reg.exe. Returns true on success.</summary>
    public static bool Export(string fullPath, string regFile)
    {
        var r = ProcessRunner.Run("reg.exe", $"export \"{fullPath}\" \"{regFile}\" /y /reg:64", TimeSpan.FromMinutes(5));
        return r.ExitCode == 0 && File.Exists(regFile);
    }

    public static bool Import(string regFile)
    {
        var r = ProcessRunner.Run("reg.exe", $"import \"{regFile}\" /reg:64", TimeSpan.FromMinutes(5));
        return r.ExitCode == 0;
    }

    /// <summary>Writes a single-value .reg file so a deleted value can be restored.</summary>
    public static bool ExportValue(string keyPath, string valueName, string regFile)
    {
        using var k = Open(keyPath);
        if (k is null) return false;
        var kind = k.GetValueKind(valueName);
        var data = k.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var name = valueName.Length == 0 ? "@" : $"\"{Escape(valueName)}\"";
        string? encoded = kind switch
        {
            RegistryValueKind.String => $"\"{Escape((string)data!)}\"",
            RegistryValueKind.DWord => $"dword:{Convert.ToUInt32(data):x8}",
            RegistryValueKind.ExpandString => "hex(2):" + HexBytes(System.Text.Encoding.Unicode.GetBytes((string)data! + "\0")),
            RegistryValueKind.MultiString => "hex(7):" + HexBytes(System.Text.Encoding.Unicode.GetBytes(string.Join("\0", (string[])data!) + "\0\0")),
            RegistryValueKind.QWord => "hex(b):" + HexBytes(BitConverter.GetBytes(Convert.ToUInt64(data))),
            RegistryValueKind.Binary => "hex:" + HexBytes((byte[])data!),
            _ => null,
        };
        if (encoded is null) return false;
        File.WriteAllText(regFile, $"Windows Registry Editor Version 5.00\r\n\r\n[{keyPath}]\r\n{name}={encoded}\r\n", System.Text.Encoding.Unicode);
        return true;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string HexBytes(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2")));

    /// <summary>Launches regedit focused on the given key.</summary>
    public static void OpenInRegedit(string fullPath)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
            k.SetValue("LastKey", "Computer\\" + fullPath);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("regedit")) p.Kill();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("regedit.exe", "/m") { UseShellExecute = true });
    }
}
