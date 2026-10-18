using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.Modules.Tenancy.Contracts;

/// <summary>Platform-admin read API: organizations that have a module enabled (e.g. POS for the fiscal administration).</summary>
public interface IOrganizationModuleDirectory
{
    /// <summary>Organizations with <paramref name="module"/> enabled, by name; optional case-insensitive name/city search.</summary>
    Task<IReadOnlyList<OrganizationSummary>> ListWithModuleAsync(PlatformModule module, string? search, int limit, CancellationToken ct);
}
