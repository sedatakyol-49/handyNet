using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

/// <summary>Gift cards (ADR-0020): codes, balance rules, settlement as a non-cash means of payment, fiscal buckets.</summary>
public class PosGiftCardTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static (GiftCard Card, string Code) Card(long amount = 50_00, DateOnly? expiresOn = null)
    {
        var code = GiftCardCode.New();
        return (GiftCard.Issue(Guid.NewGuid(), Org, code, amount, Guid.NewGuid(), expiresOn ?? new DateOnly(2029, 12, 31), null, User, Now).Value.Card, code);
    }

    private static SaleLineInput GiftLine(long amount = 25_00) =>
        new(null, null, null, "Geschenkgutschein ••••-AB12", 1, amount, 0, TaxTreatment.NotTaxable, null, null, Guid.NewGuid());

    [Fact]
    public void Codes_are_16_unambiguous_characters_and_never_repeat()
    {
        var codes = Enumerable.Range(0, 2000).Select(_ => GiftCardCode.New()).ToList();
        Assert.All(codes, c => Assert.True(c.Length == 16 && c.All(GiftCardCode.Alphabet.Contains), c));
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(32, GiftCardCode.Alphabet.Distinct().Count());
        Assert.DoesNotContain(GiftCardCode.Alphabet, c => c is 'I' or 'O' or '0' or '1');
        // Every character of the alphabet is produced (the generator is not biased to a subset).
        Assert.Equal(32, string.Concat(codes).Distinct().Count());
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKLM-NPQR", "ABCDEFGHJKLMNPQR")]
    [InlineData("abcd efgh jklm npqr", "ABCDEFGHJKLMNPQR")]
    [InlineData(" abcdefghjklmnpqr ", "ABCDEFGHJKLMNPQR")]
    [InlineData("ABCD-EFGH-JKLM-NPQ", null)]      // too short
    [InlineData("ABCD-EFGH-JKLM-NPQRS", null)]    // too long
    [InlineData("ABCD-EFGH-JKLM-NPQ0", null)]     // 0 is not in the alphabet
    [InlineData("ABCD-EFGH-JKLM-NPQI", null)]     // I is not in the alphabet
    [InlineData("ABCD_EFGH_JKLM_NPQR", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Codes_are_normalized_or_rejected(string? input, string? normalized) => Assert.Equal(normalized, GiftCardCode.Normalize(input));

    [Fact]
    public void Only_hash_and_last_four_characters_of_the_code_are_kept()
    {
        var (card, code) = Card();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(code))), card.CodeHash);
        Assert.Equal(code[^4..], card.CodeLast4);
        Assert.Equal("ABCD-EFGH-JKLM-NPQR", GiftCardCode.Format("ABCDEFGHJKLMNPQR"));
        Assert.Equal("••••-NPQR", GiftCardCode.Masked("NPQR"));
        Assert.DoesNotContain(typeof(GiftCard).GetProperties(), p => p.PropertyType == typeof(string) && (string?)p.GetValue(card) == code);
        Assert.Equal(new DateOnly(2029, 12, 31), GiftCard.ExpiryFor(new DateOnly(2026, 1, 1), 3));
        Assert.Equal(new DateOnly(2029, 12, 31), GiftCard.ExpiryFor(new DateOnly(2026, 12, 31), 3));
    }

    [Fact]
    public void Redeeming_never_takes_more_than_the_balance()
    {
        var (card, _) = Card(50_00);
        var receipt = Guid.NewGuid();
        var first = card.Redeem(30_00, receipt, Today, User, Now).Value;
        Assert.Equal((GiftCardTransactionType.Redeem, -30_00L, 20_00L, receipt), (first.Type, first.AmountMinor, first.BalanceAfterMinor, first.ReceiptId!.Value));
        var tooMuch = card.Redeem(20_01, receipt, Today, User, Now);
        Assert.Equal(("pos.gift_card_insufficient", (ErrorKind?)ErrorKind.Conflict, 20_00L), (tooMuch.Error?.Code, tooMuch.Error?.Kind, tooMuch.Error?.Details?["balanceMinor"]));
        Assert.Equal(20_00, card.BalanceMinor);
        Assert.True(card.Redeem(20_00, receipt, Today, User, Now).IsSuccess);
        Assert.Equal(0, card.BalanceMinor);
        Assert.Equal("pos.gift_card_insufficient", card.Redeem(1, receipt, Today, User, Now).Error?.Code);
        Assert.Equal("pos.payment_amount", card.Redeem(0, receipt, Today, User, Now).Error?.Code);
        Assert.Equal("pos.payment_amount", card.Redeem(-5, receipt, Today, User, Now).Error?.Code);
        Assert.Equal(0, card.BalanceMinor);
    }

    [Fact]
    public void Expired_and_voided_cards_cannot_pay()
    {
        var (card, _) = Card(50_00, expiresOn: Today);
        Assert.True(card.Redeem(10_00, Guid.NewGuid(), Today, User, Now).IsSuccess); // the last day counts
        var expired = card.Redeem(10_00, Guid.NewGuid(), Today.AddDays(1), User, Now);
        Assert.Equal(("pos.gift_card_not_usable", (ErrorKind?)ErrorKind.Conflict), (expired.Error?.Code, expired.Error?.Kind));
        Assert.Equal(40_00, card.BalanceMinor);

        Assert.Equal("pos.reason", card.Void(" ", null, User, Now).Error?.Code);
        var voided = card.Void("verloren", null, User, Now).Value;
        Assert.Equal((GiftCardTransactionType.Void, -40_00L, 0L, "verloren"), (voided.Type, voided.AmountMinor, voided.BalanceAfterMinor, voided.Reason));
        Assert.Equal((GiftCardStatus.Voided, 0L), (card.Status, card.BalanceMinor));
        Assert.Equal("pos.gift_card_not_usable", card.Redeem(1, Guid.NewGuid(), Today, User, Now).Error?.Code);
        Assert.Equal("pos.gift_card_not_usable", card.Void("nochmal", null, User, Now).Error?.Code);
        Assert.Equal("pos.gift_card_not_usable", card.Restore(10_00, Guid.NewGuid(), User, Now).Error?.Code);
        Assert.Equal("pos.gift_card_not_usable", card.Reissue(GiftCardCode.New(), "neu", User, Now).Error?.Code);
    }

    [Fact]
    public void Storno_restores_what_was_paid_and_voids_only_an_untouched_card()
    {
        var (paid, _) = Card(50_00);
        paid.Redeem(30_00, Guid.NewGuid(), Today, User, Now);
        var restored = paid.Restore(30_00, Guid.NewGuid(), User, Now).Value;
        Assert.Equal((GiftCardTransactionType.Restore, 30_00L, 50_00L), (restored.Type, restored.AmountMinor, restored.BalanceAfterMinor));
        // Never above what the card was issued with.
        Assert.Equal("pos.gift_card_restore", paid.Restore(1, Guid.NewGuid(), User, Now).Error?.Code);
        Assert.Equal(50_00, paid.BalanceMinor);

        var (untouched, _) = Card(25_00);
        var storno = Guid.NewGuid();
        var voided = untouched.VoidForStorno(storno, User, Now).Value;
        Assert.Equal((GiftCardTransactionType.Void, -25_00L, storno), (voided.Type, voided.AmountMinor, voided.ReceiptId!.Value));
        Assert.Equal(GiftCardStatus.Voided, untouched.Status);

        var (used, _) = Card(25_00);
        used.Redeem(1, Guid.NewGuid(), Today, User, Now);
        var refused = used.VoidForStorno(storno, User, Now);
        Assert.Equal(("pos.gift_card_already_redeemed", (ErrorKind?)ErrorKind.Conflict), (refused.Error?.Code, refused.Error?.Kind));
        Assert.Equal((GiftCardStatus.Active, 24_99L), (used.Status, used.BalanceMinor));
    }

    [Fact]
    public void Reissue_changes_the_code_but_not_the_balance()
    {
        var (card, oldCode) = Card(50_00);
        card.Redeem(10_00, Guid.NewGuid(), Today, User, Now);
        var oldHash = card.CodeHash;
        var newCode = GiftCardCode.New();
        var reissued = card.Reissue(newCode, "Code fotografiert", User, Now).Value;
        Assert.Equal((GiftCardTransactionType.Reissue, 0L, 40_00L), (reissued.Type, reissued.AmountMinor, reissued.BalanceAfterMinor));
        Assert.Equal((GiftCardCode.HashOf(newCode), newCode[^4..], 40_00L), (card.CodeHash, card.CodeLast4, card.BalanceMinor));
        Assert.NotEqual(oldHash, card.CodeHash);
        Assert.NotEqual(GiftCardCode.HashOf(oldCode), card.CodeHash);
        Assert.Equal("pos.reason", card.Reissue(GiftCardCode.New(), null, User, Now).Error?.Code);
    }

    [Fact]
    public void Gift_card_line_rules()
    {
        var rules = new GiftCardRules(5_00, 500_00, 3);
        Assert.True(rules.CheckLine(1, 25_00, false, 1, 0, 0).IsSuccess);
        Assert.True(rules.CheckLine(1, 25_00, false, 1, 0, 25_00).IsSuccess);
        Assert.True(rules.CheckLine(1, 5_00, false, 1, 0, 0).IsSuccess);
        Assert.True(rules.CheckLine(1, 500_00, false, 1, 0, 0).IsSuccess);
        Assert.Equal("pos.gift_card_amount", rules.CheckLine(1, 4_99, false, 1, 0, 0).Error?.Code);
        Assert.Equal("pos.gift_card_amount", rules.CheckLine(1, 500_01, false, 1, 0, 0).Error?.Code);
        Assert.Equal("pos.gift_card_line", rules.CheckLine(2, 25_00, true, 1, 0, 0).Error?.Code);   // with a product
        Assert.Equal("pos.gift_card_line", rules.CheckLine(2, 25_00, false, 2, 0, 0).Error?.Code);  // quantity
        Assert.Equal("pos.gift_card_line", rules.CheckLine(2, 25_00, false, 1, 1, 0).Error?.Code);  // discount
        Assert.Equal("pos.gift_card_line", rules.CheckLine(2, 25_00, false, 1, 0, 20_00).Error?.Code); // another price
    }

    [Fact]
    public void Gift_card_line_is_not_taxable_and_counts_as_sold_in_the_z_report()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(), GiftLine(25_00)], [new PaymentInput(PaymentMethod.Cash, 44_00)]).Value;
        var line = sale.Lines[1];
        Assert.Equal((null as Guid?, TaxTreatment.NotTaxable, 0, 0L, 25_00L, 25_00L), (line.ProductId, line.TaxTreatment, line.VatRateBasisPoints, line.VatMinor, line.NetMinor, line.LineGrossMinor));
        Assert.NotNull(line.GiftCardId);
        Assert.Equal(44_00, sale.TotalGrossMinor);
        Assert.Equal(sale.Lines[0].VatMinor, sale.TotalVatMinor); // VAT only on the cable

        var summary = SessionCalculator.Summarize(0, [sale], []);
        Assert.Equal((1, 25_00L), (summary.GiftCardsSoldCount, summary.GiftCardsSoldMinor));
        // Kassenbeleg-V1: the gift card is in the 0 % bucket.
        Assert.Equal("Beleg^19.00_0.00_0.00_0.00_25.00^44.00:Bar", KassenSichV.ReceiptProcessData(summary.ByTaxTreatment, summary.ByPaymentMethod));
        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        var document = ReceiptDocument.For(sale, new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null), "Kasse", null, fiscal);
        Assert.Equal("N", document.Lines[1].TaxLabel);
        Assert.Contains(ReceiptDocument.GiftCardNote, document.Notes);
        Assert.Single(document.Vat); // the 19 % block only

        // The storno mirrors the line: nothing sold in the end.
        var storno = Receipt.CreateStorno(PosReceiptTests.Header(), sale, hasReturns: false, "Fehlbon").Value;
        Assert.Equal(line.GiftCardId, storno.Lines[1].GiftCardId);
        var net = SessionCalculator.Summarize(0, [sale, storno], []);
        Assert.Equal((0, 0L), (net.GiftCardsSoldCount, net.GiftCardsSoldMinor));

        // Invalid shapes are refused by the domain as well.
        Assert.Equal("pos.gift_card_line", Receipt.CreateSale(PosReceiptTests.Header(), [GiftLine() with { GiftCardId = null }], [new PaymentInput(PaymentMethod.Cash, 25_00)]).Error?.Code);
        Assert.Equal("pos.gift_card_line", Receipt.CreateSale(PosReceiptTests.Header(), [GiftLine() with { Treatment = TaxTreatment.Standard19 }], [new PaymentInput(PaymentMethod.Cash, 25_00)]).Error?.Code);
        Assert.Equal("pos.gift_card_line", Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable() with { GiftCardId = Guid.NewGuid() }], [new PaymentInput(PaymentMethod.Cash, 19_00)]).Error?.Code);
    }

    [Fact]
    public void Gift_card_payment_is_non_cash_exact_and_cannot_exceed_the_total_or_pay_for_gift_cards()
    {
        // 19.00 total: 10.00 gift card + 5.00 card + cash.
        var split = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()],
            [new PaymentInput(PaymentMethod.GiftCard, 6_00), new PaymentInput(PaymentMethod.GiftCard, 4_00), new PaymentInput(PaymentMethod.Card, 5_00), new PaymentInput(PaymentMethod.Cash, 10_00)]).Value;
        Assert.Equal([(PaymentMethod.Cash, 4_00L), (PaymentMethod.Card, 5_00L), (PaymentMethod.GiftCard, 10_00L)], split.Payments.Select(p => (p.Method, p.AmountMinor)));
        Assert.Equal(6_00, split.ChangeMinor);
        var summary = SessionCalculator.Summarize(0, [split], []);
        // One "Unbar" entry: card and gift card together.
        Assert.Equal("Beleg^19.00_0.00_0.00_0.00_0.00^4.00:Bar_15.00:Unbar", KassenSichV.ReceiptProcessData(summary.ByTaxTreatment, summary.ByPaymentMethod));
        Assert.Equal(4_00, summary.CashPaymentsMinor);

        var over = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()], [new PaymentInput(PaymentMethod.GiftCard, 19_01)]);
        Assert.Equal("pos.gift_card_exceeds_total", over.Error?.Code);
        var together = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()],
            [new PaymentInput(PaymentMethod.GiftCard, 10_00), new PaymentInput(PaymentMethod.Card, 9_01)]);
        Assert.Equal("pos.card_exceeds_total", together.Error?.Code);

        // A gift card cannot buy a gift card: only the product part (19.00) of 44.00 may be paid with one.
        var lines = new[] { PosReceiptTests.Cable(), GiftLine(25_00) };
        Assert.Equal("pos.gift_card_exceeds_total",
            Receipt.CreateSale(PosReceiptTests.Header(), lines, [new PaymentInput(PaymentMethod.GiftCard, 19_01), new PaymentInput(PaymentMethod.Cash, 25_00)]).Error?.Code);
        Assert.True(Receipt.CreateSale(PosReceiptTests.Header(), lines, [new PaymentInput(PaymentMethod.GiftCard, 19_00), new PaymentInput(PaymentMethod.Cash, 25_00)]).IsSuccess);
        Assert.Equal("pos.gift_card_exceeds_total",
            Receipt.CreateSale(PosReceiptTests.Header(), [GiftLine(25_00)], [new PaymentInput(PaymentMethod.GiftCard, 25_00)]).Error?.Code);

        // Cash rounding applies to the cash part only: 19.99 − 10.00 gift card = 9.99 → 10.00 in cash.
        var rounded = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(1, 19_99, 0)],
            [new PaymentInput(PaymentMethod.GiftCard, 10_00), new PaymentInput(PaymentMethod.Cash, 10_00)], cashRounding: true).Value;
        Assert.Equal((1L, 0L), (rounded.RoundingMinor, rounded.ChangeMinor));
        // Paid by gift card alone: exact, no rounding.
        var exact = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(1, 19_99, 0)], [new PaymentInput(PaymentMethod.GiftCard, 19_99)], cashRounding: true).Value;
        Assert.Equal(0, exact.RoundingMinor);
    }

    [Fact]
    public void Existing_settlement_and_process_data_are_unchanged_without_gift_cards()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()],
            [new PaymentInput(PaymentMethod.Card, 9_00), new PaymentInput(PaymentMethod.Cash, 20_00)]).Value;
        Assert.Equal([(PaymentMethod.Cash, 10_00L), (PaymentMethod.Card, 9_00L)], sale.Payments.Select(p => (p.Method, p.AmountMinor)));
        var summary = SessionCalculator.Summarize(0, [sale], []);
        Assert.Equal("Beleg^19.00_0.00_0.00_0.00_0.00^10.00:Bar_9.00:Unbar", KassenSichV.ReceiptProcessData(summary.ByTaxTreatment, summary.ByPaymentMethod));
        Assert.Equal(0, summary.ByPaymentMethod.Single(p => p.Method == PaymentMethod.GiftCard).AmountMinor);
        Assert.Equal((0, 0L), (summary.GiftCardsSoldCount, summary.GiftCardsSoldMinor));
    }

    [Fact]
    public void Gift_card_lines_cannot_be_returned_and_a_return_may_refund_onto_a_gift_card()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable(), GiftLine(25_00)], [new PaymentInput(PaymentMethod.Cash, 44_00)]).Value;
        var none = new Dictionary<int, int>();
        var giftLine = Receipt.CreateReturn(PosReceiptTests.Header(), sale, new Dictionary<int, int> { [2] = 1 }, none, PaymentMethod.Cash, "zurück");
        Assert.Equal(("pos.gift_card_line_not_returnable", (ErrorKind?)ErrorKind.Conflict), (giftLine.Error?.Code, giftLine.Error?.Kind));

        var refund = Receipt.CreateReturn(PosReceiptTests.Header(), sale, new Dictionary<int, int> { [1] = 1 }, none, PaymentMethod.GiftCard, "defekt", cashRounding: true).Value;
        Assert.Equal((PaymentMethod.GiftCard, -9_50L), (refund.Payments.Single().Method, refund.Payments.Single().AmountMinor));
        Assert.Equal(0, refund.RoundingMinor); // not cash: exact
    }

    [Fact]
    public void Fingerprints_change_only_for_gift_card_lines_and_codes()
    {
        var register = Guid.NewGuid();
        var product = Guid.NewGuid();
        var request = new CreateSaleRequest(register, [new SaleLineRequest(product, null, 2, 10_00, 1_00)], [new SalePaymentRequest(PaymentMethod.Cash, 20_00)]);
        var legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"sale#{register:N}#{product:N}::2:1000:100#Cash:2000")));
        Assert.Equal(legacy, SaleService.HashRequest(request));

        var withGiftLine = SaleService.HashRequest(request with { Lines = [.. request.Lines, new SaleLineRequest(null, null, 1, 0, null, null, 25_00)] });
        Assert.NotEqual(legacy, withGiftLine);
        Assert.NotEqual(withGiftLine, SaleService.HashRequest(request with { Lines = [.. request.Lines, new SaleLineRequest(null, null, 1, 0, null, null, 30_00)] }));

        var paid = request with { Payments = [new SalePaymentRequest(PaymentMethod.GiftCard, 19_00, null, "ABCD-EFGH-JKLM-NPQR")] };
        // The same card typed differently is the same request; another card is not.
        Assert.Equal(SaleService.HashRequest(paid), SaleService.HashRequest(request with { Payments = [new SalePaymentRequest(PaymentMethod.GiftCard, 19_00, null, "abcdefghjklmnpqr")] }));
        Assert.NotEqual(SaleService.HashRequest(paid), SaleService.HashRequest(request with { Payments = [new SalePaymentRequest(PaymentMethod.GiftCard, 19_00, null, "ABCD-EFGH-JKLM-NPQS")] }));
    }
}
