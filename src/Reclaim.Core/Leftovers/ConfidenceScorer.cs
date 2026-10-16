using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Leftovers;

public readonly record struct Score(int Value, string Reason)
{
    public static readonly Score None = new(0, "");
    public bool IsMatch => Value > 0;
}

/// <summary>
/// Rates how strongly a folder/key/task name points at the app. Scores map to buckets:
/// 80+ Certain, 55–79 Likely, 30–54 Possible. Anything lower is not reported.
/// </summary>
public static class ConfidenceScorer
{
    public const int ReportThreshold = 30;

    public static Score ScoreName(string candidate, AppFingerprint fp)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return Score.None;
        var raw = candidate.Trim();
        var stem = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
        var key = PathUtil.Squash(AppFingerprint.CleanName(stem));
        if (key.Length < 3) return Score.None;
        if (AppFingerprint.GenericTokens.Contains(key)) return Score.None;

        if (fp.IdentityKeys.Contains(key))
            return new(90, "Name matches the program");
        if (fp.ExeNames.Any(e => PathUtil.Squash(Path.GetFileNameWithoutExtension(e)) == key && key.Length >= 4))
            return new(78, "Name matches one of the program's executables");

        foreach (var id in fp.IdentityKeys)
        {
            if (id.Length < 5 || key.Length < 5) continue;
            if (key.StartsWith(id) || id.StartsWith(key))
            {
                var ratio = (double)Math.Min(key.Length, id.Length) / Math.Max(key.Length, id.Length);
                if (ratio >= 0.6) return new(70, "Name closely matches the program");
                if (ratio >= 0.4) return new(50, "Name partially matches the program");
            }
        }

        if (fp.PublisherKey is { } pub && key == pub)
            return new(35, "Publisher folder");

        var tokens = AppFingerprint.Tokenize(AppFingerprint.CleanName(stem));
        var shared = tokens.Intersect(fp.NameTokens).Where(t => t.Length >= 4).ToList();
        if (shared.Count > 0 && fp.NameTokens.Count > 0)
        {
            var coverage = (double)shared.Count / fp.NameTokens.Count;
            var value = coverage >= 0.99 ? 65 : 35 + (int)(coverage * 30);
            return new(value, $"Shares the word{(shared.Count > 1 ? "s" : "")} \"{string.Join("\", \"", shared)}\"");
        }
        return Score.None;
    }

    /// <summary>True when a string (registry data, shortcut target, command line) points into the install folder.</summary>
    public static bool ReferencesInstallDir(string? data, AppFingerprint fp)
    {
        if (string.IsNullOrEmpty(data) || fp.InstallDir is null) return false;
        var expanded = data.Contains('%') ? Environment.ExpandEnvironmentVariables(data) : data;
        var idx = expanded.IndexOf(fp.InstallDir, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var end = idx + fp.InstallDir.Length;
        // Must be the folder itself or followed by a separator, not "C:\Apps\Foo" matching "C:\Apps\FooBar".
        return end == expanded.Length || expanded[end] is '\\' or '"' or ' ' or ',' or ';' or '/';
    }
}
