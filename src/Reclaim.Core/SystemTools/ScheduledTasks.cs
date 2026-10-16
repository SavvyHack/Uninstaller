using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.SystemTools;

public sealed record ScheduledTaskInfo(string Path, string Name, string? Command, string? Arguments, bool Enabled, bool RunsAtLogon, string? Author);

/// <summary>Task Scheduler access through its scripting COM API.</summary>
public static class ScheduledTasks
{
    public static List<ScheduledTaskInfo> List()
    {
        var result = new List<ScheduledTaskInfo>();
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            Walk(service.GetFolder("\\"), result);
        }
        catch (Exception ex)
        {
            Log.Warn($"Task Scheduler unavailable: {ex.Message}");
        }
        return result;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> result)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* TASK_ENUM_HIDDEN */))
            {
                try
                {
                    string? command = null, args = null;
                    dynamic def = task.Definition;
                    foreach (dynamic action in def.Actions)
                    {
                        if ((int)action.Type == 0) // TASK_ACTION_EXEC
                        {
                            command = Environment.ExpandEnvironmentVariables(((string?)action.Path ?? "").Trim('"'));
                            args = (string?)action.Arguments;
                            break;
                        }
                    }
                    var logon = false;
                    foreach (dynamic trigger in def.Triggers)
                        if ((int)trigger.Type is 9 or 8) logon = true; // LOGON / BOOT
                    result.Add(new ScheduledTaskInfo((string)task.Path, (string)task.Name, command, args, (bool)task.Enabled, logon,
                        (string?)def.RegistrationInfo.Author));
                }
                catch { }
            }
            foreach (dynamic sub in folder.GetFolders(0))
            {
                string path = sub.Path;
                if (path.StartsWith(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, result);
            }
        }
        catch { }
    }

    public static string? ExportXml(string taskPath)
    {
        var r = ProcessRunner.Run("schtasks.exe", $"/Query /TN \"{taskPath}\" /XML", TimeSpan.FromSeconds(30));
        return r.ExitCode == 0 ? r.StdOut : null;
    }

    public static bool Delete(string taskPath) =>
        ProcessRunner.Run("schtasks.exe", $"/Delete /TN \"{taskPath}\" /F", TimeSpan.FromSeconds(30)).ExitCode == 0;

    public static bool SetEnabled(string taskPath, bool enabled) =>
        ProcessRunner.Run("schtasks.exe", $"/Change /TN \"{taskPath}\" {(enabled ? "/ENABLE" : "/DISABLE")}", TimeSpan.FromSeconds(30)).ExitCode == 0;

    public static bool ImportXml(string taskPath, string xmlFile) =>
        ProcessRunner.Run("schtasks.exe", $"/Create /TN \"{taskPath}\" /XML \"{xmlFile}\" /F", TimeSpan.FromSeconds(30)).ExitCode == 0;
}
