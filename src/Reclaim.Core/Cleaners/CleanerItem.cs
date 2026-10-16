using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Cleaners;

public enum TargetKind
{
    /// <summary>Delete everything inside a folder, keep the folder.</summary>
    FolderContents,
    /// <summary>Delete files matching a pattern in a folder (non-recursive unless Recursive).</summary>
    FilePattern,
    /// <summary>Delete a single file or folder.</summary>
    Path,
    /// <summary>Delete all values in a key (optionally only names matching Pattern).</summary>
    RegistryValues,
    /// <summary>Delete all subkeys of a key (keep the key).</summary>
    RegistrySubKeys,
    /// <summary>Delete one registry value.</summary>
    RegistryValue,
    RecycleBin,
    Clipboard,
}

public sealed record CleanTarget(TargetKind Kind, string Location, string? Pattern = null, bool Recursive = false);

/// <summary>One checkbox in a cleaner ("Chrome · Default · Cache").</summary>
public sealed class CleanerItem
{
    public required string Group { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<CleanTarget> Targets { get; init; }
    public bool Selected { get; set; } = true;
    public long SizeBytes { get; set; }
    public int Count { get; set; }
    public bool RequiresClosedProcess => ProcessName is not null;
    public string? ProcessName { get; init; }
    public bool StopServiceFirst { get; init; }
    public string? ServiceName { get; init; }

    public string SizeText => SizeBytes > 0 ? Format.Bytes(SizeBytes) : Count > 0 ? $"{Count} entries" : "Nothing to clean";
}

public sealed record CleanerRunResult(int ItemsCleaned, long BytesFreed, List<string> Skipped);
