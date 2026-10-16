using System.Text.RegularExpressions;

namespace Reclaim.Core.Infrastructure;

public static partial class PathUtil
{
    /// <summary>Expands env vars, strips quotes/arguments/icon indexes and normalizes a path. Returns null if unusable.</summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            s = end > 0 ? s[1..end] : s.Trim('"');
        }
        // "C:\x\app.exe,0" icon index
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        s = s.Trim().TrimEnd('\\');
        if (s.Length < 3 || !(char.IsLetter(s[0]) && s[1] == ':') && !s.StartsWith(@"\\")) return null;
        try
        {
            return Path.GetFullPath(s);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the executable path from a command line such as an UninstallString.</summary>
    public static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var (exe, _) = CommandLine.Split(command);
        return Clean(exe);
    }

    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        var p = path.TrimEnd('\\') + "\\";
        var r = root.TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower-case alphanumeric form used for fuzzy name comparisons ("Foo-Bar 2.0" → "foobar20").</summary>
    public static string Squash(string? s) =>
        s is null ? "" : NonAlnum().Replace(s, "").ToLowerInvariant();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnum();
}

public static class CommandLine
{
    /// <summary>
    /// Splits a Windows command line into executable and arguments. Handles quoted paths and the common
    /// unquoted "C:\Program Files\X\uninst.exe /S" form by probing for the first existing ".exe" prefix.
    /// </summary>
    public static (string Exe, string Args) Split(string command)
    {
        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 0) return (s[1..end], s[(end + 1)..].Trim());
            return (s.Trim('"'), "");
        }

        // Unquoted: prefer the first ".exe"-style boundary whose prefix exists on disk, else the first boundary.
        var lower = s.ToLowerInvariant();
        (string, string)? firstBoundary = null;
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
        {
            var idx = 0;
            while ((idx = lower.IndexOf(ext, idx, StringComparison.Ordinal)) >= 0)
            {
                var end = idx + ext.Length;
                if (end == s.Length || s[end] == ' ')
                {
                    var split = (s[..end], s[end..].Trim());
                    if (File.Exists(Environment.ExpandEnvironmentVariables(split.Item1))) return split;
                    firstBoundary ??= split;
                }
                idx = end;
            }
        }
        if (firstBoundary is { } fb) return fb;

        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    public static string Quote(string path) => path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;
}
