using System.Globalization;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Marketplace.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Imports.Domain;

public sealed record ParsedRow(
    int RowNumber,
    Dictionary<string, string> Raw,
    string? Sku,
    string? Ean,
    string? Mpn,
    string? Brand,
    string? Name,
    ProductCategory? Category,
    PartQuality Quality,
    OfferCondition Condition,
    string? ModelName,
    Money? Price,
    int? Quantity,
    int DeliveryDays,
    int MinOrderQuantity,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public string SkuKey => Text.NormalizeKey(Sku ?? string.Empty);

    public ProductKey ProductKey => new(
        Text.NormalizeKey(Brand ?? string.Empty),
        string.IsNullOrWhiteSpace(Mpn) ? null : Text.NormalizeKey(Mpn),
        Ean,
        Quality);
}

public sealed record ParseDefaults(int DeliveryDays = 2, OfferCondition Condition = OfferCondition.New);

/// <summary>Turns one raw spreadsheet row into typed values according to the column mapping.</summary>
public static class RowParser
{
    public static ParsedRow Parse(int rowNumber, Dictionary<string, string> raw, IReadOnlyDictionary<string, string> mapping, ParseDefaults defaults)
    {
        var errors = new List<string>();

        string? Get(string field) =>
            mapping.TryGetValue(field, out var header) && raw.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

        var sku = Get(ImportField.Sku);
        var brand = Get(ImportField.Brand);
        var name = Get(ImportField.Name);
        var mpn = Get(ImportField.Mpn);
        if (sku is null) errors.Add("SKU is missing.");
        if (brand is null) errors.Add("Brand is missing.");
        if (name is null) errors.Add("Name is missing.");
        if (sku?.Length > 100) errors.Add("SKU is longer than 100 characters.");
        if (name?.Length > 300) errors.Add("Name is longer than 300 characters.");
        if (mpn?.Length > 100) errors.Add("Manufacturer part number is longer than 100 characters.");

        string? ean = null;
        if (Get(ImportField.Ean) is { } rawEan)
        {
            if (Gtin.TryNormalize(rawEan, out var normalized))
                ean = normalized;
            else
                errors.Add($"EAN '{rawEan}' is invalid (length or check digit).");
        }
        if (ean is null && mpn is null && !errors.Any(e => e.StartsWith("EAN", StringComparison.Ordinal)))
            errors.Add("EAN or manufacturer part number is required to identify the product.");

        ProductCategory? category = ProductCategory.Other;
        if (Get(ImportField.Category) is { } rawCategory)
        {
            if (CategoryParser.TryParse(rawCategory, out var parsedCategory))
                category = parsedCategory;
            else
            {
                category = null;
                errors.Add($"Unknown category '{rawCategory}'.");
            }
        }

        var quality = PartQuality.NotApplicable;
        if (Get(ImportField.Quality) is { } rawQuality && !QualityParser.TryParse(rawQuality, out quality))
            errors.Add($"Unknown quality '{rawQuality}'.");

        var condition = defaults.Condition;
        if (Get(ImportField.Condition) is { } rawCondition && !ConditionParser.TryParse(rawCondition, out condition))
            errors.Add($"Unknown condition '{rawCondition}'.");

        Money? price = null;
        if (Get(ImportField.Price) is not { } rawPrice)
            errors.Add("Price is missing.");
        else if (!NumberParser.TryParseAmount(rawPrice, out var amount))
            errors.Add($"Price '{rawPrice}' is not a valid amount.");
        else if (amount <= 0)
            errors.Add("Price must be greater than zero.");
        else if (!Money.TryFromMajor(amount, Money.Eur, out var money))
            errors.Add($"Price '{rawPrice}' has more than two decimal places.");
        else
            price = money;

        int? quantity = null;
        if (mapping.ContainsKey(ImportField.Quantity))
        {
            var rawQuantity = Get(ImportField.Quantity);
            if (rawQuantity is null)
                errors.Add("Quantity is missing.");
            else if (!NumberParser.TryParseInt(rawQuantity, out var q) || q < 0)
                errors.Add($"Quantity '{rawQuantity}' must be a whole number ≥ 0.");
            else
                quantity = q;
        }

        var deliveryDays = defaults.DeliveryDays;
        if (Get(ImportField.DeliveryDays) is { } rawDelivery
            && (!NumberParser.TryParseInt(rawDelivery, out deliveryDays) || deliveryDays < 0 || deliveryDays > Offer.MaxDeliveryDays))
            errors.Add($"Delivery days '{rawDelivery}' must be between 0 and {Offer.MaxDeliveryDays}.");

        var minOrder = 1;
        if (Get(ImportField.MinOrderQuantity) is { } rawMin && (!NumberParser.TryParseInt(rawMin, out minOrder) || minOrder < 1))
            errors.Add($"Minimum order quantity '{rawMin}' must be a whole number ≥ 1.");

        return new ParsedRow(rowNumber, raw, sku, ean, mpn, brand, name, category, quality, condition,
            Get(ImportField.ModelName), price, quantity, deliveryDays, minOrder, errors);
    }
}

public static class ConditionParser
{
    private static readonly Dictionary<string, OfferCondition> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neu"] = OfferCondition.New,
        ["new"] = OfferCondition.New,
        ["refurbished"] = OfferCondition.Refurbished,
        ["generalüberholt"] = OfferCondition.Refurbished,
        ["refurb"] = OfferCondition.Refurbished,
        ["gebraucht"] = OfferCondition.Used,
        ["used"] = OfferCondition.Used,
    };

    public static bool TryParse(string value, out OfferCondition condition) =>
        Aliases.TryGetValue(value.Trim(), out condition);
}
