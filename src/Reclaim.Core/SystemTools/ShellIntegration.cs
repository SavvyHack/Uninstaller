using Microsoft.Win32;

namespace Reclaim.Core.SystemTools;

/// <summary>Adds "Uninstall with Reclaim" to Explorer's menu for programs and shortcuts (per user, no admin rights needed).</summary>
public static class ShellIntegration
{
    private static readonly string[] Targets = [@"Software\Classes\exefile\shell\ReclaimUninstall", @"Software\Classes\lnkfile\shell\ReclaimUninstall"];

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Targets[0]);
            return k is not null;
        }
    }

    public static void Enable(string exePath)
    {
        foreach (var t in Targets)
        {
            using var k = Registry.CurrentUser.CreateSubKey(t);
            k.SetValue("", "Uninstall with Reclaim");
            k.SetValue("Icon", $"\"{exePath}\",0");
            using var cmd = k.CreateSubKey("command");
            cmd.SetValue("", $"\"{exePath}\" --uninstall-target \"%1\"");
        }
    }

    public static void Disable()
    {
        foreach (var t in Targets) Registry.CurrentUser.DeleteSubKeyTree(t, false);
    }
}
