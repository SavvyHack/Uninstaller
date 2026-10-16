using Microsoft.Win32;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Monitor;

/// <summary>
/// Compact picture of the registry: every key plus hashes of its value names and data. Two snapshots
/// diff into "keys created", "values created" and "values changed" without holding the data in memory.
/// </summary>
public sealed class RegistrySnapshot
{
    internal sealed record KeyState(long DataHash, HashSet<long> ValueNameHashes);

    internal Dictionary<string, KeyState> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] DefaultRoots =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE",
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services",
        @"HKEY_CURRENT_USER\Software",
        @"HKEY_CURRENT_USER\Environment",
    ];

    private static readonly string[] Skip =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SideBySide",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Search",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SystemCertificates",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CloudStore",
        @"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
        @"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
    ];

    public static RegistrySnapshot Capture(IEnumerable<string>? roots = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var snap = new RegistrySnapshot();
        foreach (var root in roots ?? DefaultRoots)
        {
            progress?.Report($"Recording {root}…");
            using var key = RegistryUtil.Open(root);
            if (key is not null) snap.Walk(key, root, ct);
        }
        return snap;
    }

    private void Walk(RegistryKey key, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Skip.Any(s => s.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

        var names = new HashSet<long>();
        long dataHash = 17;
        try
        {
            foreach (var v in key.GetValueNames())
            {
                var nh = Hash(v);
                names.Add(nh);
                object? data = null;
                try { data = key.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { }
                dataHash = unchecked(dataHash * 31 + nh * 7 + HashData(data));
            }
        }
        catch { }
        Keys[path] = new KeyState(dataHash, names);

        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { return; }
        foreach (var s in subs)
        {
            RegistryKey? child;
            try { child = key.OpenSubKey(s); } catch { continue; }
            if (child is null) continue;
            using (child) Walk(child, $@"{path}\{s}", ct);
        }
    }

    public sealed record RegistryDiff(List<string> CreatedKeys, List<(string Key, string Value)> CreatedValues, List<(string Key, string Value)> ChangedValues);

    /// <summary>Changes from <paramref name="before"/> to this snapshot. Created keys are reduced to their top-most new ancestor.</summary>
    public RegistryDiff DiffFrom(RegistrySnapshot before)
    {
        var created = Keys.Keys.Where(k => !before.Keys.ContainsKey(k)).OrderBy(k => k.Length).ToList();
        var topMost = new List<string>();
        var createdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in created)
        {
            var parent = k[..Math.Max(0, k.LastIndexOf('\\'))];
            if (!createdSet.Contains(parent)) topMost.Add(k);
            createdSet.Add(k);
        }

        var newValues = new List<(string, string)>();
        var changedValues = new List<(string, string)>();
        foreach (var (path, state) in Keys)
        {
            if (!before.Keys.TryGetValue(path, out var old) || old.DataHash == state.DataHash) continue;
            using var key = RegistryUtil.Open(path);
            if (key is null) continue;
            foreach (var v in key.GetValueNames())
            {
                if (!old.ValueNameHashes.Contains(Hash(v))) newValues.Add((path, v));
                else changedValues.Add((path, v));
            }
        }
        return new RegistryDiff(topMost, newValues, changedValues);
    }

    private static long Hash(string s)
    {
        unchecked
        {
            long h = 1469598103934665603;
            foreach (var c in s.ToLowerInvariant()) h = (h ^ c) * 1099511628211;
            return h;
        }
    }

    private static long HashData(object? data) => data switch
    {
        null => 0,
        string s => Hash(s),
        string[] arr => arr.Aggregate(3L, (a, s) => unchecked(a * 31 + Hash(s))),
        byte[] b => b.Aggregate(5L, (a, x) => unchecked(a * 31 + x)),
        _ => Hash(data.ToString() ?? ""),
    };
}
