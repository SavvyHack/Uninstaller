using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Leftovers;

public enum ScanMode
{
    /// <summary>Only items that provably belong to the app (its folder, its keys, objects pointing into its folder).</summary>
    Safe,
    /// <summary>Safe plus name-matched folders/keys in the common locations.</summary>
    Moderate,
    /// <summary>Moderate plus a deep reference sweep of the registry, COM, drivers and deeper folders.</summary>
    Advanced,
}

public enum LeftoverKind
{
    Directory,
    File,
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,
    FirewallRule,
    EnvironmentPath,
}

public enum ConfidenceBucket { Certain, Likely, Possible }

public sealed class LeftoverItem
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>File/dir path, full registry key path, service name, task path or firewall rule name.</summary>
    public required string Path { get; init; }

    /// <summary>Registry value name (RegistryValue), or the PATH segment (EnvironmentPath).</summary>
    public string? ValueName { get; init; }

    public required string Category { get; init; }
    public int Confidence { get; set; }
    public List<string> Reasons { get; } = [];
    public long SizeBytes { get; set; }
    public bool Selected { get; set; }

    public ConfidenceBucket Bucket => Confidence >= 80 ? ConfidenceBucket.Certain : Confidence >= 55 ? ConfidenceBucket.Likely : ConfidenceBucket.Possible;

    public string Display => Kind switch
    {
        LeftoverKind.RegistryValue => $"{Path}  →  {(string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName)}",
        LeftoverKind.EnvironmentPath => $"PATH entry: {ValueName}",
        _ => Path,
    };

    public string Reason => string.Join("; ", Reasons);

    public string SizeText => SizeBytes > 0 ? Format.Bytes(SizeBytes) : "";

    public string Key => $"{Kind}|{Path}|{ValueName}".ToLowerInvariant();

    public void Raise(int confidence, string reason)
    {
        if (!Reasons.Contains(reason)) Reasons.Add(reason);
        if (confidence > Confidence) Confidence = Math.Min(100, confidence);
    }

    public override string ToString() => $"[{Confidence}] {Kind}: {Display}";
}
