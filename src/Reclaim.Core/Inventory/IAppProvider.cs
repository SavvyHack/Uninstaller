using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

public interface IAppProvider
{
    string Name { get; }
    Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct);
}
