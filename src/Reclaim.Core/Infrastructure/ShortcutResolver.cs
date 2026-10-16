using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Reclaim.Core.Infrastructure;

/// <summary>Reads the target of a .lnk file through the shell's IShellLink.</summary>
public static class ShortcutResolver
{
    public static string? GetTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        object? obj = null;
        try
        {
            obj = new ShellLink();
            ((IPersistFile)obj).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)obj).GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var target = Environment.ExpandEnvironmentVariables(sb.ToString());
            if (string.IsNullOrEmpty(target))
            {
                // Advertised (MSI) shortcuts have no path; fall back to the arguments/working dir.
                var wd = new StringBuilder(1024);
                ((IShellLinkW)obj).GetWorkingDirectory(wd, wd.Capacity);
                target = wd.ToString();
            }
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }

    public static string? GetArguments(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new ShellLink();
            ((IPersistFile)obj).Load(lnkPath, 0);
            var sb = new StringBuilder(2048);
            ((IShellLinkW)obj).GetArguments(sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return null; }
        finally
        {
            if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }

    public static void Create(string lnkPath, string target, string? args = null, string? workingDir = null)
    {
        var obj = new ShellLink();
        try
        {
            var link = (IShellLinkW)obj;
            link.SetPath(target);
            if (args is not null) link.SetArguments(args);
            link.SetWorkingDirectory(workingDir ?? Path.GetDirectoryName(target) ?? "");
            ((IPersistFile)obj).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
