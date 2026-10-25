using HandyNet.Modules.Inventory.Domain;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

public class PosVatTests
{
    [Theory]
    [InlineData(119_00, 19_00)]   // 100 net + 19 VAT
    [InlineData(100, 16)]         // net round(84.03) = 84
    [InlineData(50, 8)]           // net round(42.02) = 42
    [InlineData(1, 0)]            // net round(0.84) = 1
    [InlineData(6_00, 96)]        // net round(504.20) = 504
    [InlineData(-119_00, -19_00)] // refunds mirror sales
    [InlineData(0, 0)]
    public void Standard_vat_is_gross_minus_rounded_net(long gross, long vat) =>
        Assert.Equal(vat, VatCalculator.StandardVatFromGross(gross));

    [Fact]
    public void Standard_vat_rounds_half_away_from_zero()
    {
        // net = round(gross / 1.19) ⇒ |net × 1.19 − gross| ≤ 0.5 × 1.19; and refunds are exact mirrors.
        for (long gross = 1; gross <= 5_000; gross++)
        {
            var vat = VatCalculator.StandardVatFromGross(gross);
            var net = gross - vat;
            Assert.True(Math.Abs(net * 1.19m - gross) <= 0.595m, $"gross {gross}: net {net}");
            Assert.Equal(-vat, VatCalculator.StandardVatFromGross(-gross));
        }
    }

    [Theory]
    [InlineData(300_00, 220_00, 12_77)] // margin 80.00 → 80 − round(67.23) = 12.77
    [InlineData(220_01, 220_00, 0)]     // margin 1 cent → net round(0.84) = 1 → VAT 0
    [InlineData(220_00, 220_00, 0)]     // no margin
    [InlineData(150_00, 220_00, 0)]     // sold at a loss: no VAT, never negative
    [InlineData(119_00, 0, 19_00)]      // bought for nothing: VAT on the whole price
    public void Margin_scheme_taxes_only_a_positive_margin(long gross, long purchase, long vat) =>
        Assert.Equal(vat, VatCalculator.MarginVat(gross, purchase));

    [Theory]
    [InlineData(2900, 3)]
    [InlineData(1000, 3)]
    [InlineData(1, 7)]
    [InlineData(-2900, 4)]
    public void Allocation_of_partial_returns_telescopes_to_the_total(long total, int of)
    {
        long sum = 0;
        for (var k = 0; k < of; k++)
            sum += VatCalculator.Allocate(total, k + 1, of) - VatCalculator.Allocate(total, k, of);
        Assert.Equal(total, sum);
    }
}

