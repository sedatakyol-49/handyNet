using HandyNet.SharedKernel;

namespace HandyNet.Modules.Catalog.Domain;

/// <summary>Stored as text. Never rename members without a data migration.</summary>
public enum ProductCategory
{
    Smartphone,
    Tablet,
    Wearable,
    Display,
    Battery,
    Housing,
    Camera,
    ChargingPort,
    SmallParts,
    Accessory,
    Tool,
    Other,
}

/// <summary>
/// Quality/origin of a part. It is part of the product identity: an original service-pack display
/// and an aftermarket copy of the same display are different products, even if a supplier lists
/// them under the same manufacturer part number.
/// </summary>
public enum PartQuality
{
    NotApplicable,
    Original,
    ServicePack,
    Refurbished,
    Aftermarket,
}

/// <summary>
/// Canonical catalog entry, shared by all tenants. Supplier-specific data (price, stock, SKU,
/// condition) lives in Marketplace offers, never here.
/// </summary>
public sealed class Product : Entity, IAuditable
{
    private Product() { }

    public Guid Id { get; private set; }
    public string Brand { get; private set; } = null!;
    public string BrandKey { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public ProductCategory Category { get; private set; }
    public PartQuality Quality { get; private set; }
    public string? Mpn { get; private set; }
    public string? MpnKey { get; private set; }
    public string? Ean { get; private set; }
    public string? ModelName { get; private set; }
    public List<string> CompatibleModels { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedByOrganizationId { get; private set; }
    public uint Version { get; private set; }

    public static Result<Product> Create(NewProduct input, Guid? createdByOrganizationId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(input.Brand) || string.IsNullOrWhiteSpace(input.Name))
            return Error.Validation("product.incomplete", "Brand and name are required.");
        if (string.IsNullOrWhiteSpace(input.Ean) && string.IsNullOrWhiteSpace(input.Mpn))
            return Error.Validation("product.identifier_required", "An EAN/GTIN or a manufacturer part number is required.");

        string? ean = null;
        if (!string.IsNullOrWhiteSpace(input.Ean))
        {
            if (!Gtin.TryNormalize(input.Ean, out var normalized))
                return Error.Validation("product.invalid_ean", $"'{input.Ean}' is not a valid EAN/GTIN.");
            ean = normalized;
        }

        return new Product
        {
            Id = Guid.CreateVersion7(),
            Brand = input.Brand.Trim(),
            BrandKey = Text.NormalizeKey(input.Brand),
            Name = input.Name.Trim(),
            Category = input.Category,
            Quality = input.Quality,
            Mpn = string.IsNullOrWhiteSpace(input.Mpn) ? null : input.Mpn.Trim(),
            MpnKey = string.IsNullOrWhiteSpace(input.Mpn) ? null : Text.NormalizeKey(input.Mpn),
            Ean = ean,
            ModelName = string.IsNullOrWhiteSpace(input.ModelName) ? null : input.ModelName.Trim(),
            CompatibleModels = input.CompatibleModels?.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList() ?? [],
            CreatedAt = now,
            CreatedByOrganizationId = createdByOrganizationId,
        };
    }
}

public sealed record NewProduct(
    string Brand,
    string Name,
    ProductCategory Category,
    PartQuality Quality,
    string? Mpn,
    string? Ean,
    string? ModelName = null,
    IReadOnlyList<string>? CompatibleModels = null);

public static class Gtin
{
    /// <summary>Validates GTIN-8/12/13/14 including the check digit; returns the digits padded to 13 (EAN-13) or 14.</summary>
    public static bool TryNormalize(string value, out string normalized)
    {
        normalized = string.Empty;
        var digits = value.Trim().Replace(" ", string.Empty);
        if (digits.Length is not (8 or 12 or 13 or 14) || !digits.All(char.IsAsciiDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            var digit = digits[digits.Length - 2 - i] - '0';
            sum += i % 2 == 0 ? digit * 3 : digit;
        }

        var check = (10 - sum % 10) % 10;
        if (check != digits[^1] - '0')
            return false;

        normalized = digits.Length <= 13 ? digits.PadLeft(13, '0') : digits;
        return true;
    }
}

public static class CategoryParser
{
    private static readonly Dictionary<string, ProductCategory> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["handy"] = ProductCategory.Smartphone,
        ["mobiltelefon"] = ProductCategory.Smartphone,
        ["phone"] = ProductCategory.Smartphone,
        ["akku"] = ProductCategory.Battery,
        ["batterie"] = ProductCategory.Battery,
        ["lcd"] = ProductCategory.Display,
        ["bildschirm"] = ProductCategory.Display,
        ["gehäuse"] = ProductCategory.Housing,
        ["backcover"] = ProductCategory.Housing,
        ["kamera"] = ProductCategory.Camera,
        ["ladebuchse"] = ProductCategory.ChargingPort,
        ["kleinteile"] = ProductCategory.SmallParts,
        ["zubehör"] = ProductCategory.Accessory,
        ["werkzeug"] = ProductCategory.Tool,
        ["sonstiges"] = ProductCategory.Other,
    };

    public static bool TryParse(string? value, out ProductCategory category)
    {
        category = ProductCategory.Other;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var key = value.Trim().Replace(" ", string.Empty);
        return (Enum.TryParse(key, true, out category) && Enum.IsDefined(category)) || Aliases.TryGetValue(key, out category);
    }
}

public static class QualityParser
{
    private static readonly Dictionary<string, PartQuality> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = PartQuality.NotApplicable,
        ["n/a"] = PartQuality.NotApplicable,
        ["orig"] = PartQuality.Original,
        ["oem"] = PartQuality.Original,
        ["original"] = PartQuality.Original,
        ["sp"] = PartQuality.ServicePack,
        ["servicepack"] = PartQuality.ServicePack,
        ["refurb"] = PartQuality.Refurbished,
        ["refurbished"] = PartQuality.Refurbished,
        ["nachbau"] = PartQuality.Aftermarket,
        ["aftermarket"] = PartQuality.Aftermarket,
        ["copy"] = PartQuality.Aftermarket,
    };

    public static bool TryParse(string? value, out PartQuality quality)
    {
        var key = (value ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
        return Aliases.TryGetValue(key, out quality) || (Enum.TryParse(key, true, out quality) && Enum.IsDefined(quality));
    }
}
