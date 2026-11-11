using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.UnitTests;

/// <summary>Card terminal profiles and card transactions (ADR-0021): domain rules.</summary>
public class PosCardTerminalTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Register = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static TerminalProfileSettings Zvt(string? host = "192.168.1.60", int? port = null, string? bridge = "http://localhost:9123", string? password = null) =>
        new("Theke", TerminalConnection.ZvtBridge, host, port, bridge, password);

    private static readonly CardResult Girocard = new("60012345", "000123", "0042", "girocard", "672510******1234", "A1B2C3", "00", "Zahlung erfolgt",
        "Kundenbeleg", "Händlerbeleg");

    private static CardTransaction Pending(CardTransactionKind kind = CardTransactionKind.Payment, long amount = 25_00, TerminalProfile? profile = null) =>
        CardTransaction.Start(Org, Register, kind, amount, profile, User, Now).Value;

    [Fact]
    public void Zvt_profile_gets_defaults_and_other_connections_drop_the_network_fields()
    {
        var zvt = TerminalProfile.Create(Org, Register, Zvt(), isDefault: true, Now).Value;
        Assert.Equal((20007, "000000", "192.168.1.60", "http://localhost:9123"), (zvt.Port, zvt.ZvtPassword, zvt.Host, zvt.BridgeUrl));

        Assert.True(zvt.Update(new TerminalProfileSettings(" Manuell ", TerminalConnection.Manual, "192.168.1.60", 20007, "http://localhost:9123", "123456"), Now).IsSuccess);
        Assert.Equal(("Manuell", null, null, null, null), (zvt.Name, zvt.Host, zvt.Port, zvt.BridgeUrl, zvt.ZvtPassword));
    }

    [Theory]
    [InlineData(null, null, "http://localhost:9123", null, "pos.terminal_host")]
    [InlineData("http://192.168.1.60/x", null, "http://localhost:9123", null, "pos.terminal_host")]
    [InlineData("192.168.1.60", 70000, "http://localhost:9123", null, "pos.terminal_port")]
    [InlineData("192.168.1.60", null, null, null, "pos.terminal_bridge_url")]
    [InlineData("192.168.1.60", null, "ftp://localhost", null, "pos.terminal_bridge_url")]
    [InlineData("192.168.1.60", null, "http://user:pw@localhost:9123", null, "pos.terminal_bridge_url")]
    [InlineData("192.168.1.60", null, "http://localhost:9123", "12345", "pos.terminal_password")]
    [InlineData("192.168.1.60", null, "http://localhost:9123", "12345a", "pos.terminal_password")]
    public void Zvt_profile_validates_host_port_bridge_and_password(string? host, int? port, string? bridge, string? password, string code) =>
        Assert.Equal(code, TerminalProfile.Create(Org, Register, Zvt(host, port, bridge, password), true, Now).Error?.Code);

    [Fact]
    public void Simulated_terminal_is_refused_in_production_unless_the_staging_flag_is_set()
    {
        Assert.Equal("pos.terminal_simulated_not_allowed", TerminalProfile.CheckSimulatedAllowed(TerminalConnection.Simulated, true, false)?.Code);
        Assert.Null(TerminalProfile.CheckSimulatedAllowed(TerminalConnection.Simulated, true, true));
        Assert.Null(TerminalProfile.CheckSimulatedAllowed(TerminalConnection.Simulated, false, false));
        Assert.Null(TerminalProfile.CheckSimulatedAllowed(TerminalConnection.ZvtBridge, true, false));
        Assert.Null(TerminalProfile.CheckSimulatedAllowed(TerminalConnection.Manual, true, false));
    }

    [Theory]
    [InlineData("672510******1234", true)]
    [InlineData("************1234", true)]
    [InlineData("1234", true)]
    [InlineData("4111 11** **** 1111", true)]
    [InlineData("XXXXXXXXXXXX1234", true)]
    [InlineData("4111111111111111", false)]   // full PAN
    [InlineData("4111111*****1111", false)]   // 7 leading digits
    [InlineData("411111*****11111", false)]   // 5 trailing digits
    [InlineData("411111**1***1111", false)]   // digit inside the mask
    [InlineData("12345", false)]              // digits only, more than the last four
    [InlineData("4111-1111-1111-1111", false)]
    [InlineData("411111<script>1111", false)]
    [InlineData("", false)]
    public void Card_number_must_be_masked_to_first_6_and_last_4(string pan, bool masked) => Assert.Equal(masked, CardTransaction.IsMasked(pan));

    [Fact]
    public void Transaction_gets_its_result_once_and_a_full_pan_is_never_stored()
    {
        var transaction = Pending();
        Assert.Equal((CardTransactionStatus.Pending, false), (transaction.Status, transaction.IsTest));

        Assert.Equal("pos.card_pan_not_masked", transaction.Complete(CardTransactionStatus.Approved, Girocard with { MaskedPan = "6725101234561234" }, Now).Error?.Code);
        Assert.Equal(CardTransactionStatus.Pending, transaction.Status);
        Assert.Null(transaction.MaskedPan);
        Assert.Equal("pos.card_transaction_status", transaction.Complete(CardTransactionStatus.Reversed, Girocard, Now).Error?.Code);
        Assert.Equal("pos.card_transaction_status", transaction.Complete(CardTransactionStatus.Pending, Girocard, Now).Error?.Code);
        Assert.Equal("pos.card_result_field_length",
            transaction.Complete(CardTransactionStatus.Approved, Girocard with { CustomerReceiptText = new string('x', 4001) }, Now).Error?.Code);

        Assert.True(transaction.Complete(CardTransactionStatus.Approved, Girocard, Now.AddSeconds(20)).IsSuccess);
        Assert.Equal((CardTransactionStatus.Approved, "672510******1234", "000123", Now.AddSeconds(20)),
            (transaction.Status, transaction.MaskedPan, transaction.TraceNumber, transaction.CompletedAt));

        // Reporting the same status again changes nothing; another result is refused.
        Assert.True(transaction.Complete(CardTransactionStatus.Approved, Girocard with { TraceNumber = "999" }, Now.AddMinutes(1)).IsSuccess);
        Assert.Equal("000123", transaction.TraceNumber);
        var changed = transaction.Complete(CardTransactionStatus.Declined, Girocard, Now);
        Assert.Equal(("pos.card_transaction_final", (ErrorKind?)ErrorKind.Conflict), (changed.Error?.Code, changed.Error?.Kind));
    }

    [Fact]
    public void Only_an_approved_matching_unlinked_transaction_settles_a_payment_and_only_once()
    {
        var transaction = Pending(amount: 25_00);
        Assert.Equal("status", transaction.CheckUsable(Register, CardTransactionKind.Payment, 25_00).Error?.Details?["reason"]);
        transaction.Complete(CardTransactionStatus.Approved, Girocard, Now);

        Assert.True(transaction.CheckUsable(Register, CardTransactionKind.Payment, 25_00).IsSuccess);
        Assert.Equal("register", transaction.CheckUsable(Guid.NewGuid(), CardTransactionKind.Payment, 25_00).Error?.Details?["reason"]);
        Assert.Equal("kind", transaction.CheckUsable(Register, CardTransactionKind.Refund, 25_00).Error?.Details?["reason"]);
        Assert.Equal("amount", transaction.CheckUsable(Register, CardTransactionKind.Payment, 24_99).Error?.Details?["reason"]);
        var refused = transaction.CheckUsable(Register, CardTransactionKind.Payment, 26_00);
        Assert.Equal(("pos.card_transaction_invalid", (ErrorKind?)ErrorKind.Conflict), (refused.Error?.Code, refused.Error?.Kind));

        var receipt = Guid.NewGuid();
        Assert.True(transaction.LinkTo(receipt, Now).IsSuccess);
        Assert.Equal(receipt, transaction.ReceiptId);
        // A second receipt cannot use it, and it can no longer be reversed.
        Assert.Equal("linked", transaction.CheckUsable(Register, CardTransactionKind.Payment, 25_00).Error?.Details?["reason"]);
        Assert.Equal("pos.card_transaction_invalid", transaction.LinkTo(Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal(receipt, transaction.ReceiptId);
        Assert.Equal("pos.card_transaction_linked", transaction.Reverse("Storno", Now).Error?.Code);
    }

    [Fact]
    public void Reversal_needs_an_approved_unlinked_transaction_and_makes_it_unusable()
    {
        var declined = Pending();
        declined.Complete(CardTransactionStatus.Declined, Girocard, Now);
        Assert.Equal("pos.card_transaction_not_approved", declined.Reverse(null, Now).Error?.Code);
        Assert.Equal("pos.card_transaction_not_approved", Pending().Reverse(null, Now).Error?.Code);

        var approved = Pending();
        approved.Complete(CardTransactionStatus.Approved, Girocard, Now);
        Assert.True(approved.Reverse(" Verkauf fehlgeschlagen ", Now.AddMinutes(1)).IsSuccess);
        Assert.Equal((CardTransactionStatus.Reversed, "Verkauf fehlgeschlagen", Now.AddMinutes(1)), (approved.Status, approved.ReversalText, approved.ReversedAt));
        Assert.True(approved.Reverse(null, Now.AddMinutes(2)).IsSuccess); // repeated report
        Assert.Equal(Now.AddMinutes(1), approved.ReversedAt);
        Assert.Equal("status", approved.CheckUsable(Register, CardTransactionKind.Payment, 25_00).Error?.Details?["reason"]);
        Assert.Equal("pos.card_transaction_invalid", approved.LinkTo(Guid.NewGuid(), Now).Error?.Code);
    }

    [Fact]
    public void Transaction_remembers_the_profile_and_marks_the_simulated_terminal_as_test()
    {
        var simulated = TerminalProfile.Create(Org, Register, new TerminalProfileSettings("Simulation", TerminalConnection.Simulated, null, null, null, null), true, Now).Value;
        var transaction = Pending(profile: simulated);
        Assert.Equal((simulated.Id, "Simulation", true), (transaction.TerminalProfileId!.Value, transaction.TerminalName, transaction.IsTest));

        var foreign = TerminalProfile.Create(Org, Guid.NewGuid(), Zvt(), true, Now).Value;
        Assert.Equal("pos.terminal_not_found", CardTransaction.Start(Org, Register, CardTransactionKind.Payment, 10_00, foreign, User, Now).Error?.Code);
        Assert.Equal("pos.card_transaction_amount", CardTransaction.Start(Org, Register, CardTransactionKind.Payment, 0, null, User, Now).Error?.Code);
    }

    [Fact]
    public void Fingerprints_change_only_when_a_card_transaction_is_named()
    {
        var register = Guid.NewGuid();
        var product = Guid.NewGuid();
        var request = new CreateSaleRequest(register, [new SaleLineRequest(product, null, 2, 10_00, 1_00)], [new SalePaymentRequest(PaymentMethod.Card, 19_00)]);
        var legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"sale#{register:N}#{product:N}::2:1000:100#Card:1900")));
        Assert.Equal(legacy, SaleService.HashRequest(request));
        var card = Guid.NewGuid();
        var withCard = SaleService.HashRequest(request with { Payments = [new SalePaymentRequest(PaymentMethod.Card, 19_00, card)] });
        Assert.NotEqual(legacy, withCard);
        Assert.NotEqual(withCard, SaleService.HashRequest(request with { Payments = [new SalePaymentRequest(PaymentMethod.Card, 19_00, Guid.NewGuid())] }));

        var receipt = Guid.NewGuid();
        var returned = new ReturnRequest([new ReturnLineRequest(1, 1)], PaymentMethod.Card, "defekt", register);
        var legacyReturn = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"return#{receipt:N}#{register:N}#Card#1:1#defekt")));
        Assert.Equal(legacyReturn, SaleService.HashReturn(receipt, returned));
        Assert.NotEqual(legacyReturn, SaleService.HashReturn(receipt, returned with { CardTransactionId = card }));

        var invoice = Guid.NewGuid();
        var collect = new CollectInvoiceRequest(register, 50_00, [new SalePaymentRequest(PaymentMethod.Card, 50_00)]);
        var legacyCollect = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"collect#{invoice:N}#{register:N}#5000#Card:5000")));
        Assert.Equal(legacyCollect, SaleService.HashCollect(invoice, collect));
    }

    [Fact]
    public void Receipt_document_prints_the_terminal_data_under_the_card_payment()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()],
            [new PaymentInput(PaymentMethod.Cash, 9_00), new PaymentInput(PaymentMethod.Card, 10_00)]).Value;
        var transaction = Pending(amount: 10_00);
        transaction.Complete(CardTransactionStatus.Approved, Girocard, Now);
        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        var document = new DocumentExtras(transaction).Apply(
            ReceiptDocument.For(sale, new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null), "Kasse", null, fiscal));
        Assert.Null(document.Payments.Single(p => p.Method == "Cash").Card);
        Assert.Equal(new DocumentCard("girocard", "672510******1234", "000123", "A1B2C3", "60012345", "0042"), document.Payments.Single(p => p.Method == "Card").Card);
    }
}
