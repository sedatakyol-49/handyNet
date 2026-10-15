using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using HandyNet.Modules.Imports.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Imports.Application;

public sealed record TabularData(IReadOnlyList<string> Headers, IReadOnlyList<(int RowNumber, Dictionary<string, string> Values)> Rows);

/// <summary>Reads supplier files into header → value rows. Each format is an adapter; add JSON/XML feeds the same way.</summary>
public static class TabularReaders
{
    public const int MaxRows = 20_000;
    public const long MaxFileBytes = 10 * 1024 * 1024;

    static TabularReaders() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Result<ImportFormat> DetectFormat(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" or ".txt" => ImportFormat.Csv,
            ".xlsx" => ImportFormat.Xlsx,
            _ => Error.Validation("import.unsupported_format", "Only .csv and .xlsx files are supported."),
        };

    public static Result<TabularData> Read(ImportFormat format, byte[] content)
    {
        try
        {
            return format == ImportFormat.Csv ? ReadCsv(content) : ReadXlsx(content);
        }
        catch (Exception ex) when (ex is CsvHelperException or InvalidDataException or FormatException or IOException
                                       or ArgumentException or InvalidOperationException)
        {
            return Error.Validation("import.unreadable", $"The file could not be read: {ex.Message}");
        }
    }

    private static Result<TabularData> ReadCsv(byte[] content)
    {
        var text = Decode(content);
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ";", ",", "\t" }.MaxBy(d => firstLine.Split(d).Length)!;

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
            DetectColumnCountChanges = false,
        });

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } headerRecord)
            return Error.Validation("import.empty", "The file has no header row.");

        var headers = CheckHeaders(headerRecord);
        if (!headers.IsSuccess)
            return headers.Error!;

        var rows = new List<(int, Dictionary<string, string>)>();
        while (csv.Read())
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < headers.Value.Count; i++)
                values[headers.Value[i]] = csv.TryGetField<string>(i, out var v) ? v ?? string.Empty : string.Empty;

            if (values.Values.All(string.IsNullOrWhiteSpace))
                continue;
            if (rows.Count >= MaxRows)
                return Error.Validation("import.too_many_rows", $"Files are limited to {MaxRows} rows.");
            rows.Add((csv.Parser.Row, values));
        }

        return new TabularData(headers.Value, rows);
    }

    private static Result<TabularData> ReadXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet?.RangeUsed() is not { } range)
            return Error.Validation("import.empty", "The workbook has no data.");

        var headerRow = range.FirstRow();
        var headers = CheckHeaders(headerRow.Cells().Select(c => CellText(c)).ToArray());
        if (!headers.IsSuccess)
            return headers.Error!;

        var firstColumn = range.FirstColumn().ColumnNumber();
        var rows = new List<(int, Dictionary<string, string>)>();
        foreach (var row in range.RowsUsed().Skip(1))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < headers.Value.Count; i++)
                values[headers.Value[i]] = CellText(row.WorksheetRow().Cell(firstColumn + i));

            if (values.Values.All(string.IsNullOrWhiteSpace))
                continue;
            if (rows.Count >= MaxRows)
                return Error.Validation("import.too_many_rows", $"Files are limited to {MaxRows} rows.");
            rows.Add((row.RowNumber(), values));
        }

        return new TabularData(headers.Value, rows);
    }

    private static string CellText(IXLCell cell) => cell.DataType switch
    {
        // Numbers are rendered invariantly so EANs stay whole and decimals use '.'.
        XLDataType.Number => cell.GetDouble().ToString("0.##########", CultureInfo.InvariantCulture),
        XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
        _ => cell.GetString().Trim(),
    };

    private static Result<IReadOnlyList<string>> CheckHeaders(string[] raw)
    {
        var headers = raw.Select(h => (h ?? string.Empty).Trim()).ToList();
        while (headers.Count > 0 && headers[^1].Length == 0)
            headers.RemoveAt(headers.Count - 1);

        if (headers.Count == 0)
            return Error.Validation("import.empty", "The file has no header row.");
        if (headers.Any(h => h.Length == 0))
            return Error.Validation("import.blank_header", "Every column needs a header.");
        var duplicate = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return Error.Validation("import.duplicate_header", $"Column header '{duplicate.Key}' appears more than once.");
        return headers;
    }

    private static string Decode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            // German Excel "CSV (Trennzeichen-getrennt)" exports Windows-1252.
            return Encoding.GetEncoding(1252).GetString(content);
        }
    }
}

/// <summary>Suggests a column mapping from common German and English header names.</summary>
public static class MappingGuesser
{
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        [ImportField.Sku] = ["sku", "artikelnummer", "artnr", "artikelnr", "itemnumber", "itemno", "lieferantenartikelnummer"],
        [ImportField.Ean] = ["ean", "gtin", "ean13", "barcode"],
        [ImportField.Mpn] = ["mpn", "herstellernummer", "herstellerartikelnummer", "partnumber", "teilenummer", "manufacturerpartnumber"],
        [ImportField.Brand] = ["brand", "marke", "hersteller", "manufacturer"],
        [ImportField.Name] = ["name", "bezeichnung", "artikelbezeichnung", "artikel", "produkt", "product", "title", "titel"],
        [ImportField.Category] = ["category", "kategorie", "warengruppe"],
        [ImportField.Quality] = ["quality", "qualität", "qualitaet"],
        [ImportField.Price] = ["price", "preis", "netto", "nettopreis", "ek", "ekpreis", "haendlerpreis", "händlerpreis"],
        [ImportField.Quantity] = ["quantity", "qty", "bestand", "menge", "lagerbestand", "stock"],
        [ImportField.DeliveryDays] = ["deliverydays", "lieferzeit", "lieferzeittage"],
        [ImportField.MinOrderQuantity] = ["minorderquantity", "moq", "mindestmenge", "mindestbestellmenge"],
        [ImportField.Condition] = ["condition", "zustand"],
        [ImportField.ModelName] = ["model", "modell", "modelname"],
    };

    public static Dictionary<string, string> Guess(IReadOnlyList<string> headers)
    {
        var mapping = new Dictionary<string, string>();
        foreach (var header in headers)
        {
            var key = new string(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            var field = Aliases.FirstOrDefault(a => a.Value.Contains(key)).Key;
            if (field is not null)
                mapping.TryAdd(field, header);
        }

        return mapping;
    }
}
