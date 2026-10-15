using HandyNet.Modules.Ledger.Domain;
using HandyNet.Modules.Payments.Domain;

namespace HandyNet.UnitTests;

public class CommissionTests
{
    [Fact]
    public void Commission_is_charged_on_net_goods_value_plus_vat()
    {
        // 269,70 € net, 320,94 € gross, 3 % commission = 8,09 €, VAT 19 % on it = 1,54 €
        var split = CommissionSplit.Calculate(32094, 26970, 300, 1900);
        Assert.Equal(809, split.CommissionNetMinor);
        Assert.Equal(154, split.CommissionVatMinor);
        Assert.Equal(32094 - 809 - 154, split.SellerShareMinor);
    }

    [Fact]
    public void Zero_commission_pays_everything_to_the_seller() =>
        Assert.Equal(1000, CommissionSplit.Calculate(1000, 840, 0, 1900).SellerShareMinor);
}

public class PaymentJournalTests
{
    private static Payment NewPayment() => Payment.Create(Guid.NewGuid(), Guid.NewGuid(), "B2B-001000", Guid.NewGuid(), Guid.NewGuid(), PaymentProvider.Simulated,
        "sim_1", null, CommissionSplit.Calculate(32094, 26970, 300, 1900), 300, DateTimeOffset.UnixEpoch).Value;

    [Fact]
    public void Every_entry_balances()
    {
        var payment = NewPayment();
        payment.Apply(ProviderPaymentStatus.Paid, DateTimeOffset.UnixEpoch);
        foreach (var draft in new[]
                 {
                     PaymentJournal.Capture(payment, DateTimeOffset.UnixEpoch),
                     PaymentJournal.Refund(payment, DateTimeOffset.UnixEpoch),
                     PaymentJournal.Payout(payment, Guid.NewGuid(), DateTimeOffset.UnixEpoch),
                 })
        {
            Assert.Null(JournalRules.Validate(draft));
            Assert.Equal(0, draft.Lines.Sum(l => l.AmountMinor));
        }
    }

    [Fact]
    public void Capture_then_payout_leaves_the_seller_at_zero_and_the_platform_with_its_commission()
    {
        var payment = NewPayment();
        var lines = PaymentJournal.Capture(payment, DateTimeOffset.UnixEpoch).Lines
            .Concat(PaymentJournal.Payout(payment, Guid.NewGuid(), DateTimeOffset.UnixEpoch).Lines).ToList();

        long Balance(AccountType type) => lines.Where(l => l.Account == type).Sum(l => l.AmountMinor);
        Assert.Equal(0, Balance(AccountType.SellerPayable));
        Assert.Equal(809 + 154, Balance(AccountType.PspClearing)); // what stays at the PSP = platform's commission + VAT
        Assert.Equal(-809, Balance(AccountType.CommissionRevenue));
        Assert.Equal(-154, Balance(AccountType.VatPayable));
    }

    [Fact]
    public void Refund_exactly_reverses_capture()
    {
        var payment = NewPayment();
        var all = PaymentJournal.Capture(payment, DateTimeOffset.UnixEpoch).Lines.Concat(PaymentJournal.Refund(payment, DateTimeOffset.UnixEpoch).Lines);
        Assert.All(all.GroupBy(l => (l.Account, l.OrganizationId)), g => Assert.Equal(0, g.Sum(l => l.AmountMinor)));
    }

    [Fact]
    public void Unbalanced_or_ownerless_entries_are_rejected()
    {
        var org = Guid.NewGuid();
        Assert.Equal("ledger.unbalanced", JournalRules.Validate(new JournalEntryDraft("k", "d", "t", Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            [new(AccountType.PspClearing, null, 100, "EUR"), new(AccountType.SellerPayable, org, -99, "EUR")]))!.Code);
        Assert.Equal("ledger.account_owner", JournalRules.Validate(new JournalEntryDraft("k", "d", "t", Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            [new(AccountType.PspClearing, null, 100, "EUR"), new(AccountType.SellerPayable, null, -100, "EUR")]))!.Code);
    }

    [Fact]
    public void Terminal_payment_states_never_change()
    {
        var payment = NewPayment();
        Assert.Equal(PaymentChange.BecamePaid, payment.Apply(ProviderPaymentStatus.Paid, DateTimeOffset.UnixEpoch));
        Assert.Equal(PaymentChange.None, payment.Apply(ProviderPaymentStatus.Paid, DateTimeOffset.UnixEpoch));   // replayed webhook
        Assert.Equal(PaymentChange.None, payment.Apply(ProviderPaymentStatus.Failed, DateTimeOffset.UnixEpoch)); // late webhook
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }
}
