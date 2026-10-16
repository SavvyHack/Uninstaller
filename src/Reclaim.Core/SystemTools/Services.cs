using Microsoft.Win32;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.SystemTools;

public sealed record ServiceInfo(string Name, string DisplayName, string? ImagePath, int StartType, int Type, string KeyPath);

public static class Services
{
    public const string ServicesKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services";

    public static List<ServiceInfo> List()
    {
        var list = new List<ServiceInfo>();
        using var root = RegistryUtil.Open(ServicesKey);
        if (root is null) return list;
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(name);
                if (k is null) continue;
                var image = RegistryUtil.GetString(k, "ImagePath");
                if (image is null) continue;
                list.Add(new ServiceInfo(name, ResolveDisplayName(k, name), NormalizeImagePath(image),
                    k.GetValue("Start") is int s ? s : -1, k.GetValue("Type") is int t ? t : 0, $@"{ServicesKey}\{name}"));
            }
            catch { }
        }
        return list;
    }

    private static string ResolveDisplayName(RegistryKey k, string fallback)
    {
        var d = RegistryUtil.GetString(k, "DisplayName");
        return string.IsNullOrEmpty(d) || d.StartsWith('@') ? fallback : d;
    }

    /// <summary>Turns "\SystemRoot\System32\drivers\x.sys" or "\??\C:\..." into a normal path.</summary>
    public static string NormalizeImagePath(string image)
    {
        var s = Environment.ExpandEnvironmentVariables(image.Trim());
        if (s.StartsWith(@"\??\")) s = s[4..];
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), s[12..]);
        else if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), s);
        return s;
    }

    public static bool StopAndDelete(string name)
    {
        ProcessRunner.Run("sc.exe", $"stop \"{name}\"", TimeSpan.FromSeconds(30));
        var r = ProcessRunner.Run("sc.exe", $"delete \"{name}\"", TimeSpan.FromSeconds(30));
        return r.ExitCode == 0 || r.StdOut.Contains("1060"); // 1060 = service does not exist
    }

    public static bool SetStartType(string name, string startType) =>
        ProcessRunner.Run("sc.exe", $"config \"{name}\" start= {startType}", TimeSpan.FromSeconds(30)).ExitCode == 0;
}
