using System.Globalization;
using ClosedXML.Excel;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Infrastructure;

public sealed record PriceRow(int RowNumber, string Brand, string Model, string? Storage, long PriceMinor);

public sealed record PriceSheet(string SheetName, IReadOnlyList<PriceRow> Rows, IReadOnlyList<SkippedRow> Skipped);

/// <summary>
/// Reads the monthly Ankaufspreisliste. The sheet (default "Mobilfunk") is selected by name; the header row is
/// detected within the first rows by German/English column names. Rows without a usable price are reported as
/// skipped — they get no new price version, so their old price expires instead of silently remaining valid.
/// </summary>
public static class PriceListReader
{
    public const string DefaultSheet = "Mobilfunk";
    public const int MaxRows = 5000;
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly string[] BrandHeaders = ["hersteller", "marke", "brand", "manufacturer"];
    private static readonly string[] ModelHeaders = ["modell", "model", "gerät", "geraet", "bezeichnung", "gerätename", "device"];
    private static readonly string[] StorageHeaders = ["speicher", "kapazität", "kapazitaet", "storage", "gb", "speichergröße", "speichergroesse"];
    private static readonly string[] PriceHeaders = ["preis", "ankaufspreis", "ankauf", "price", "gut", "preisgut", "ankaufpreis"];

    public static Result<PriceSheet> Read(byte[] content, string? sheetName)
    {
        try
        {
            using var stream = new MemoryStream(content);
            using var workbook = new XLWorkbook(stream);
            var wanted = string.IsNullOrWhiteSpace(sheetName) ? DefaultSheet : sheetName.Trim();
            var sheet = workbook.Worksheets.FirstOrDefault(s => string.Equals(s.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            if (sheet is null)
                return Error.Validation("ankauf.sheet_missing",
                    $"Sheet '{wanted}' not found. Available: {string.Join(", ", workbook.Worksheets.Select(s => s.Name))}.");

            if (sheet.RangeUsed() is not { } range)
                return Error.Validation("ankauf.sheet_empty", $"Sheet '{sheet.Name}' is empty.");

            var header = FindHeader(range);
            if (header is null)
                return Error.Validation("ankauf.header_missing", "No header row with a model and a price column was found in the first 15 rows.");

            var (headerRow, brandCol, modelCol, storageCol, priceCol) = header.Value;
            var rows = new List<PriceRow>();
            var skipped = new List<SkippedRow>();
            var lastRow = range.LastRow().RowNumber();
            for (var r = headerRow + 1; r <= lastRow; r++)
            {
                var model = Cell(sheet, r, modelCol);
                var brand = brandCol is { } bc ? Cell(sheet, r, bc) : string.Empty;
                var storage = storageCol is { } sc ? Cell(sheet, r, sc) : null;
                var priceCell = sheet.Cell(r, priceCol);
                var priceText = Cell(sheet, r, priceCol);

                if (model.Length == 0 && priceText.Length == 0)
                    continue; // blank or section separator row
                if (model.Length == 0)
                {
                    skipped.Add(new SkippedRow(r, priceText, "No model name."));
                    continue;
                }
                if (brand.Length == 0)
                    brand = model.Split(' ', 2)[0]; // lists without a brand column start the model with it ("Apple iPhone 13")

                if (!TryPrice(priceCell, priceText, out var minor))
                {
                    skipped.Add(new SkippedRow(r, $"{brand} {model} {storage}".Trim(),
                        priceText.Length == 0 ? "No price." : $"Price '{priceText}' is not a valid amount."));
                    continue;
                }

                if (rows.Count >= MaxRows)
                    return Error.Validation("ankauf.too_many_rows", $"At most {MaxRows} models per list.");
                rows.Add(new PriceRow(r, brand, model, string.IsNullOrWhiteSpace(storage) ? null : storage, minor));
            }

            return new PriceSheet(sheet.Name, rows, skipped);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or FormatException)
        {
            return Error.Validation("ankauf.unreadable", $"The workbook could not be read: {ex.Message}");
        }
    }

    private static (int Row, int? Brand, int Model, int? Storage, int Price)? FindHeader(IXLRange range)
    {
        var firstRow = range.FirstRow().RowNumber();
        var lastCol = range.LastColumn().ColumnNumber();
        for (var r = firstRow; r < firstRow + 15; r++)
        {
            int? brand = null, storage = null, model = null, price = null;
            for (var c = range.FirstColumn().ColumnNumber(); c <= lastCol; c++)
            {
                var key = Key(range.Worksheet.Cell(r, c).GetString());
                if (key.Length == 0)
                    continue;
                if (brand is null && BrandHeaders.Contains(key)) brand = c;
                else if (model is null && ModelHeaders.Contains(key)) model = c;
                else if (storage is null && StorageHeaders.Contains(key)) storage = c;
                else if (price is null && (PriceHeaders.Contains(key) || key.StartsWith("ankauf", StringComparison.Ordinal) || key.StartsWith("preis", StringComparison.Ordinal)))
                    price = c;
            }

            if (model is { } m && price is { } p)
                return (r, brand, m, storage, p);
        }

        return null;
    }

    private static string Key(string header) => new(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Cell(IXLWorksheet sheet, int row, int col)
    {
        var cell = sheet.Cell(row, col);
        return cell.DataType == XLDataType.Number
            ? cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture)
            : cell.GetString().Trim();
    }

    private static bool TryPrice(IXLCell cell, string text, out long minor)
    {
        minor = 0;
        decimal amount;
        if (cell.DataType == XLDataType.Number)
            amount = (decimal)cell.GetDouble();
        else if (!NumberParser.TryParseAmount(text, out amount))
            return false;

        if (amount <= 0 || !Money.TryFromMajor(decimal.Round(amount, 2), Money.Eur, out var money))
            return false;
        // Spreadsheet doubles like 129.99999999 are rounded to cents; anything finer than a cent is not a price.
        if (Math.Abs(amount - decimal.Round(amount, 2)) > 0.000001m)
            return false;
        minor = money.MinorUnits;
        return true;
    }
}
