using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reclaim.Core.Infrastructure;

namespace Reclaim.App.Services;

/// <summary>Loads app icons from exe/dll/ico resources or image files, cached and frozen for cross-thread use.</summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(4, 4);

    public static async Task<ImageSource?> LoadAsync(string? iconSpec, string? fallbackExe = null)
    {
        var key = iconSpec ?? fallbackExe;
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        await Gate.WaitAsync();
        try
        {
            var image = await Task.Run(() => Load(iconSpec) ?? Load(fallbackExe));
            Cache[key] = image;
            return image;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ImageSource? Load(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var raw = Environment.ExpandEnvironmentVariables(spec.Trim().Trim('"'));
        var index = 0;
        var comma = raw.LastIndexOf(',');
        if (comma > 2 && int.TryParse(raw[(comma + 1)..].Trim(), out var idx))
        {
            index = idx;
            raw = raw[..comma].Trim().Trim('"');
        }
        if (!File.Exists(raw)) return null;

        try
        {
            var ext = Path.GetExtension(raw).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64;
                bmp.UriSource = new Uri(raw);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            var large = new IntPtr[1];
            if (ExtractIconEx(raw, index, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                return FromHIcon(large[0]);

            var info = new SHFILEINFO();
            var r = SHGetFileInfo(raw, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */ | 0x0 /* LARGE */);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero) return FromHIcon(info.hIcon);
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon load failed for {raw}: {ex.Message}");
        }
        return null;
    }

    private static ImageSource FromHIcon(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
