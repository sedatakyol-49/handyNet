namespace HandyNet.Modules.Tenancy.Contracts;

/// <summary>Keys of <see cref="OrganizationFacts.Counts"/> shown in the platform administration.</summary>
public static class OrganizationFactKeys
{
    public const string Registers = "registers";
    public const string Offers = "offers";
    public const string PublishedOffers = "publishedOffers";
}

/// <summary>
/// What another module contributes to the platform admin's view of a company: counts and the names of its registers — never
/// operational data (no receipts, customers, sales, Ankauf seller data, prices).
/// </summary>
public sealed record OrganizationFacts(IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> RegisterNames)
{
    public static readonly OrganizationFacts Empty = new(new Dictionary<string, int>(), []);
}

/// <summary>
/// Port implemented by modules that own tenant data (POS: registers, Marketplace: offers), so the company administration can
/// show their counts without Tenancy depending on them. Implementations read through id-only database functions that refuse
/// to run for a tenant session (RLS stays intact for every tenant endpoint).
/// </summary>
public interface IOrganizationFactSource
{
    Task<IReadOnlyDictionary<Guid, OrganizationFacts>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct);
}
