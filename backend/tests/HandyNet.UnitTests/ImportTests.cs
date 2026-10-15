using System.Text;
using ClosedXML.Excel;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Imports.Application;
using HandyNet.Modules.Imports.Domain;
using HandyNet.Modules.Inventory.Contracts;
using HandyNet.Modules.Marketplace.Contracts;
using HandyNet.Modules.Marketplace.Domain;

namespace HandyNet.UnitTests;

public class NumberParserTests
{
    [Theory]
    [InlineData("89,90", 89.90)]
    [InlineData("89.90", 89.90)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("€ 12,5", 12.5)]
    [InlineData("12,50 EUR", 12.50)]
    [InlineData("7", 7)]
    public void Parses_german_and_international_amounts(string input, double expected)
    {
        Assert.True(NumberParser.TryParseAmount(input, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("1.234")] // thousands separator or decimal point? refuse to guess
    [InlineData("1,234")]
    [InlineData("abc")]
    [InlineData("")]
    public void Rejects_ambiguous_or_invalid_amounts(string input) => Assert.False(NumberParser.TryParseAmount(input, out _));

    [Theory]
    [InlineData("5", 5)]
    [InlineData("5.0", 5)]
    [InlineData(" 12 ", 12)]
    public void Parses_integers(string input, int expected)
    {
        Assert.True(NumberParser.TryParseInt(input, out var value));
        Assert.Equal(expected, value);
    }
}

public class RowParserTests
{
    private static readonly Dictionary<string, string> Mapping = new()
    {
        [ImportField.Sku] = "Artikelnummer",
        [ImportField.Ean] = "EAN",
        [ImportField.Mpn] = "MPN",
        [ImportField.Brand] = "Marke",
        [ImportField.Name] = "Bezeichnung",
        [ImportField.Category] = "Kategorie",
        [ImportField.Quality] = "Qualität",
        [ImportField.Price] = "Preis",
        [ImportField.Quantity] = "Bestand",
    };

    private static ParsedRow Parse(params (string Header, string Value)[] values) =>
        RowParser.Parse(2, values.ToDictionary(v => v.Header, v => v.Value), Mapping, new ParseDefaults());

    [Fact]
    public void Parses_a_complete_row()
    {
        var row = Parse(("Artikelnummer", "SKU-1"), ("EAN", "4006381333931"), ("Marke", "Apple"), ("Bezeichnung", "iPhone 13 Display"),
            ("Kategorie", "Display"), ("Qualität", "Service Pack"), ("Preis", "89,90"), ("Bestand", "5"));

        Assert.True(row.IsValid, string.Join(", ", row.Errors));
        Assert.Equal(8990, row.Price!.Value.MinorUnits);
        Assert.Equal(5, row.Quantity);
        Assert.Equal(PartQuality.ServicePack, row.Quality);
        Assert.Equal(ProductCategory.Display, row.Category);
    }

    [Fact]
    public void German_category_aliases_are_understood() =>
        Assert.Equal(ProductCategory.Battery, Parse(("Artikelnummer", "S"), ("MPN", "X1"), ("Marke", "Samsung"), ("Bezeichnung", "Akku"),
            ("Kategorie", "Akku"), ("Preis", "10"), ("Bestand", "1")).Category);

    [Fact]
    public void Reports_every_problem_in_a_row()
    {
        var row = Parse(("EAN", "123"), ("Preis", "1.234"), ("Bestand", "-1"), ("Kategorie", "Raumschiff"));
        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Contains("SKU"));
        Assert.Contains(row.Errors, e => e.Contains("Brand"));
        Assert.Contains(row.Errors, e => e.Contains("EAN"));
        Assert.Contains(row.Errors, e => e.Contains("Price"));
        Assert.Contains(row.Errors, e => e.Contains("Quantity"));
        Assert.Contains(row.Errors, e => e.Contains("category"));
    }

    [Fact]
    public void Requires_ean_or_mpn() =>
        Assert.Contains(Parse(("Artikelnummer", "S"), ("Marke", "A"), ("Bezeichnung", "B"), ("Preis", "1"), ("Bestand", "1")).Errors,
            e => e.Contains("EAN or manufacturer part number"));

    [Fact]
    public void Rejects_sub_cent_prices() =>
        Assert.Contains(Parse(("Artikelnummer", "S"), ("MPN", "X"), ("Marke", "A"), ("Bezeichnung", "B"), ("Preis", "1,999"), ("Bestand", "1")).Errors,
            e => e.Contains("Price"));
}

public class ImportPlannerTests
{
    private static readonly Guid ExistingProduct = Guid.NewGuid();
    private static readonly ProductRef Existing = new(ExistingProduct, "Apple", "Display", "APPLE", "MPN1", null, PartQuality.Original);

    private static ParsedRow Row(int number, string sku, string mpn, decimal price, int? quantity = null) =>
        new(number, [], sku, null, mpn, "Apple", "Display", ProductCategory.Display, PartQuality.Original, OfferCondition.New, null,
            HandyNet.SharedKernel.Money.TryFromMajor(price, "EUR", out var m) ? m : null, quantity, 2, 1, []);

