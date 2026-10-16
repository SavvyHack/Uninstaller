namespace Reclaim.Core.Infrastructure;

/// <summary>Minimal append-only file log plus an in-process event for live log views.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static event Action<string>? Written;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"reclaim-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the operation being logged.
        }
        Written?.Invoke(line);
    }
}
