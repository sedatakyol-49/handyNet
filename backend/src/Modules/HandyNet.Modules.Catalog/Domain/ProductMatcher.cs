namespace HandyNet.Modules.Catalog.Domain;

public sealed record ProductRef(Guid Id, string Brand, string Name, string BrandKey, string? MpnKey, string? Ean, PartQuality Quality);

/// <summary>Identifying attributes of an incoming product line (from an import row or manual entry).</summary>
public sealed record ProductKey(string BrandKey, string? MpnKey, string? Ean, PartQuality Quality)
{
    public string? IdentityKey => MpnKey is null ? null : IdentityKeyOf(BrandKey, MpnKey, Quality);

    public static string IdentityKeyOf(string brandKey, string mpnKey, PartQuality quality) => $"{brandKey}|{mpnKey}|{quality}";
}

public enum MatchKind
{
    Existing,
    New,
    Conflict,
}

public sealed record MatchResult(MatchKind Kind, Guid? ProductId, string? Reason)
{
    public static MatchResult New() => new(MatchKind.New, null, null);
    public static MatchResult Existing(Guid id) => new(MatchKind.Existing, id, null);
    public static MatchResult Conflict(string reason) => new(MatchKind.Conflict, null, reason);
}

/// <summary>
/// Decides whether an incoming line refers to an existing canonical product. Conservative by design:
/// when identifiers disagree it reports a conflict for human review instead of merging, because a
/// wrong merge would put offers for incompatible or lower-quality parts under the same product.
/// </summary>
public sealed class ProductMatcher
{
    private readonly Dictionary<string, ProductRef> _byEan = [];
    private readonly Dictionary<string, ProductRef> _byIdentity = [];

    public ProductMatcher(IEnumerable<ProductRef> candidates)
    {
        foreach (var candidate in candidates)
            Add(candidate);
    }

    /// <summary>Makes a product created during the current operation matchable by later lines.</summary>
    public void Add(ProductRef product)
    {
        if (product.Ean is not null)
            _byEan.TryAdd(product.Ean, product);
        if (product.MpnKey is not null)
            _byIdentity.TryAdd(ProductKey.IdentityKeyOf(product.BrandKey, product.MpnKey, product.Quality), product);
    }

    public MatchResult Match(ProductKey key)
    {
        if (key.Ean is not null && _byEan.TryGetValue(key.Ean, out var byEan))
        {
            if (byEan.Quality != key.Quality)
                return MatchResult.Conflict(
                    $"EAN {key.Ean} belongs to '{byEan.Name}' with quality {byEan.Quality}, not {key.Quality}.");
            if (byEan.BrandKey != key.BrandKey)
                return MatchResult.Conflict($"EAN {key.Ean} belongs to brand '{byEan.Brand}'.");
            if (key.MpnKey is not null && byEan.MpnKey is not null && byEan.MpnKey != key.MpnKey)
                return MatchResult.Conflict($"EAN {key.Ean} belongs to '{byEan.Name}' with a different manufacturer part number.");
            return MatchResult.Existing(byEan.Id);
        }

        if (key.IdentityKey is { } identity && _byIdentity.TryGetValue(identity, out var byMpn))
        {
            if (key.Ean is not null && byMpn.Ean is not null && byMpn.Ean != key.Ean)
                return MatchResult.Conflict(
                    $"Part number matches '{byMpn.Name}', but its EAN is {byMpn.Ean}, not {key.Ean}.");
            return MatchResult.Existing(byMpn.Id);
        }

        return MatchResult.New();
    }
}
