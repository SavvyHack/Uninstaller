using System.Diagnostics;
using System.Text;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Models;

namespace Reclaim.Core.SystemTools;

public sealed record HuntResult(string? ExePath, string Title, int? ProcessId, InstalledApp? App, string Source);

/// <summary>
/// Hunter mode: identifies the program behind whatever is under the cursor, whether that's a window,
/// a desktop shortcut or a taskbar/tray button.
/// </summary>
public static class HunterService
{
    public static HuntResult? Identify(int screenX, int screenY, IReadOnlyCollection<InstalledApp> apps)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT { X = screenX, Y = screenY });
        if (hwnd == IntPtr.Zero) return null;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        var cls = ClassName(root);
        Native.GetWindowThreadProcessId(root, out var pid);
        var ownImage = Native.ProcessImagePath(pid);

        // Desktop or taskbar: ask UI Automation which icon/button is under the cursor.
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")
        {
            var name = UiaNameAt(screenX, screenY);
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (cls is "Progman" or "WorkerW")
            {
                var target = ResolveDesktopItem(name);
                var app = target is null ? null : AppCatalog.FindOwner(apps, target);
                return new HuntResult(target, name, null, app ?? MatchByName(apps, name), "Desktop icon");
            }
            // Taskbar/tray tooltips usually start with the program's name.
            var proc = FindProcessByTitle(name);
            var exe = proc is null ? null : Native.ProcessImagePath((uint)proc.Id);
            var owner = exe is null ? null : AppCatalog.FindOwner(apps, exe);
            return new HuntResult(exe, name, proc?.Id, owner ?? MatchByName(apps, name), cls == "Shell_TrayWnd" ? "Taskbar" : "Notification area");
        }

        if (ownImage is null) return null;
        if (PathUtil.SamePath(ownImage, Environment.ProcessPath)) return null; // ourselves
        return new HuntResult(ownImage, WindowText(root), (int)pid, AppCatalog.FindOwner(apps, ownImage), "Window");
    }

    private static InstalledApp? MatchByName(IEnumerable<InstalledApp> apps, string name)
    {
        var first = name.Split(['\n', '-', '–', ':'], 2)[0].Trim();
        var key = PathUtil.Squash(first);
        return key.Length < 3 ? null : apps.FirstOrDefault(a => PathUtil.Squash(a.Name).StartsWith(key) || key.StartsWith(PathUtil.Squash(a.Name)));
    }

    private static string? ResolveDesktopItem(string name)
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                 })
        {
            foreach (var ext in new[] { ".lnk", ".url", ".exe", "" })
            {
                var path = Path.Combine(folder, name + ext);
                if (!File.Exists(path)) continue;
                return ext == ".lnk" ? ShortcutResolver.GetTarget(path) : path;
            }
        }
        return null;
    }

    private static Process? FindProcessByTitle(string tooltip)
    {
        var first = tooltip.Split(['\n', '-', '–'], 2)[0].Trim();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains(first, StringComparison.OrdinalIgnoreCase)) return p;
                if (p.ProcessName.Equals(first.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { }
        }
        return null;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? UiaNameAt(int x, int y)
    {
        try
        {
            var uia = (IUIAutomation)new CUIAutomation();
            var element = uia.ElementFromPoint(new tagPOINT { x = x, y = y });
            return element?.CurrentName;
        }
        catch
        {
            return null;
        }
    }

    // Minimal UI Automation COM interop (only ElementFromPoint + CurrentName are needed).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct tagPOINT { public int x; public int y; }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation;

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        IUIAutomationElement ElementFromPoint(tagPOINT pt);
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        void GetCurrentPattern();
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        void get_CurrentProcessId();
        void get_CurrentControlType();
        void get_CurrentLocalizedControlType();
        string CurrentName { [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.BStr)] get; }
    }
}