    private static IReadOnlyList<PlannedRow> Plan(IReadOnlyList<ParsedRow> rows,
        IReadOnlyDictionary<string, ExistingOffer>? offers = null, IReadOnlyDictionary<Guid, StockSnapshot>? stock = null) =>
        ImportPlanner.Plan(rows, new ProductMatcher([Existing]), offers ?? new Dictionary<string, ExistingOffer>(),
            stock ?? new Dictionary<Guid, StockSnapshot>(), publishOffers: true);

    [Fact]
    public void New_and_existing_products_are_distinguished()
    {
        var planned = Plan([Row(2, "A", "MPN1", 10), Row(3, "B", "MPN2", 10)]);
        Assert.Equal(ProductAction.MatchExisting, planned[0].ProductAction);
        Assert.Equal(ExistingProduct, planned[0].ProductId);
        Assert.Equal(ProductAction.CreateNew, planned[1].ProductAction);
    }

    [Fact]
    public void Two_rows_for_the_same_new_product_create_it_once()
    {
        var planned = Plan([Row(2, "A", "NEW", 10), Row(3, "B", "NEW", 12)]);
        Assert.All(planned, p => Assert.Equal(ProductAction.CreateNew, p.ProductAction));
        Assert.Equal(1, ImportPlanner.Summarize(planned).NewProducts);
    }

    [Fact]
    public void Duplicate_sku_in_file_is_an_error()
    {
        var planned = Plan([Row(2, "A", "MPN1", 10), Row(3, "a", "MPN1", 11)]);
        Assert.Equal(RowStatus.Error, planned[1].Status);
        Assert.Contains("Duplicate SKU", planned[1].Messages[0]);
    }

    [Fact]
    public void Unchanged_offer_is_detected_and_large_price_change_flagged()
    {
        var offers = new Dictionary<string, ExistingOffer>
        {
            ["A"] = new(Guid.NewGuid(), "A", ExistingProduct, 1000, 1, 2, OfferCondition.New, OfferStatus.Published),
        };
        Assert.Equal(OfferAction.Unchanged, Plan([Row(2, "A", "MPN1", 10)], offers)[0].OfferAction);

        var changed = Plan([Row(2, "A", "MPN1", 20)], offers)[0];
        Assert.Equal(OfferAction.Update, changed.OfferAction);
        Assert.Equal(1000, changed.OldPriceMinor);
        Assert.Contains(changed.Messages, m => m.Contains("100%"));
    }

    [Fact]
    public void Sku_pointing_to_another_product_is_a_conflict()
    {
        var offers = new Dictionary<string, ExistingOffer>
        {
            ["A"] = new(Guid.NewGuid(), "A", Guid.NewGuid(), 1000, 1, 2, OfferCondition.New, OfferStatus.Published),
        };
        Assert.Equal(RowStatus.Conflict, Plan([Row(2, "A", "MPN1", 10)], offers)[0].Status);
    }

    [Fact]
    public void Quantity_below_reserved_stock_is_rejected()
    {
        var stock = new Dictionary<Guid, StockSnapshot> { [ExistingProduct] = new(Guid.NewGuid(), ExistingProduct, 10, 4) };
        var planned = Plan([Row(2, "A", "MPN1", 10, quantity: 3)], stock: stock);
        Assert.Equal(RowStatus.Error, planned[0].Status);
        Assert.Contains("reserved", planned[0].Messages[0]);
    }
}

public class TabularReaderTests
{
    [Fact]
    public void Reads_semicolon_csv_in_windows_1252()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Artikelnummer;Qualität;Preis\nA-1;Original;12,50\n\n");
        var data = TabularReaders.Read(ImportFormat.Csv, bytes).Value;

        Assert.Equal(["Artikelnummer", "Qualität", "Preis"], data.Headers);
        Assert.Single(data.Rows);
        Assert.Equal("12,50", data.Rows[0].Values["Preis"]);
    }

    [Fact]
    public void Reads_xlsx_with_numeric_ean_intact()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Mobilfunk");
        sheet.Cell(1, 1).Value = "EAN";
        sheet.Cell(1, 2).Value = "Preis";
        sheet.Cell(2, 1).Value = 4006381333931;
        sheet.Cell(2, 2).Value = 89.9;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var data = TabularReaders.Read(ImportFormat.Xlsx, stream.ToArray()).Value;
        Assert.Equal("4006381333931", data.Rows[0].Values["EAN"]);
        Assert.Equal("89.9", data.Rows[0].Values["Preis"]);
        Assert.Equal(2, data.Rows[0].RowNumber);
    }

    [Fact]
    public void Duplicate_headers_are_rejected() =>
        Assert.False(TabularReaders.Read(ImportFormat.Csv, Encoding.UTF8.GetBytes("A;a\n1;2\n")).IsSuccess);

    [Fact]
    public void Mapping_is_guessed_from_german_headers()
    {
        var mapping = MappingGuesser.Guess(["Artikelnummer", "EAN", "Hersteller", "Bezeichnung", "Netto-Preis", "Lagerbestand"]);
        Assert.Equal("Artikelnummer", mapping[ImportField.Sku]);
        Assert.Equal("Hersteller", mapping[ImportField.Brand]);
        Assert.Equal("Netto-Preis", mapping[ImportField.Price]);
        Assert.Equal("Lagerbestand", mapping[ImportField.Quantity]);
    }
}
