using Microsoft.Win32;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.SystemTools;

public static class EnvironmentPaths
{
    /// <summary>Removes one entry from a PATH value, keeping the rest exactly as written (unexpanded).</summary>
    public static bool RemoveSegment(string keyPath, string segment)
    {
        using var k = RegistryUtil.Open(keyPath, writable: true);
        if (k is null) return false;
        var kind = k.GetValueKind("Path");
        var value = k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = value.Split(';').Where(p => !string.Equals(p.TrimEnd('\\'), segment.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
        var updated = string.Join(";", parts);
        if (updated == value) return true;
        k.SetValue("Path", updated, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        BroadcastEnvironmentChange();
        return true;
    }

    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xffff), 0x1A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment", 2, 2000, out _);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
