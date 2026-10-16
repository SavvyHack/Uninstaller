namespace Reclaim.Core.Monitor;

public sealed class TraceValue
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Everything an installer created, recorded while it ran. Shareable as JSON.</summary>
public sealed class TraceLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string Installer { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public string Method { get; set; } = "";
    public List<string> Directories { get; set; } = [];
    public List<string> Files { get; set; } = [];
    public List<string> RegistryKeys { get; set; } = [];
    public List<TraceValue> RegistryValues { get; set; } = [];
    public List<TraceValue> ChangedValues { get; set; } = [];
    public List<string> Services { get; set; } = [];
    public List<string> ScheduledTasks { get; set; } = [];
    public List<string> FirewallRules { get; set; } = [];
    public List<string> LinkedUninstallKeys { get; set; } = [];
    public bool Imported { get; set; }

    public int TotalChanges => Directories.Count + Files.Count + RegistryKeys.Count + RegistryValues.Count + Services.Count + ScheduledTasks.Count + FirewallRules.Count;
}