public class PosReceiptTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static long _number;

    internal static ReceiptHeader Header(Guid? session = null) =>
        new(Org, Guid.NewGuid(), session ?? Guid.NewGuid(), Guid.NewGuid(), Interlocked.Increment(ref _number), Guid.NewGuid(), Now);

    internal static SaleLineInput Cable(int quantity = 2, long price = 10_00, long discount = 1_00) =>
        new(Product, null, null, "Ladekabel", quantity, price, discount, TaxTreatment.Standard19, null);

    internal static SaleLineInput UsedPhone(long price = 300_00, long purchase = 220_00) =>
        new(Device, Guid.NewGuid(), "490154203237518", "iPhone 13 128 GB", 1, price, 0, TaxTreatment.Margin25a, purchase);

    internal static Receipt Sale(IReadOnlyList<SaleLineInput> lines, params PaymentInput[] payments) =>
        Receipt.CreateSale(Header(), lines, payments).Value;

    [Fact]
    public void Sale_totals_vat_per_line_and_change()
    {
        var result = Receipt.CreateSale(Header(), [Cable(), UsedPhone()], [new PaymentInput(PaymentMethod.Cash, 400_00)]);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var receipt = result.Value;

        Assert.Equal(19_00, receipt.Lines[0].LineGrossMinor);  // 2 × 10.00 − 1.00
        Assert.Equal(3_03, receipt.Lines[0].VatMinor);         // 19.00 − round(15.966…) = 3.03
        Assert.Equal(12_77, receipt.Lines[1].VatMinor);        // §25a on the 80.00 margin
        Assert.Equal(220_00, receipt.Lines[1].PurchasePriceMinor);
        Assert.Equal(319_00, receipt.TotalGrossMinor);
        Assert.Equal(15_80, receipt.TotalVatMinor);
        Assert.Equal(303_20, receipt.TotalNetMinor);
        Assert.Equal(81_00, receipt.ChangeMinor);
        var cash = Assert.Single(receipt.Payments);
        Assert.Equal((PaymentMethod.Cash, 319_00L, (long?)400_00), (cash.Method, cash.AmountMinor, cash.TenderedMinor));
    }

    [Fact]
    public void Split_payment_card_plus_cash_with_change_from_cash()
    {
        var receipt = Sale([Cable(1, 50_00, 0)], new PaymentInput(PaymentMethod.Card, 30_00), new PaymentInput(PaymentMethod.Cash, 25_00));
        Assert.Equal(5_00, receipt.ChangeMinor);
        Assert.Equal(20_00, receipt.Payments.Single(p => p.Method == PaymentMethod.Cash).AmountMinor);
        Assert.Equal(30_00, receipt.Payments.Single(p => p.Method == PaymentMethod.Card).AmountMinor);
    }

    [Theory]
    [InlineData(PaymentMethod.Card, 60_00, null, "pos.card_exceeds_total")]
    [InlineData(PaymentMethod.Cash, 49_99, null, "pos.payment_insufficient")]
    [InlineData(PaymentMethod.Card, 50_00, 10_00L, "pos.cash_not_needed")]
    [InlineData(PaymentMethod.Cash, 0, null, "pos.payment_amount")]
    public void Payment_rules(PaymentMethod method, long amount, long? extraCash, string code)
    {
        var payments = new List<PaymentInput> { new(method, amount) };
        if (extraCash is { } c)
            payments.Add(new PaymentInput(PaymentMethod.Cash, c));
        Assert.Equal(code, Receipt.CreateSale(Header(), [Cable(1, 50_00, 0)], payments).Error?.Code);
    }

    [Theory]
    [InlineData(0, 10_00, 0, "pos.quantity")]
    [InlineData(1, 0, 0, "pos.unit_price")]
    [InlineData(1, 10_00, 10_01, "pos.discount")]
    [InlineData(1, 10_00, -1, "pos.discount")]
    [InlineData(1, 10_00, 10_00, "pos.total_not_positive")]
    public void Line_rules(int quantity, long price, long discount, string code) =>
        Assert.Equal(code, Receipt.CreateSale(Header(), [Cable(quantity, price, discount)], [new PaymentInput(PaymentMethod.Cash, 100_00)]).Error?.Code);

    [Fact]
    public void Margin_scheme_needs_a_single_unit_with_purchase_price()
    {
        var withoutPrice = UsedPhone() with { PurchasePriceMinor = null };
        var twoUnits = UsedPhone() with { Quantity = 2 };
        Assert.Equal("pos.margin_line", Receipt.CreateSale(Header(), [withoutPrice], [new PaymentInput(PaymentMethod.Cash, 300_00)]).Error?.Code);
        Assert.Equal("pos.margin_line", Receipt.CreateSale(Header(), [twoUnits], [new PaymentInput(PaymentMethod.Cash, 600_00)]).Error?.Code);
        var phone = UsedPhone();
        Assert.Equal("pos.duplicate_unit", Receipt.CreateSale(Header(), [phone, phone], [new PaymentInput(PaymentMethod.Cash, 600_00)]).Error?.Code);
    }

    [Fact]
    public void Storno_is_the_exact_negative_mirror()
    {
        var sale = Sale([Cable(3, 9_99, 2_00), UsedPhone()], new PaymentInput(PaymentMethod.Card, 100_00), new PaymentInput(PaymentMethod.Cash, 250_00));
        var storno = Receipt.CreateStorno(Header(), sale, hasReturns: false, "Fehlbuchung").Value;

        Assert.Equal(ReceiptKind.Storno, storno.Kind);
        Assert.Equal(sale.Id, storno.ReferencedReceiptId);
        Assert.Equal(-sale.TotalGrossMinor, storno.TotalGrossMinor);
        Assert.Equal(-sale.TotalVatMinor, storno.TotalVatMinor);
        Assert.Equal(-sale.TotalNetMinor, storno.TotalNetMinor);
        Assert.Equal(0, storno.ChangeMinor);
        foreach (var (original, mirrored) in sale.Lines.Zip(storno.Lines))
        {
            Assert.Equal(-original.Quantity, mirrored.Quantity);
            Assert.Equal(-original.LineGrossMinor, mirrored.LineGrossMinor);
            Assert.Equal(-original.VatMinor, mirrored.VatMinor);
            Assert.Equal(mirrored.UnitPriceMinor * mirrored.Quantity - mirrored.DiscountMinor, mirrored.LineGrossMinor); // DB CHECK identity
            Assert.Equal(original.LineNumber, mirrored.OriginalLineNumber);
        }
        Assert.Equal(sale.Payments.Select(p => (p.Method, -p.AmountMinor)), storno.Payments.Select(p => (p.Method, p.AmountMinor)));

        Assert.Equal("pos.has_returns", Receipt.CreateStorno(Header(), sale, hasReturns: true, "x").Error?.Code);
        Assert.Equal("pos.not_a_sale", Receipt.CreateStorno(Header(), storno, hasReturns: false, "x").Error?.Code);
        Assert.Equal("pos.reason", Receipt.CreateStorno(Header(), sale, hasReturns: false, " ").Error?.Code);
    }

    [Fact]
    public void Partial_returns_never_refund_more_than_was_paid()
    {
        var sale = Sale([Cable(3, 10_00, 1_00)], new PaymentInput(PaymentMethod.Cash, 29_00)); // 29.00 for three
        var first = Receipt.CreateReturn(Header(), sale, new Dictionary<int, int> { [1] = 1 }, new Dictionary<int, int>(), PaymentMethod.Cash, "defekt").Value;
        Assert.Equal(-9_67, first.TotalGrossMinor);
        Assert.Equal(-9_67, first.Payments.Single().AmountMinor);
        Assert.Equal(-1, first.Lines.Single().Quantity);

        var rest = Receipt.CreateReturn(Header(), sale, new Dictionary<int, int> { [1] = 2 }, new Dictionary<int, int> { [1] = 1 }, PaymentMethod.Card, "defekt").Value;
        Assert.Equal(-19_33, rest.TotalGrossMinor);
        Assert.Equal(sale.TotalGrossMinor, -(first.TotalGrossMinor + rest.TotalGrossMinor));
        Assert.Equal(sale.TotalVatMinor, -(first.TotalVatMinor + rest.TotalVatMinor));

        var tooMany = Receipt.CreateReturn(Header(), sale, new Dictionary<int, int> { [1] = 1 }, new Dictionary<int, int> { [1] = 3 }, PaymentMethod.Cash, "x");
        Assert.Equal("pos.return_exceeds_sold", tooMany.Error?.Code);
        Assert.Equal("pos.line_not_found",
            Receipt.CreateReturn(Header(), sale, new Dictionary<int, int> { [2] = 1 }, new Dictionary<int, int>(), PaymentMethod.Cash, "x").Error?.Code);
    }

    [Fact]
    public void A_sealed_receipt_cannot_be_sealed_again()
    {
        var sale = Sale([Cable()], new PaymentInput(PaymentMethod.Cash, 19_00));
        var fiscal = new FiscalData(FiscalStatus.NotConnected, ReceiptDocument.NotConnectedNotice, null, null, null, null, null, null);
        var seller = new SellerInfo("Shop", "Shop GmbH", "Hauptstr. 1", "10115", "Berlin", "DE", "DE123456789");
        Assert.True(sale.Seal(fiscal, ReceiptDocument.For(sale, seller, "Kasse 1", null, fiscal)).IsSuccess);
        Assert.Equal("pos.receipt_sealed", sale.Seal(fiscal, ReceiptDocument.For(sale, seller, "Kasse 1", null, fiscal)).Error?.Code);
    }

    [Fact]
    public void Printed_receipt_hides_margin_vat_and_states_missing_tse()
    {
        var sale = Sale([Cable(), UsedPhone()], new PaymentInput(PaymentMethod.Cash, 319_00));
        var fiscal = new FiscalData(FiscalStatus.NotConnected, ReceiptDocument.NotConnectedNotice, null, null, null, null, null, null);
        var document = ReceiptDocument.For(sale, new SellerInfo("Shop", "Shop GmbH", "Hauptstr. 1", "10115", "Berlin", "DE", null), "Kasse 1", null, fiscal);

        var vat = Assert.Single(document.Vat); // only the regularly taxed cable
        Assert.Equal((19_00L, 15_97L, 3_03L), (vat.GrossMinor, vat.NetMinor, vat.VatMinor));
        Assert.Equal("§25a", document.Lines[1].TaxLabel);
        Assert.Contains(document.Notes, n => n.Contains("§ 25a UStG"));
        Assert.Contains("TSE nicht angebunden – Testbetrieb", document.Notes);
        Assert.Null(document.Fiscal.Signature);
        Assert.Null(document.Fiscal.TransactionNumber);
    }
}

