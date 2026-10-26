using HandyNet.Modules.Inventory.Domain;

namespace HandyNet.Modules.Inventory.Application;

/// <summary>
/// Port for an external lost/stolen device database (e.g. GSMA Device Check). Asked by
/// <see cref="Contracts.IInventory.StageReceiveUnitAsync"/> in addition to the platform's own blacklist.
/// <b>No external database is connected today</b>: the registered default is <see cref="DisabledExternalImeiCheck"/>,
/// which always answers <see cref="ExternalImeiStatus.NotChecked"/>. A real adapter needs a contract and credentials and
/// must be verified against the provider's sandbox before it is enabled.
/// </summary>
public interface IExternalImeiCheck
{
    Task<ExternalImeiStatus> CheckAsync(string imei, CancellationToken ct);
}

/// <summary>Default: no external database. Never blocks and never claims a device is clean.</summary>
public sealed class DisabledExternalImeiCheck : IExternalImeiCheck
{
    public Task<ExternalImeiStatus> CheckAsync(string imei, CancellationToken ct) => Task.FromResult(ExternalImeiStatus.NotChecked);
}
