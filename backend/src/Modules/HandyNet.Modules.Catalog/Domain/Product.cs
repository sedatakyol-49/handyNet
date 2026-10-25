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

    /// <summary>
    /// Normalized keys (<see cref="Text.NormalizeKey"/>) of the model name and every compatible model, for the
    /// marketplace filter "passend für …" (exact, case- and punctuation-insensitive; GIN-indexed).
    /// </summary>
    public List<string> ModelKeys { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? CreatedByOrganizationId { get; private set; }
    public uint Version { get; private set; }

    public static Result<Product> Create(NewProduct input, Guid? createdByOrganizationId, DateTimeOffset now)
    {
        var data = Normalize(input);
        if (!data.IsSuccess)
            return data.Error!;
        var product = new Product { Id = Guid.CreateVersion7(), CreatedAt = now, CreatedByOrganizationId = createdByOrganizationId };
        product.Apply(data.Value);
        return product;
    }

    /// <summary>
    /// Changes the product data with the same rules as <see cref="Create"/>. Only the creating organization may do this
    /// (<see cref="CanEdit"/>; RLS on catalog.products enforces it as well). Switching between a category stocked by IMEI
    /// and one that is not is refused (<c>product.category_stock_mode</c>): existing stock — serialized units vs.
    /// quantities, possibly of other organizations — would no longer fit the product. Collisions of the new EAN or
    /// brand + part number + quality with another product are checked by the caller. Returns whether anything changed.
    /// </summary>
    public Result<bool> Update(NewProduct input)
    {
        var data = Normalize(input);
        if (!data.IsSuccess)
            return data.Error!;
        var next = data.Value;
        if (next.Category != Category
            && (SerializedStock.RequiresImei(next.Category) != SerializedStock.RequiresImei(Category)
                || SerializedStock.AllowsImei(next.Category) != SerializedStock.AllowsImei(Category)))
            return Error.Validation("product.category_stock_mode",
                $"The category cannot change from {Category} to {next.Category}: the two are stocked differently (IMEI vs. quantity).");
        var changed = next.Brand != Brand || next.Name != Name || next.Category != Category || next.Quality != Quality
                      || next.Mpn != Mpn || next.Ean != Ean || next.ModelName != ModelName
                      || !next.CompatibleModels.SequenceEqual(CompatibleModels, StringComparer.Ordinal);
        if (changed)
            Apply(next);
        return changed;
    }

    /// <summary>Whether <paramref name="organizationId"/> may change this product's data and images (ADR-0011, ADR-0015).</summary>
    public static bool CanEdit(Guid? createdByOrganizationId, Guid? organizationId) =>
        organizationId is not null && createdByOrganizationId == organizationId;

    private void Apply(ProductData d)
    {
        Brand = d.Brand;
        BrandKey = Text.NormalizeKey(d.Brand);
        Name = d.Name;
        Category = d.Category;
        Quality = d.Quality;
        Mpn = d.Mpn;
        MpnKey = d.Mpn is null ? null : Text.NormalizeKey(d.Mpn);
        Ean = d.Ean;
        ModelName = d.ModelName;
        CompatibleModels = d.CompatibleModels;
        ModelKeys = ModelKeysOf(d.ModelName, d.CompatibleModels);
    }

    private sealed record ProductData(string Brand, string Name, ProductCategory Category, PartQuality Quality, string? Mpn, string? Ean,
        string? ModelName, List<string> CompatibleModels);

    private static Result<ProductData> Normalize(NewProduct input)
    {
        if (string.IsNullOrWhiteSpace(input.Brand) || string.IsNullOrWhiteSpace(input.Name))
            return Error.Validation("product.incomplete", "Brand and name are required.");
        if (input.Brand.Trim().Length > 100 || input.Name.Trim().Length > 300 || input.Mpn?.Trim().Length > 100)
            return Error.Validation("product.field_too_long", "Brand (100), name (300) or part number (100) is too long.");
        if (string.IsNullOrWhiteSpace(input.Ean) && string.IsNullOrWhiteSpace(input.Mpn))
            return Error.Validation("product.identifier_required", "An EAN/GTIN or a manufacturer part number is required.");

        string? ean = null;
        if (!string.IsNullOrWhiteSpace(input.Ean))
        {
            if (!Gtin.TryNormalize(input.Ean, out var normalized))
                return Error.Validation("product.invalid_ean", $"'{input.Ean}' is not a valid EAN/GTIN.");
            ean = normalized;
        }

        var modelName = string.IsNullOrWhiteSpace(input.ModelName) ? null : input.ModelName.Trim();
        var compatible = input.CompatibleModels?.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (modelName?.Length > 200 || compatible.Any(m => m.Length > 200))
            return Error.Validation("product.model_too_long", "Model names are limited to 200 characters.");
        if (compatible.Count > MaxCompatibleModels)
            return Error.Validation("product.too_many_models", $"At most {MaxCompatibleModels} compatible models.");

        return new ProductData(input.Brand.Trim(), input.Name.Trim(), input.Category, input.Quality,
            string.IsNullOrWhiteSpace(input.Mpn) ? null : input.Mpn.Trim(), ean, modelName, compatible);
    }

    public const int MaxCompatibleModels = 100;

    public static List<string> ModelKeysOf(string? modelName, IEnumerable<string> compatibleModels) =>
        (modelName is null ? compatibleModels : compatibleModels.Prepend(modelName))
        .Select(Text.NormalizeKey).Where(k => k.Length > 0).Distinct().ToList();
}

/// <summary>
/// Which categories are stocked as serialized devices (one IMEI per unit). Smartphones always are; tablets and wearables
/// may be (cellular models have an IMEI, Wi-Fi-only models do not). Parts and accessories never are.
/// </summary>
public static class SerializedStock
{
    public static bool RequiresImei(ProductCategory category) => category == ProductCategory.Smartphone;

    public static bool AllowsImei(ProductCategory category) =>
        category is ProductCategory.Smartphone or ProductCategory.Tablet or ProductCategory.Wearable;
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
