using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.SystemTools;

public sealed record FirewallRuleInfo(string Name, string? ApplicationPath, int Direction, bool Enabled);

/// <summary>Windows Firewall rules via the HNetCfg.FwPolicy2 COM API.</summary>
public static class FirewallRules
{
    public static List<FirewallRuleInfo> List()
    {
        var list = new List<FirewallRuleInfo>();
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    string? app = rule.ApplicationName;
                    if (string.IsNullOrEmpty(app)) continue;
                    list.Add(new FirewallRuleInfo((string)rule.Name, Environment.ExpandEnvironmentVariables(app), (int)rule.Direction, (bool)rule.Enabled));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Firewall API unavailable: {ex.Message}");
        }
        return list;
    }

    /// <summary>Removes every rule with this name that targets the given program.</summary>
    public static int Remove(string name, string applicationPath)
    {
        var removed = 0;
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            // Rules.Remove(name) deletes by name only; loop until none of ours remain.
            for (var guard = 0; guard < 32; guard++)
            {
                var found = false;
                foreach (dynamic rule in policy.Rules)
                {
                    if ((string)rule.Name == name && PathUtil.SamePath(Environment.ExpandEnvironmentVariables((string?)rule.ApplicationName ?? ""), applicationPath))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) break;
                policy.Rules.Remove(name);
                removed++;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove firewall rule {name}: {ex.Message}");
        }
        return removed;
    }

    public static bool Add(string name, string applicationPath, int direction)
    {
        var dir = direction == 2 ? "out" : "in";
        return ProcessRunner.Run("netsh.exe", $"advfirewall firewall add rule name=\"{name}\" dir={dir} action=allow program=\"{applicationPath}\" enable=yes",
            TimeSpan.FromSeconds(30)).ExitCode == 0;
    }
}
