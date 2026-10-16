namespace Reclaim.Core.Infrastructure;

/// <summary>
/// Resolves where Reclaim stores settings, backups, traces and logs.
/// Portable mode (a <c>portable.flag</c> file next to the executable) keeps everything beside the exe.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> _root = new(ResolveRoot);

    public static string ExeDirectory => AppContext.BaseDirectory;

    public static bool IsPortable => File.Exists(Path.Combine(ExeDirectory, "portable.flag"));

    public static string DataRoot => _root.Value;

    public static string Backups => Ensure(Path.Combine(DataRoot, "Backups"));
    public static string Traces => Ensure(Path.Combine(DataRoot, "Traces"));
    public static string Logs => Ensure(Path.Combine(DataRoot, "Logs"));
    public static string Relocations => Ensure(Path.Combine(DataRoot, "Relocations"));
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string BatchStateFile => Path.Combine(DataRoot, "pending-batch.json");

    /// <summary>Overrides the data root (used by tests).</summary>
    public static string? OverrideRoot { get; set; }

    private static string ResolveRoot()
    {
        if (OverrideRoot is not null) return Ensure(OverrideRoot);
        var root = IsPortable
            ? Path.Combine(ExeDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Reclaim");
        return Ensure(root);
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
