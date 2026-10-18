using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

public sealed record DsfinvkFile(byte[] Content, string FileName);

/// <summary>
/// Basic DSFinV-K 2.x export of one register for closed cash sessions (Kassenabschlüsse) whose business day lies in a date
/// range: a ZIP with the main CSV files and an <c>index.xml</c> (GDPdU description). Follows the DSFinV-K structure as
/// closely as the data allows; <b>not certified and not reviewed by a tax adviser</b> — see docs/adr/0012-fiscal-layer.md
/// for the implemented files and fields. Simulated (test) TSE data is marked as such.
/// </summary>
internal sealed class DsfinvkExportService(
    HandyNetDbContext db,
    ITenantContext tenant,
    IOrganizationDirectory organizations,
    IUserAccounts users)
{
    public const int MaxDays = 366;
    public const string TaxonomyVersion = "2.3";
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public async Task<Result<DsfinvkFile>> ExportAsync(Guid registerId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var organizationId = tenant.RequiredOrganizationId;
        if (from is null || to is null)
            return Error.Validation("pos.export_range", "'from' and 'to' (business days, inclusive) are required.");
        if (from > to)
            return Error.Validation("pos.export_range", "'from' must not be after 'to'.");
        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxDays)
            return Error.Validation("pos.export_range", $"At most {MaxDays} days per export.");
        var register = await db.Set<Register>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == registerId && r.OrganizationId == organizationId, ct);
        if (register is null)
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var seller = await organizations.GetLegalDetailsAsync(organizationId, ct)
                     ?? throw new InvalidOperationException("Organization of the current tenant not found.");

        // Z_NR: the closing number of the register (1, 2, … in closing order over the register's whole history).
        var closed = await db.Set<CashSession>().AsNoTracking()
            .Where(s => s.RegisterId == registerId && s.Status == CashSessionStatus.Closed)
            .OrderBy(s => s.ClosedAt).ThenBy(s => s.Id).ToListAsync(ct);
        var zNumbers = closed.Select((s, i) => (s.Id, Number: i + 1L)).ToDictionary(x => x.Id, x => x.Number);
        var sessions = closed.Where(s => BusinessDay(s.ClosedAt!.Value) >= from && BusinessDay(s.ClosedAt.Value) <= to).ToList();
        var sessionIds = sessions.Select(s => s.Id).ToList();

        var receipts = await db.Set<Receipt>().AsNoTracking().Include(r => r.Lines).Include(r => r.Payments).AsSplitQuery()
            .Where(r => sessionIds.Contains(r.SessionId)).OrderBy(r => r.Number).ToListAsync(ct);
        var movements = await db.Set<CashMovement>().AsNoTracking().Where(m => sessionIds.Contains(m.SessionId)).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var referencedIds = receipts.Where(r => r.ReferencedReceiptId != null).Select(r => r.ReferencedReceiptId!.Value).Distinct().ToList();
        var referenced = await db.Set<Receipt>().AsNoTracking().Where(r => referencedIds.Contains(r.Id))
            .Select(r => new { r.Id, r.RegisterId, r.SessionId, r.CreatedAt }).ToDictionaryAsync(r => r.Id, ct);
        var cashiers = await users.GetAsync(receipts.Select(r => r.CreatedBy).Distinct().ToList(), ct);

        var tables = DsfinvkTables.Create();
        var tses = new Dictionary<string, int>();
        var kasse = register.Id.ToString("N");
        foreach (var session in sessions)
        {
            var z = zNumbers[session.Id];
            var created = Local(session.ClosedAt!.Value);
            object[] Key() => [kasse, created, z];
            var sessionReceipts = receipts.Where(r => r.SessionId == session.Id).ToList();
            var cash = sessionReceipts.SelectMany(r => r.Payments).Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountMinor);
            var card = sessionReceipts.SelectMany(r => r.Payments).Where(p => p.Method == PaymentMethod.Card).Sum(p => p.AmountMinor);

            tables["cashpointclosing"].Add([.. Key(), BusinessDay(session.ClosedAt.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TaxonomyVersion,
                sessionReceipts.FirstOrDefault()?.Id.ToString() ?? "", sessionReceipts.LastOrDefault()?.Id.ToString() ?? "", seller.LegalName, seller.Street,
                seller.PostalCode, seller.City, Country(seller.CountryCode), "", seller.VatId ?? "", Money(cash + card), Money(cash)]);
            tables["location"].Add([.. Key(), seller.Name, seller.Street, seller.PostalCode, seller.City, Country(seller.CountryCode), seller.VatId ?? ""]);
            tables["cashregister"].Add([.. Key(), "HandyNet", "HandyNet POS", kasse, "HandyNet", typeof(DsfinvkExportService).Assembly.GetName().Version?.ToString() ?? "",
                "EUR", 0]);
            tables["vat"].Add([.. Key(), 1, "19.00", "Regelsteuersatz"]);

            foreach (var group in sessionReceipts.SelectMany(r => r.Lines).GroupBy(l => (Key: VatKey(l), l.TaxTreatment)))
                tables["businesscases"].Add([.. Key(), "Umsatz", group.Key.TaxTreatment == TaxTreatment.Margin25a ? "Differenzbesteuerung § 25a UStG" : "",
                    "", group.Key.Key, Money(group.Sum(l => l.LineGrossMinor)), Money(group.Sum(l => l.NetMinor)), Money(group.Sum(l => l.VatMinor))]);
            foreach (var group in movements.Where(m => m.SessionId == session.Id).GroupBy(BusinessCase))
            {
                var amount = group.Sum(m => m.Kind == CashMovementKind.Deposit ? m.AmountMinor : -m.AmountMinor);
                tables["businesscases"].Add([.. Key(), group.Key, "", "", 5, Money(amount), Money(amount), Money(0)]);
            }

            if (cash != 0)
                tables["payment"].Add([.. Key(), "Bar", "Bar", Money(cash)]);
            if (card != 0)
                tables["payment"].Add([.. Key(), "Unbar", "Karte", Money(card)]);
            tables["cash_per_currency"].Add([.. Key(), "EUR", Money(cash)]);

            foreach (var receipt in sessionReceipts)
            {
                var bon = receipt.Id.ToString();
                object[] BonKey() => [.. Key(), bon];
                var customer = receipt.Document?.Customer;
                var notice = receipt.FiscalStatus == FiscalStatus.Failed
                    ? KassenSichV.OutageNotice
                    : receipt.FiscalIsTest == true ? KassenSichV.TestTseNotice : receipt.FiscalNotice ?? "";
                tables["transactions"].Add([.. BonKey(), receipt.Number, KassenSichV.ReceiptType, Name(receipt.Kind), kasse,
                    receipt.Kind == ReceiptKind.Storno ? 1 : 0, Local(receipt.FiscalStartedAt ?? receipt.CreatedAt), Local(receipt.FiscalFinishedAt ?? receipt.CreatedAt),
                    receipt.CreatedBy.ToString(), cashiers.GetValueOrDefault(receipt.CreatedBy)?.DisplayName ?? "", Money(receipt.TotalGrossMinor),
                    customer?.Name ?? "", customer?.Id.ToString() ?? "", customer is null ? "" : customer.Company is null ? "Privatkunde" : "Geschäftskunde",
                    customer?.Street ?? "", customer?.PostalCode ?? "", customer?.City ?? "", customer?.CountryCode is { } cc ? Country(cc) : "",
                    customer?.VatId ?? "", notice]);

                foreach (var group in receipt.Lines.GroupBy(VatKey))
                    tables["transactions_vat"].Add([.. BonKey(), group.Key, Money(group.Sum(l => l.LineGrossMinor)), Money(group.Sum(l => l.NetMinor)),
                        Money(group.Sum(l => l.VatMinor))]);

                if (receipt.FiscalStatus is FiscalStatus.Signed or FiscalStatus.Failed && receipt.FiscalMode is not null and not nameof(FiscalMode.Disabled))
                {
                    var tseId = receipt.FiscalTseSerialNumber is { } serial ? TseId(tses, serial, receipt, tables, Key()) : 0;
                    tables["transactions_tse"].Add([.. BonKey(), tseId == 0 ? "" : (object)tseId, receipt.FiscalTransactionNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
                        receipt.FiscalStartedAt is { } s ? Local(s) : "", receipt.FiscalFinishedAt is { } e ? Local(e) : "", receipt.FiscalProcessType ?? "",
                        receipt.FiscalSignatureCounter?.ToString(CultureInfo.InvariantCulture) ?? "", receipt.FiscalSignature ?? "",
                        receipt.FiscalStatus == FiscalStatus.Failed ? $"TSE ausgefallen: {receipt.FiscalError}" : "", receipt.FiscalProcessData ?? ""]);
                }

                foreach (var payment in receipt.Payments)
                    tables["datapayment"].Add([.. BonKey(), payment.Method == PaymentMethod.Cash ? "Bar" : "Unbar", payment.Method == PaymentMethod.Cash ? "Bar" : "Karte",
                        "EUR", Money(payment.AmountMinor), Money(payment.AmountMinor)]);

                if (receipt.ReferencedReceiptId is { } refId && referenced.TryGetValue(refId, out var original))
                    tables["references"].Add([.. BonKey(), "", "Transaktion", Name(ReceiptKind.Sale), Local(original.CreatedAt),
                        original.RegisterId.ToString("N"), original.RegisterId == registerId && zNumbers.TryGetValue(original.SessionId, out var refZ) ? (object)refZ : "",
                        original.Id.ToString()]);

                foreach (var line in receipt.Lines.OrderBy(l => l.LineNumber))
                {
                    tables["lines"].Add([.. BonKey(), line.LineNumber, "", Description(line), kasse, "Umsatz",
                        line.TaxTreatment == TaxTreatment.Margin25a ? "Differenzbesteuerung § 25a UStG" : "", 0, receipt.Kind == ReceiptKind.Storno ? 1 : 0,
                        "", line.ProductId.ToString(), "", "", "", Quantity(line.Quantity), "1", "Stück", Money(line.UnitPriceMinor)]);
                    tables["lines_vat"].Add([.. BonKey(), line.LineNumber, VatKey(line), Money(line.LineGrossMinor), Money(line.NetMinor), Money(line.VatMinor)]);
                }
            }
        }

        var range = (from.Value, to.Value);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var table in tables.Values)
                Add(zip, table.FileName, table.ToCsv());
            Add(zip, "index.xml", DsfinvkTables.IndexXml(tables.Values, seller.LegalName, range));
            Add(zip, "LIESMICH.txt", Readme(register.Name, range, tses.Count > 0 && receipts.Any(r => r.FiscalIsTest == true)));
        }
        return new DsfinvkFile(buffer.ToArray(), $"dsfinvk_{Safe(register.Name)}_{from:yyyyMMdd}-{to:yyyyMMdd}.zip");
    }

    private static int TseId(Dictionary<string, int> tses, string serial, Receipt receipt, IReadOnlyDictionary<string, DsfinvkTable> tables, object[] key)
    {
        if (tses.TryGetValue(serial, out var id))
            return id;
        id = tses.Count + 1;
        tses[serial] = id;
        var test = receipt.FiscalIsTest == true;
        tables["tse"].Add([.. key, id, serial, receipt.FiscalSignatureAlgorithm ?? "", receipt.FiscalTimeFormat ?? "", "UTF-8", receipt.FiscalPublicKey ?? "",
            test ? "TEST-TSE (simuliert oder Sandbox) – kein Zertifikat, nicht für den Echtbetrieb" : "", ""]);
        return id;
    }

    private static string Readme(string registerName, (DateOnly From, DateOnly To) range, bool test) =>
        $"""
        DSFinV-K-Export (Basisumfang) – HandyNet POS
        Kasse: {registerName}
        Zeitraum (Geschäftstage der Kassenabschlüsse): {range.From:dd.MM.yyyy} – {range.To:dd.MM.yyyy}

        Dieser Export folgt der Struktur der DSFinV-K 2.x so weit wie möglich. Er ist NICHT zertifiziert und
        NICHT von einer Steuerberatung geprüft. Nicht enthalten: u. a. Stamm_Abschluss-Felder STNR, Bonpos_Preisfindung,
        Bonpos_Zusatzinfo, Stamm_Agenturen, Stamm_Terminals, Bon_Referenzen auf Positionsebene.
        {(test ? "\nACHTUNG: Enthält Signaturen einer TEST-TSE (simuliert oder fiskaly-Sandbox) – nicht für den Echtbetrieb.\n" : "")}
        """;

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        stream.Write(bytes);
    }

    private static string BusinessCase(CashMovement m) => (m.Kind, m.ReasonCode) switch
    {
        (CashMovementKind.Deposit, CashMovementReason.OwnerDeposit) => "Privateinlage",
        (CashMovementKind.Withdrawal, CashMovementReason.OwnerWithdrawal) => "Privatentnahme",
        (_, CashMovementReason.ChangeFund) or (_, CashMovementReason.BankDeposit) => "Geldtransit",
        (CashMovementKind.Deposit, _) => "Einzahlung",
        _ => "Auszahlung",
    };

    /// <summary>DSFinV-K USt-Schlüssel: 1 = 19 % (also §25a margin lines, taxed at the standard rate on the margin).</summary>
    private static int VatKey(ReceiptLine line) => line.VatRateBasisPoints switch
    {
        700 => 2,
        1070 => 3,
        550 => 4,
        0 when line.TaxTreatment != TaxTreatment.Margin25a => 5,
        _ => 1,
    };

    private static string Name(ReceiptKind kind) => kind switch
    {
        ReceiptKind.Storno => "Storno",
        ReceiptKind.Return => "Rückgabe",
        _ => "Verkauf",
    };

    private static string Description(ReceiptLine line) => line.Imei is { } imei ? $"{line.Description} (IMEI {imei})" : line.Description;

    internal static DateOnly BusinessDay(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, Zone).DateTime);

    internal static string Local(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, Zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string Money(long minor) => KassenSichV.Amount(minor);

    private static string Quantity(int quantity) => quantity.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Country(string code) => code.ToUpperInvariant() switch
    {
        "DE" => "DEU",
        "AT" => "AUT",
        "CH" => "CHE",
        "NL" => "NLD",
        "FR" => "FRA",
        "PL" => "POL",
        _ => code.ToUpperInvariant(),
    };

    private static string Safe(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}

internal enum DsfinvkType
{
    Text,
    Numeric,
    Date,
}

internal sealed record DsfinvkColumn(string Name, DsfinvkType Type, int MaxLength = 0, int Accuracy = 0);

/// <summary>One DSFinV-K table: CSV with a header row, ',' as separator, '"' as text delimiter, '.' as decimal symbol, CRLF.</summary>
internal sealed class DsfinvkTable(string fileName, string name, string description, IReadOnlyList<DsfinvkColumn> columns)
{
    private readonly List<object[]> _rows = [];

    public string FileName { get; } = fileName;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public IReadOnlyList<DsfinvkColumn> Columns { get; } = columns;
    public int RowCount => _rows.Count;

    public void Add(object[] row)
    {
        if (row.Length != Columns.Count)
            throw new InvalidOperationException($"{FileName}: {row.Length} values for {Columns.Count} columns.");
        _rows.Add(row);
    }

    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns.Select(c => c.Name))).Append("\r\n");
        foreach (var row in _rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                var value = Convert.ToString(row[i], CultureInfo.InvariantCulture) ?? "";
                if (Columns[i].Type == DsfinvkType.Text)
                {
                    var column = Columns[i];
                    if (column.MaxLength > 0 && value.Length > column.MaxLength)
                        value = value[..column.MaxLength];
                    sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                }
                else
                    sb.Append(value);
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

internal static class DsfinvkTables
{
    private static DsfinvkColumn T(string name, int max) => new(name, DsfinvkType.Text, max);
    private static DsfinvkColumn N(string name, int accuracy = 0) => new(name, DsfinvkType.Numeric, 0, accuracy);
    private static DsfinvkColumn D(string name) => new(name, DsfinvkType.Date);

    private static readonly DsfinvkColumn[] ZKey = [T("Z_KASSE_ID", 50), D("Z_ERSTELLUNG"), N("Z_NR")];
    private static readonly DsfinvkColumn[] BonKey = [.. ZKey, T("BON_ID", 40)];

    /// <summary>The implemented DSFinV-K files, in index.xml order.</summary>
    public static Dictionary<string, DsfinvkTable> Create()
    {
        var tables = new DsfinvkTable[]
        {
            // Einzelaufzeichnungsmodul
            new("transactions.csv", "Bonkopf", "Bonkopf", [.. BonKey, N("BON_NR"), T("BON_TYP", 30), T("BON_NAME", 60), T("TERMINAL_ID", 50), N("BON_STORNO"),
                D("BON_START"), D("BON_ENDE"), T("BEDIENER_ID", 50), T("BEDIENER_NAME", 50), N("UMS_BRUTTO", 2), T("KUNDE_NAME", 50), T("KUNDE_ID", 50),
                T("KUNDE_TYP", 50), T("KUNDE_STRASSE", 60), T("KUNDE_PLZ", 10), T("KUNDE_ORT", 62), T("KUNDE_LAND", 3), T("KUNDE_USTID", 15), T("BON_NOTIZ", 255)]),
            new("transactions_vat.csv", "Bonkopf_USt", "Bonkopf - Umsatzsteuer", [.. BonKey, N("UST_SCHLUESSEL"), N("BON_BRUTTO", 2), N("BON_NETTO", 2), N("BON_UST", 2)]),
            new("transactions_tse.csv", "TSE_Transaktionen", "TSE-Transaktionen", [.. BonKey, N("TSE_ID"), N("TSE_TANR"), D("TSE_TA_START"), D("TSE_TA_ENDE"),
                T("TSE_TA_VORGANGSART", 30), N("TSE_TA_SIGZ"), T("TSE_TA_SIG", 512), T("TSE_TA_FEHLER", 200), T("TSE_TA_VORGANGSDATEN", 1000)]),
            new("datapayment.csv", "Bonkopf_Zahlarten", "Bonkopf - Zahlarten", [.. BonKey, T("ZAHLART_TYP", 25), T("ZAHLART_NAME", 60), T("ZAHLWAEH_CODE", 3),
                N("ZAHLWAEH_BETRAG", 2), N("BASISWAEH_BETRAG", 2)]),
            new("references.csv", "Bon_Referenzen", "Referenzen", [.. BonKey, N("POS_ZEILE"), T("REF_TYP", 20), T("REF_NAME", 40), D("REF_DATUM"),
                T("REF_Z_KASSE_ID", 50), N("REF_Z_NR"), T("REF_BON_ID", 40)]),
            new("lines.csv", "Bonpos", "Bonpositionen", [.. BonKey, N("POS_ZEILE"), T("GUTSCHEIN_NR", 50), T("ARTIKELTEXT", 255), T("POS_TERMINAL_ID", 50),
                T("GV_TYP", 30), T("GV_NAME", 40), N("INHAUS"), N("P_STORNO"), N("AGENTUR_ID"), T("ART_NR", 50), T("GTIN", 50), T("WARENGR_ID", 40),
                T("WARENGR", 50), N("MENGE", 3), N("FAKTOR", 3), T("EINHEIT", 50), N("STK_BR", 2)]),
            new("lines_vat.csv", "Bonpos_USt", "Bonpositionen - Umsatzsteuer", [.. BonKey, N("POS_ZEILE"), N("UST_SCHLUESSEL"), N("POS_BRUTTO", 2),
                N("POS_NETTO", 2), N("POS_UST", 2)]),
            // Stammdatenmodul
            new("cashpointclosing.csv", "Stamm_Abschluss", "Stammdaten Kassenabschluss", [.. ZKey, T("Z_BUCHUNGSTAG", 10), T("TAXONOMIE_VERSION", 10),
                T("Z_START_ID", 40), T("Z_ENDE_ID", 40), T("NAME", 60), T("STRASSE", 60), T("PLZ", 10), T("ORT", 62), T("LAND", 3), T("STNR", 20),
                T("USTID", 15), N("Z_SE_ZAHLUNGEN", 2), N("Z_SE_BARZAHLUNGEN", 2)]),
            new("location.csv", "Stamm_Orte", "Stammdaten Orte", [.. ZKey, T("LOC_NAME", 60), T("LOC_STRASSE", 60), T("LOC_PLZ", 10), T("LOC_ORT", 62),
                T("LOC_LAND", 3), T("LOC_USTID", 15)]),
            new("cashregister.csv", "Stamm_Kassen", "Stammdaten Kassen", [.. ZKey, T("KASSE_BRAND", 50), T("KASSE_MODELL", 50), T("KASSE_SERIENNR", 70),
                T("KASSE_SW_BRAND", 50), T("KASSE_SW_VERSION", 50), T("KASSE_BASISWAEH_CODE", 3), N("KEINE_UST_ZUORDNUNG")]),
            new("vat.csv", "Stamm_USt", "Stammdaten Umsatzsteuer", [.. ZKey, N("UST_SCHLUESSEL"), N("UST_SATZ", 2), T("UST_BESCHR", 55)]),
            new("tse.csv", "Stamm_TSE", "Stammdaten TSE", [.. ZKey, N("TSE_ID"), T("TSE_SERIAL", 68), T("TSE_SIG_ALGO", 21), T("TSE_ZEITFORMAT", 31),
                T("TSE_PD_ENCODING", 5), T("TSE_PUBLIC_KEY", 512), T("TSE_ZERTIFIKAT_I", 1000), T("TSE_ZERTIFIKAT_II", 1000)]),
            // Kassenabschlussmodul
            new("businesscases.csv", "Z_GV_Typ", "Kassenabschluss - Geschäftsvorfälle", [.. ZKey, T("GV_TYP", 30), T("GV_NAME", 40), N("AGENTUR_ID"),
                N("UST_SCHLUESSEL"), N("Z_UMS_BRUTTO", 2), N("Z_UMS_NETTO", 2), N("Z_UST", 2)]),
            new("payment.csv", "Z_Zahlart", "Kassenabschluss - Zahlarten", [.. ZKey, T("ZAHLART_TYP", 25), T("ZAHLART_NAME", 60), N("Z_ZAHLART_BETRAG", 2)]),
            new("cash_per_currency.csv", "Z_Waehrungen", "Kassenabschluss - Bargeld je Währung", [.. ZKey, T("ZAHLART_WAEH", 3), N("ZAHLART_BETRAG_WAEH", 2)]),
        };
        return tables.ToDictionary(t => Path.GetFileNameWithoutExtension(t.FileName), StringComparer.Ordinal);
    }

    /// <summary>GDPdU description (gdpdu-01-09-2004.dtd) of the exported files, as DSFinV-K expects next to the CSVs.</summary>
    public static string IndexXml(IEnumerable<DsfinvkTable> tables, string supplier, (DateOnly From, DateOnly To) range)
    {
        static string E(string value) => SecurityElement.Escape(value);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
        sb.Append("<!DOCTYPE DataSet SYSTEM \"gdpdu-01-09-2004.dtd\">\r\n");
        sb.Append("<DataSet>\r\n  <Version>1.0</Version>\r\n");
        sb.Append($"  <DataSupplier>\r\n    <Name>{E(supplier)}</Name>\r\n    <Location>HandyNet POS</Location>\r\n");
        sb.Append($"    <Comment>DSFinV-K {DsfinvkExportService.TaxonomyVersion} (Basisumfang) – nicht zertifiziert, nicht steuerlich geprüft</Comment>\r\n  </DataSupplier>\r\n");
        sb.Append("  <Media>\r\n    <Name>DSFinV-K</Name>\r\n");
        foreach (var table in tables)
        {
            sb.Append($"    <Table>\r\n      <URL>{table.FileName}</URL>\r\n      <Name>{E(table.Name)}</Name>\r\n      <Description>{E(table.Description)}</Description>\r\n");
            sb.Append($"      <Validity>\r\n        <Range>\r\n          <From>{range.From:dd.MM.yyyy}</From>\r\n          <To>{range.To:dd.MM.yyyy}</To>\r\n        </Range>\r\n");
            sb.Append("        <Format>DD.MM.YYYY</Format>\r\n      </Validity>\r\n");
            sb.Append("      <UTF8/>\r\n      <DecimalSymbol>.</DecimalSymbol>\r\n      <DigitGroupingSymbol>,</DigitGroupingSymbol>\r\n");
            sb.Append("      <SkipNumBytes>0</SkipNumBytes>\r\n");
            sb.Append("      <VariableLength>\r\n        <ColumnDelimiter>,</ColumnDelimiter>\r\n        <RecordDelimiter>&#13;&#10;</RecordDelimiter>\r\n");
            sb.Append("        <TextEncapsulator>\"</TextEncapsulator>\r\n");
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                var element = i < 3 ? "VariablePrimaryKey" : "VariableColumn";
                sb.Append($"        <{element}>\r\n          <Name>{column.Name}</Name>\r\n");
                sb.Append(column.Type switch
                {
                    DsfinvkType.Numeric => column.Accuracy > 0 ? $"          <Numeric>\r\n            <Accuracy>{column.Accuracy}</Accuracy>\r\n          </Numeric>\r\n" : "          <Numeric/>\r\n",
                    DsfinvkType.Date => "          <Date>\r\n            <Format>YYYY-MM-DD\"T\"hh:mm:ss</Format>\r\n          </Date>\r\n",
                    _ => $"          <AlphaNumeric/>\r\n" + (column.MaxLength > 0 ? $"          <MaxLength>{column.MaxLength}</MaxLength>\r\n" : ""),
                });
                sb.Append($"        </{element}>\r\n");
            }
            sb.Append("      </VariableLength>\r\n    </Table>\r\n");
        }
        sb.Append("  </Media>\r\n</DataSet>\r\n");
        return sb.ToString();
    }
}
