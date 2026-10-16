using System.Runtime.CompilerServices;
using Reclaim.Core.Infrastructure;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Reclaim.Core.Tests;

internal static class TestSetup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "ReclaimTests", Guid.NewGuid().ToString("N"));

    /// <summary>Keep settings, backups and logs of the test run away from the real ProgramData folder.</summary>
    [ModuleInitializer]
    internal static void Init() => AppPaths.OverrideRoot = Root;
}
