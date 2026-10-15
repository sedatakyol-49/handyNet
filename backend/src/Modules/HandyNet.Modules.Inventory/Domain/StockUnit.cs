using HandyNet.SharedKernel;

namespace HandyNet.Modules.Inventory.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum UnitStatus
{
    InStock,
    Reserved,
    Sold,
    Scrapped,
}

/// <summary>VAT treatment when the unit is sold later. Stored as text.</summary>
public enum TaxScheme
{
    /// <summary>Regular VAT on the full sales price.</summary>
    Standard,

    /// <summary>§25a UStG margin scheme (bought from a private person without VAT): VAT only on the margin.</summary>
    Margin25a,
}

/// <summary>
/// One physical, serialized device (IMEI). A database index guarantees that an IMEI is "in stock" (InStock or
/// Reserved) at most once across the whole platform — so a device cannot be stocked or sold twice, even by
/// different organizations. Each unit also counts in the product's <see cref="StockLevel"/>.
/// </summary>
public sealed class StockUnit : IAuditable
{
    private StockUnit() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Imei { get; private set; } = null!;
    public UnitStatus Status { get; private set; }
    public TaxScheme TaxScheme { get; private set; }

    /// <summary>What the organization paid for the unit — the basis for §25a margin taxation.</summary>
    public long PurchasePriceMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public string Source { get; private set; } = null!;
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<StockUnit> Receive(Guid organizationId, Guid warehouseId, Guid productId, string imei, long purchasePriceMinor,
        TaxScheme scheme, string source, DateTimeOffset now)
    {
        if (imei.Length != 15 || !imei.All(char.IsAsciiDigit))
            return Error.Validation("unit.imei", "IMEI must have 15 digits.");
        if (purchasePriceMinor < 0)
            return Error.Validation("unit.purchase_price", "Purchase price cannot be negative.");
        return new StockUnit
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            ProductId = productId,
            Imei = imei,
            Status = UnitStatus.InStock,
            TaxScheme = scheme,
            PurchasePriceMinor = purchasePriceMinor,
            Currency = Money.Eur,
            Source = source,
            ReceivedAt = now,
        };
    }
}
