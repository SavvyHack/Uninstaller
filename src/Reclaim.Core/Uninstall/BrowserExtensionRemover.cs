using System.Diagnostics;
using System.Text.Json.Nodes;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Models;

namespace Reclaim.Core.Uninstall;

public static class BrowserExtensionRemover
{
    public static bool IsBrowserRunning(InstalledApp ext)
    {
        var browser = BrowserExtensionProvider.KnownBrowsers().FirstOrDefault(b => b.Name == ext.Extra.GetValueOrDefault("Browser"));
        return browser is not null && Process.GetProcessesByName(browser.ProcessName).Length > 0;
    }

    public static void CloseBrowser(InstalledApp ext)
    {
        var browser = BrowserExtensionProvider.KnownBrowsers().FirstOrDefault(b => b.Name == ext.Extra.GetValueOrDefault("Browser"));
        if (browser is null) return;
        foreach (var p in Process.GetProcessesByName(browser.ProcessName))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) p.Kill();
            }
            catch { }
        }
    }

    public static UninstallOutcome Remove(InstalledApp ext, IProgress<string>? log)
    {
        var id = ext.Extra.GetValueOrDefault("ExtensionId") ?? "";
        if (ext.Extra.GetValueOrDefault("Type") == "Policy")
        {
            RegistryUtil.DeleteValue(ext.RegistryKeyPath!, ext.Extra["PolicyValue"]);
            log?.Report($"Removed policy entry for {id}");
            return new(UninstallStatus.Succeeded, 0, "The policy that forces this extension was removed. Restart the browser.");
        }

        if (IsBrowserRunning(ext))
            return new(UninstallStatus.Failed, null, $"Close {ext.Extra.GetValueOrDefault("Browser")} first. It rewrites its settings on exit.");

        var profile = ext.Extra.GetValueOrDefault("ProfileDir");
        if (profile is null) return new(UninstallStatus.Failed, null, "Unknown profile.");
        var isFirefox = ext.Extra.GetValueOrDefault("Browser") == "Firefox";
        try
        {
            if (isFirefox) RemoveFirefox(profile, id, ext.InstallLocation, log);
            else RemoveChromium(profile, id, log);
            return new(UninstallStatus.Succeeded, 0, "Extension removed.");
        }
        catch (Exception ex)
        {
            Log.Error($"Extension removal failed for {id}", ex);
            return new(UninstallStatus.Failed, null, ex.Message);
        }
    }

    private static void RemoveChromium(string profile, string id, IProgress<string>? log)
    {
        var folders = new[]
        {
            Path.Combine(profile, "Extensions", id),
            Path.Combine(profile, "Local Extension Settings", id),
            Path.Combine(profile, "Sync Extension Settings", id),
            Path.Combine(profile, "Managed Extension Settings", id),
            Path.Combine(profile, "Extension State", id),
        };
        foreach (var f in folders.Where(Directory.Exists))
        {
            FileOps.DeletePermanently(f);
            log?.Report($"Deleted {f}");
        }
        foreach (var dir in new[] { "IndexedDB", "Storage\\ext" }.Select(d => Path.Combine(profile, d)).Where(Directory.Exists))
            foreach (var sub in Directory.EnumerateDirectories(dir).Where(d => d.Contains(id, StringComparison.OrdinalIgnoreCase)))
                FileOps.DeletePermanently(sub);

        // Secure Preferences is MAC-protected; only plain Preferences is edited. The browser drops the
        // Secure Preferences entry by itself once the extension folder is gone.
        var prefs = Path.Combine(profile, "Preferences");
        if (File.Exists(prefs))
        {
            var root = JsonNode.Parse(File.ReadAllText(prefs));
            if (root?["extensions"]?["settings"] is JsonObject settings && settings.Remove(id))
            {
                File.Copy(prefs, prefs + ".reclaim.bak", overwrite: true);
                File.WriteAllText(prefs, root.ToJsonString());
                log?.Report("Updated browser preferences");
            }
        }
    }

    private static void RemoveFirefox(string profile, string id, string? xpi, IProgress<string>? log)
    {
        if (xpi is not null && File.Exists(xpi)) { File.Delete(xpi); log?.Report($"Deleted {xpi}"); }
        var unpacked = Path.Combine(profile, "extensions", id);
        if (Directory.Exists(unpacked)) FileOps.DeletePermanently(unpacked);
        var data = Path.Combine(profile, "browser-extension-data", id);
        if (Directory.Exists(data)) FileOps.DeletePermanently(data);

        var json = Path.Combine(profile, "extensions.json");
        if (File.Exists(json))
        {
            var root = JsonNode.Parse(File.ReadAllText(json));
            if (root?["addons"] is JsonArray addons)
            {
                var hit = addons.FirstOrDefault(a => a?["id"]?.GetValue<string>() == id);
                if (hit is not null)
                {
                    addons.Remove(hit);
                    File.WriteAllText(json, root.ToJsonString());
                }
            }
        }
        // Force Firefox to rebuild its add-on startup cache.
        var cache = Path.Combine(profile, "addonStartup.json.lz4");
        if (File.Exists(cache)) File.Delete(cache);
        log?.Report("Updated Firefox add-on database");
    }
}
