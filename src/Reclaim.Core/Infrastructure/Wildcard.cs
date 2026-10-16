using System.Text.RegularExpressions;

namespace Reclaim.Core.Infrastructure;

/// <summary>Case-insensitive "*" and "?" matching for exclusion and junk patterns.</summary>
public static class Wildcard
{
    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string input, string pattern)
    {
        Regex rx;
        lock (Cache)
        {
            if (!Cache.TryGetValue(pattern, out rx!))
            {
                var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
                rx = new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                Cache[pattern] = rx;
            }
        }
        return rx.IsMatch(input);
    }
}