public class PosSessionTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);

    private static Register NewRegister() => Register.Create(Org, "Kasse 1", Guid.NewGuid(), Now).Value;

    private static CashSession OpenSession(long opening = 100_00) =>
        CashSession.Open(Org, NewRegister(), opening, Guid.NewGuid(), Now).Value;

    [Fact]
    public void Expected_cash_and_z_report_figures()
    {
        var session = OpenSession(100_00);
        var cashSale = PosReceiptTests.Sale([PosReceiptTests.Cable(), PosReceiptTests.UsedPhone()], new PaymentInput(PaymentMethod.Cash, 400_00));
        var cardSale = PosReceiptTests.Sale([PosReceiptTests.Cable(1, 50_00, 0)], new PaymentInput(PaymentMethod.Card, 50_00));
        var refund = Receipt.CreateReturn(PosReceiptTests.Header(), cashSale, new Dictionary<int, int> { [1] = 1 }, new Dictionary<int, int>(),
            PaymentMethod.Cash, "defekt").Value; // −9.50
        var deposit = CashMovement.Record(session, CashMovementKind.Deposit, 20_00, "Wechselgeld", 0, Guid.NewGuid(), Now).Value;
        var withdrawal = CashMovement.Record(session, CashMovementKind.Withdrawal, 30_00, "Bank", 1_000_00, Guid.NewGuid(), Now).Value;

        var summary = SessionCalculator.Summarize(100_00, [cashSale, cardSale, refund], [deposit, withdrawal]);

        // 100.00 opening + 319.00 cash sale (400 tendered − 81 change) − 9.50 cash refund + 20.00 − 30.00
        Assert.Equal(399_50, summary.ExpectedCashMinor);
        Assert.Equal(309_50, summary.CashPaymentsMinor);
        Assert.Equal(50_00, summary.ByPaymentMethod.Single(p => p.Method == PaymentMethod.Card).AmountMinor);
        Assert.Equal(359_50, summary.TotalGrossMinor);
        Assert.Equal((3, 2, 0, 1), (summary.ReceiptCount, summary.SaleCount, summary.StornoCount, summary.ReturnCount));
        var standard = summary.ByTaxTreatment.Single(t => t.Treatment == TaxTreatment.Standard19);
        Assert.Equal(19_00 + 50_00 - 9_50, standard.GrossMinor);
        Assert.Equal(standard.GrossMinor, standard.NetMinor + standard.VatMinor);
        Assert.Equal(12_77, summary.ByTaxTreatment.Single(t => t.Treatment == TaxTreatment.Margin25a).VatMinor);

        var closed = session.Close(summary, 395_00, "Kasse gezählt", Guid.NewGuid(), Now, 1);
        Assert.True(closed.IsSuccess);
        Assert.Equal(-4_50, session.DifferenceMinor); // 4.50 € missing
        Assert.Equal(CashSessionStatus.Closed, session.Status);
    }

    [Fact]
    public void Session_state_rules()
    {
        var session = OpenSession(50_00);
        Assert.Equal("pos.insufficient_cash",
            CashMovement.Record(session, CashMovementKind.Withdrawal, 50_01, "Bank", 50_00, Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.reason", CashMovement.Record(session, CashMovementKind.Deposit, 1_00, " ", 50_00, Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.amount", CashMovement.Record(session, CashMovementKind.Deposit, 0, "x", 50_00, Guid.NewGuid(), Now).Error?.Code);

        var summary = SessionCalculator.Summarize(50_00, [], []);
        Assert.Equal("pos.counted_cash", session.Close(summary, -1, null, Guid.NewGuid(), Now, 1).Error?.Code);
        Assert.True(session.Close(summary, 50_00, null, Guid.NewGuid(), Now, 1).IsSuccess);
        Assert.Equal(0, session.DifferenceMinor);
        Assert.Equal("pos.session_closed", session.Close(summary, 50_00, null, Guid.NewGuid(), Now, 1).Error?.Code);
        Assert.Equal("pos.session_closed", CashMovement.Record(session, CashMovementKind.Deposit, 1_00, "x", 0, Guid.NewGuid(), Now).Error?.Code);

        Assert.Equal("pos.opening_cash", CashSession.Open(Org, NewRegister(), -1, Guid.NewGuid(), Now).Error?.Code);
        var inactive = NewRegister();
        inactive.Update("Kasse 1", isActive: false, hasOpenSession: false);
        Assert.Equal("pos.register_inactive", CashSession.Open(Org, inactive, 0, Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.register_not_found", CashSession.Open(Guid.NewGuid(), NewRegister(), 0, Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.session_open", NewRegister().Update("Kasse 1", isActive: false, hasOpenSession: true).Error?.Code);
    }
}

public class StockUnitSaleTests
{
    private static StockUnit NewUnit() =>
        StockUnit.Receive(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "490154203237518", 200_00, TaxScheme.Margin25a, "test", DateTimeOffset.UtcNow).Value;

    [Fact]
    public void A_unit_is_sold_once_and_can_come_back()
    {
        var unit = NewUnit();
        Assert.Equal("unit.not_sold", unit.ReturnToStock(DateTimeOffset.UtcNow).Error?.Code);
        Assert.True(unit.MarkSold(DateTimeOffset.UtcNow).IsSuccess);
        Assert.Equal(UnitStatus.Sold, unit.Status);
        Assert.NotNull(unit.ClosedAt);
        Assert.Equal("unit.not_available", unit.MarkSold(DateTimeOffset.UtcNow).Error?.Code);
        Assert.True(unit.ReturnToStock(DateTimeOffset.UtcNow).IsSuccess);
        Assert.Equal(UnitStatus.InStock, unit.Status);
        Assert.Null(unit.ClosedAt);
    }

    [Fact]
    public void Selling_never_touches_reserved_stock()
    {
        var level = StockLevel.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        level.Adjust(3, MovementType.Receipt, "in", null, DateTimeOffset.UtcNow);
        typeof(StockLevel).GetProperty(nameof(StockLevel.Reserved))!.SetValue(level, 2); // as reserve_offer_stock would
        Assert.Equal("stock.below_reserved", level.Adjust(-2, MovementType.Sale, "pos", null, DateTimeOffset.UtcNow).Error?.Code);
        var sold = level.Adjust(-1, MovementType.Sale, "pos", null, DateTimeOffset.UtcNow);
        Assert.True(sold.IsSuccess);
        Assert.Equal(MovementType.Sale, sold.Value!.Type);
        Assert.Equal(0, level.Available);
    }
}
