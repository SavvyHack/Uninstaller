using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

public sealed record BrowserInfo(string Name, string UserDataDir, string ProcessName, bool IsFirefox = false, bool ProfileIsRoot = false, string? PolicyKey = null);

/// <summary>Finds extensions/add-ons in Chromium browsers and Firefox across all profiles, plus policy force-installed ones.</summary>
public sealed class BrowserExtensionProvider : IAppProvider
{
    public string Name => "Browser extensions";

    public static IReadOnlyList<BrowserInfo> KnownBrowsers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            new("Google Chrome", Path.Combine(local, @"Google\Chrome\User Data"), "chrome", PolicyKey: @"SOFTWARE\Policies\Google\Chrome"),
            new("Microsoft Edge", Path.Combine(local, @"Microsoft\Edge\User Data"), "msedge", PolicyKey: @"SOFTWARE\Policies\Microsoft\Edge"),
            new("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "brave", PolicyKey: @"SOFTWARE\Policies\BraveSoftware\Brave"),
            new("Vivaldi", Path.Combine(local, @"Vivaldi\User Data"), "vivaldi"),
            new("Chromium", Path.Combine(local, @"Chromium\User Data"), "chrome", PolicyKey: @"SOFTWARE\Policies\Chromium"),
            new("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable"), "opera", ProfileIsRoot: true),
            new("Opera GX", Path.Combine(roaming, @"Opera Software\Opera GX Stable"), "opera", ProfileIsRoot: true),
            new("Firefox", Path.Combine(roaming, @"Mozilla\Firefox\Profiles"), "firefox", IsFirefox: true),
        ];
    }

    public static IEnumerable<(BrowserInfo Browser, string ProfileDir, string ProfileName)> Profiles()
    {
        foreach (var b in KnownBrowsers())
        {
            if (!Directory.Exists(b.UserDataDir)) continue;
            if (b.ProfileIsRoot)
            {
                yield return (b, b.UserDataDir, "Default");
                continue;
            }
            foreach (var dir in Directory.EnumerateDirectories(b.UserDataDir))
            {
                var n = Path.GetFileName(dir);
                if (b.IsFirefox || n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal))
                    yield return (b, dir, b.IsFirefox ? n[(n.IndexOf('.') + 1)..] : n);
            }
        }
    }

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<InstalledApp>();
        foreach (var (browser, profileDir, profileName) in Profiles())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (browser.IsFirefox) list.AddRange(ReadFirefox(browser, profileDir, profileName));
                else list.AddRange(ReadChromium(browser, profileDir, profileName));
            }
            catch (Exception ex)
            {
                Log.Warn($"Extension scan failed for {browser.Name}/{profileName}: {ex.Message}");
            }
        }
        list.AddRange(ReadPolicyForced());
        return (IReadOnlyList<InstalledApp>)list;
    }, ct);

    private static IEnumerable<InstalledApp> ReadChromium(BrowserInfo browser, string profileDir, string profileName)
    {
        var extRoot = Path.Combine(profileDir, "Extensions");
        if (!Directory.Exists(extRoot)) yield break;
        var states = ReadChromiumStates(profileDir);
        foreach (var extDir in Directory.EnumerateDirectories(extRoot))
        {
            var id = Path.GetFileName(extDir);
            if (id.Equals("Temp", StringComparison.OrdinalIgnoreCase)) continue;
            var versionDir = Directory.EnumerateDirectories(extDir).OrderByDescending(d => d).FirstOrDefault();
            if (versionDir is null) continue;
            var manifestPath = Path.Combine(versionDir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            JsonNode? manifest;
            try { manifest = JsonNode.Parse(File.ReadAllText(manifestPath)); } catch { continue; }
            if (manifest is null) continue;
            var name = Localize(manifest["name"]?.GetValue<string>(), manifest, versionDir) ?? id;
            var desc = Localize(manifest["description"]?.GetValue<string>(), manifest, versionDir);
            // Hosted apps / themes are still removable; mark type in Extra.
            var type = manifest["theme"] is not null ? "Theme" : manifest["app"] is not null ? "App" : "Extension";

            var app = new InstalledApp
            {
                Id = $"ext:{browser.Name}:{profileName}:{id}",
                Name = name,
                Publisher = $"{browser.Name} · {profileName}",
                Version = manifest["version"]?.GetValue<string>(),
                InstallLocation = extDir,
                InstallDate = Directory.GetCreationTime(extDir),
                SizeBytes = FileOps.DirectorySize(extDir),
                DisplayIcon = ResolveChromiumIcon(manifest, versionDir),
                Comments = desc,
                Source = AppSource.BrowserExtension,
                Flags = AppFlags.PerUser,
            };
            app.Extra["Browser"] = browser.Name;
            app.Extra["ProfileDir"] = profileDir;
            app.Extra["ExtensionId"] = id;
            app.Extra["Type"] = type;
            app.Extra["Enabled"] = states.TryGetValue(id, out var enabled) ? enabled.ToString() : "True";
            app.Extra["Permissions"] = string.Join(", ", (manifest["permissions"] as JsonArray)?.Select(p => p?.ToString()).Where(p => p is not null) ?? []);
            yield return app;
        }
    }

    private static Dictionary<string, bool> ReadChromiumStates(string profileDir)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            var p = Path.Combine(profileDir, file);
            if (!File.Exists(p)) continue;
            try
            {
                var settings = JsonNode.Parse(File.ReadAllText(p))?["extensions"]?["settings"] as JsonObject;
                if (settings is null) continue;
                foreach (var (id, node) in settings)
                {
                    if (node is null || result.ContainsKey(id)) continue;
                    var disableReasons = node["disable_reasons"];
                    var state = node["state"]?.GetValue<int>();
                    var disabled = state == 0 || (disableReasons is JsonValue v && v.TryGetValue<int>(out var r) && r != 0)
                                   || (disableReasons is JsonArray a && a.Count > 0);
                    result[id] = !disabled;
                }
            }
            catch { }
        }
        return result;
    }

    private static string? Localize(string? value, JsonNode manifest, string versionDir)
    {
        if (value is null || !value.StartsWith("__MSG_", StringComparison.Ordinal)) return value;
        var key = value[6..].TrimEnd('_');
        var locales = new[] { manifest["default_locale"]?.GetValue<string>(), "en", "en_US" }.Where(l => l is not null).Distinct();
        foreach (var loc in locales)
        {
            var path = Path.Combine(versionDir, "_locales", loc!, "messages.json");
            if (!File.Exists(path)) continue;
            try
            {
                var messages = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var hit = messages?.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
                var msg = hit?["message"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
            catch { }
        }
        return key;
    }

    private static string? ResolveChromiumIcon(JsonNode manifest, string versionDir)
    {
        if (manifest["icons"] is not JsonObject icons) return null;
        var best = icons.Select(kv => (Size: int.TryParse(kv.Key, out var s) ? s : 0, Path: kv.Value?.GetValue<string>()))
            .Where(x => x.Path is not null).OrderBy(x => Math.Abs(x.Size - 48)).FirstOrDefault();
        if (best.Path is null) return null;
        var full = Path.Combine(versionDir, best.Path.TrimStart('/').Replace('/', '\\'));
        return File.Exists(full) ? full : null;
    }

    private static IEnumerable<InstalledApp> ReadFirefox(BrowserInfo browser, string profileDir, string profileName)
    {
        var path = Path.Combine(profileDir, "extensions.json");
        if (!File.Exists(path)) yield break;
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { yield break; }
        if (root?["addons"] is not JsonArray addons) yield break;
        foreach (var a in addons)
        {
            if (a is null) continue;
            var location = a["location"]?.GetValue<string>() ?? "";
            if (location is "app-builtin" or "app-system-defaults" or "app-system-addons") continue;
            var type = a["type"]?.GetValue<string>() ?? "extension";
            if (type is "locale" or "dictionary") continue;
            var id = a["id"]?.GetValue<string>() ?? "";
            var xpi = a["path"]?.GetValue<string>();
            var app = new InstalledApp
            {
                Id = $"ext:{browser.Name}:{profileName}:{id}",
                Name = a["defaultLocale"]?["name"]?.GetValue<string>() ?? id,
                Publisher = $"{browser.Name} · {profileName}",
                Version = a["version"]?.GetValue<string>(),
                Comments = a["defaultLocale"]?["description"]?.GetValue<string>(),
                InstallLocation = xpi,
                InstallDate = a["installDate"]?.GetValue<long>() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null,
                SizeBytes = xpi is not null && File.Exists(xpi) ? new FileInfo(xpi).Length : 0,
                Source = AppSource.BrowserExtension,
                Flags = AppFlags.PerUser,
            };
            app.Extra["Browser"] = browser.Name;
            app.Extra["ProfileDir"] = profileDir;
            app.Extra["ExtensionId"] = id;
            app.Extra["Type"] = type == "theme" ? "Theme" : "Extension";
            app.Extra["Enabled"] = (a["active"]?.GetValue<bool>() ?? true).ToString();
            yield return app;
        }
    }

    /// <summary>Extensions pushed through group policy (a common adware persistence trick).</summary>
    private static IEnumerable<InstalledApp> ReadPolicyForced()
    {
        foreach (var b in KnownBrowsers().Where(b => b.PolicyKey is not null).DistinctBy(b => b.PolicyKey))
        {
            foreach (var hive in new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER" })
            {
                var keyPath = $@"{hive}\{b.PolicyKey}\ExtensionInstallForcelist";
                using var key = RegistryUtil.Open(keyPath);
                if (key is null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    var data = RegistryUtil.GetString(key, valueName) ?? "";
                    var id = data.Split(';')[0];
                    var app = new InstalledApp
                    {
                        Id = $"ext-policy:{keyPath}:{valueName}",
                        Name = $"Policy-forced extension {id}",
                        Publisher = $"{b.Name} · Group Policy",
                        Comments = $"Force-installed via {keyPath}. Update URL: {(data.Contains(';') ? data[(data.IndexOf(';') + 1)..] : "store")}",
                        RegistryKeyPath = keyPath,
                        Source = AppSource.BrowserExtension,
                        Flags = AppFlags.Bundleware,
                    };
                    app.Extra["Browser"] = b.Name;
                    app.Extra["ExtensionId"] = id;
                    app.Extra["PolicyValue"] = valueName;
                    app.Extra["Type"] = "Policy";
                    yield return app;
                }
            }
        }
    }
}
