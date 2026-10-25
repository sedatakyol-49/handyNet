using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.UnitTests;

public class PosPricingTests
{
    [Theory]
    [InlineData(89_90, 106_98)] // 89.90 × 1.19 = 106.981
    [InlineData(100, 119)]
    [InlineData(50, 60)]        // VAT 9.5 → 10 (half away from zero)
    [InlineData(1, 1)]          // VAT 0.19 → 0
    [InlineData(3, 4)]          // VAT 0.57 → 1
    public void Offer_net_price_becomes_gross_with_19_percent(long net, long gross) =>
        Assert.Equal(gross, PosPricing.GrossFromNet(net));

    [Fact]
    public void Own_pos_price_wins_over_the_offer_and_none_means_free_entry()
    {
        Assert.Equal(new EffectivePrice(12_99, PriceSource.PosPrice), PosPricing.Resolve(12_99, 100_00));
        Assert.Equal(new EffectivePrice(119_00, PriceSource.Offer), PosPricing.Resolve(null, 100_00));
        Assert.Equal(EffectivePrice.None, PosPricing.Resolve(null, null));
    }

    [Fact]
    public void Pos_price_must_be_positive_or_cleared()
    {
        var user = Guid.NewGuid();
        Assert.Equal("pos.price", PosPrice.Create(Guid.NewGuid(), Guid.NewGuid(), 0, user, DateTimeOffset.UtcNow).Error?.Code);
        Assert.Equal("pos.price", PosPrice.Create(Guid.NewGuid(), Guid.NewGuid(), PosPricing.MaxPriceMinor + 1, user, DateTimeOffset.UtcNow).Error?.Code);
        var price = PosPrice.Create(Guid.NewGuid(), Guid.NewGuid(), 5_00, user, DateTimeOffset.UtcNow).Value;
        Assert.True(price.Change(null, user, DateTimeOffset.UtcNow).IsSuccess);
        Assert.Null(price.PriceMinor);
    }
}

public class PosLinePolicyTests
{
    private static readonly EffectivePrice Known = new(20_00, PriceSource.PosPrice);
    private static readonly CashierRights NoRights = new(false, false, 0);

    [Fact]
    public void Price_other_than_the_till_price_needs_the_override_right()
    {
        Assert.True(LinePolicy.Check(1, 20_00, 1, 0, Known, NoRights).IsSuccess);
        var denied = LinePolicy.Check(2, 19_99, 1, 0, Known, NoRights);
        Assert.Equal(("pos.price_override_forbidden", (ErrorKind?)ErrorKind.Forbidden), (denied.Error?.Code, denied.Error?.Kind));
        Assert.Equal(20_00L, denied.Error!.Details!["expectedUnitPriceMinor"]);
        Assert.True(LinePolicy.Check(1, 19_99, 1, 0, Known, NoRights with { MayOverridePrice = true }).IsSuccess);
        // Without a known price the cashier enters it freely.
        Assert.True(LinePolicy.Check(1, 7_77, 1, 0, EffectivePrice.None, NoRights).IsSuccess);
    }

    [Theory]
    [InlineData(1, 2_00, true)]   // exactly 10 % of 20.00
    [InlineData(1, 2_01, false)]  // one cent over
    [InlineData(3, 6_00, true)]   // 10 % of 3 × 20.00
    [InlineData(3, 6_01, false)]
    public void Discount_is_limited_to_the_cashiers_percentage(int quantity, long discount, bool allowed)
    {
        var result = LinePolicy.Check(1, 20_00, quantity, discount, Known, new CashierRights(false, true, 10));
        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
            Assert.Equal("pos.discount_limit_exceeded", result.Error?.Code);
    }

    [Fact]
    public void Any_discount_needs_the_discount_right()
    {
        Assert.Equal("pos.discount_forbidden", LinePolicy.Check(1, 20_00, 1, 1, Known, NoRights).Error?.Code);
        Assert.True(LinePolicy.Check(1, 20_00, 1, 20_00, Known, CashierRights.Unrestricted).IsSuccess);
    }
}

public class PosCashCountTests
{
    [Fact]
    public void Counts_are_validated_sorted_and_summed()
    {
        var normalized = CashCount.Normalize([new(100, 5), new(5_000, 2), new(1, 0)]).Value;
        Assert.Equal([5_000L, 100L], normalized.Select(d => d.ValueMinor));
        Assert.Equal(105_00, CashCount.Total(normalized));
        Assert.Equal("pos.denomination_unknown", CashCount.Normalize([new(300, 1)]).Error?.Code);
        Assert.Equal("pos.denomination_duplicate", CashCount.Normalize([new(100, 1), new(100, 2)]).Error?.Code);
        Assert.Equal("pos.denomination_count", CashCount.Normalize([new(100, -1)]).Error?.Code);
    }

    [Fact]
    public void Amount_and_denominations_must_agree()
    {
        Assert.Equal((105_00L, 2), (CashCount.Resolve(null, [new(5_000, 2), new(500, 1)], "x").Value.AmountMinor,
            CashCount.Resolve(null, [new(5_000, 2), new(500, 1)], "x").Value.Counts!.Count));
        Assert.Equal("pos.denominations_mismatch", CashCount.Resolve(100_00, [new(5_000, 2), new(500, 1)], "x").Error?.Code);
        Assert.Equal((42L, (IReadOnlyList<DenominationCount>?)null), CashCount.Resolve(42, null, "x").Value);
        Assert.Equal("pos.cash_amount_required", CashCount.Resolve(null, null, "x").Error?.Code);
    }

    [Fact]
    public void Closing_with_denominations_stores_them_and_the_difference_to_expected_cash()
    {
        var now = DateTimeOffset.UtcNow;
        var org = Guid.NewGuid();
        var register = Register.Create(org, "Kasse", Guid.NewGuid(), now).Value;
        Assert.Equal("pos.denominations_mismatch",
            CashSession.Open(org, register, 10_00, Guid.NewGuid(), now, [new DenominationCount(500, 1)]).Error?.Code);
        var session = CashSession.Open(org, register, 10_00, Guid.NewGuid(), now, [new DenominationCount(500, 2)]).Value;
        var summary = SessionCalculator.Summarize(10_00, [], []);
        Assert.Equal("pos.denominations_mismatch",
            session.Close(summary, 10_00, null, Guid.NewGuid(), now, 1, [new DenominationCount(500, 1)]).Error?.Code);
        Assert.True(session.Close(summary, 9_00, null, Guid.NewGuid(), now, 1, [new DenominationCount(500, 1), new DenominationCount(200, 2)]).IsSuccess);
        Assert.Equal((10_00L, 9_00L, -1_00L), (session.ExpectedCashMinor!.Value, session.CountedCashMinor!.Value, session.DifferenceMinor!.Value));
        Assert.Equal(2, session.ClosingDenominations!.Count);
        Assert.Equal(2, session.OpeningDenominations!.Single().Count);
    }

    [Fact]
    public void Movement_reasons_are_predefined_per_kind_with_free_text_for_other()
    {
        Assert.Equal("Bankeinzahlung", CashMovementReasons.Resolve(CashMovementKind.Withdrawal, CashMovementReason.BankDeposit, null).Value);
        Assert.Equal("Bank Sparkasse", CashMovementReasons.Resolve(CashMovementKind.Withdrawal, CashMovementReason.BankDeposit, " Bank Sparkasse ").Value);
        Assert.Equal("pos.reason", CashMovementReasons.Resolve(CashMovementKind.Deposit, CashMovementReason.Other, " ").Error?.Code);
        Assert.Equal("pos.reason_code", CashMovementReasons.Resolve(CashMovementKind.Deposit, CashMovementReason.Expense, null).Error?.Code);
        Assert.Equal("pos.reason", CashMovementReasons.Resolve(CashMovementKind.Deposit, null, null).Error?.Code);

        var now = DateTimeOffset.UtcNow;
        var org = Guid.NewGuid();
        var session = CashSession.Open(org, Register.Create(org, "K", Guid.NewGuid(), now).Value, 50_00, Guid.NewGuid(), now).Value;
        var movement = CashMovement.Record(session, CashMovementKind.Withdrawal, 10_00, null, 50_00, Guid.NewGuid(), now, CashMovementReason.OwnerWithdrawal).Value;
        Assert.Equal(("Privatentnahme", CashMovementReason.OwnerWithdrawal), (movement.Reason, movement.ReasonCode!.Value));
    }
}

public class PosPermissionTests
{
    [Fact]
    public void Owners_and_managers_hold_every_pos_right_whatever_is_configured()
    {
        foreach (var role in new[] { MembershipRole.Owner, MembershipRole.Manager })
        {
            var permissions = Permissions.For(role, []);
            Assert.True(Permissions.ConfigurablePos.All(permissions.Contains));
            Assert.Contains(Permissions.PosManage, permissions);
            Assert.Equal(100, Permissions.MaxDiscountPercentFor(role, 5));
        }
    }

    [Fact]
    public void Staff_defaults_keep_previous_rights_and_configuration_replaces_only_pos_rights()
    {
        var defaults = Permissions.For(MembershipRole.Staff, null);
        Assert.True(Permissions.DefaultStaffPos.All(defaults.Contains));
        Assert.DoesNotContain(Permissions.PosPriceOverride, defaults);
        Assert.DoesNotContain(Permissions.PosManage, defaults);

        var configured = Permissions.For(MembershipRole.Staff, [Permissions.PosPriceOverride, Permissions.OrganizationManage, Permissions.PosManage]);
        Assert.Contains(Permissions.PosPriceOverride, configured);
        Assert.Contains(Permissions.PosSell, configured);
        Assert.Contains(Permissions.OrdersPlace, configured);
        Assert.DoesNotContain(Permissions.PosRefund, configured);
        Assert.DoesNotContain(Permissions.OrganizationManage, configured); // never granted through POS configuration
        Assert.DoesNotContain(Permissions.PosManage, configured);
        Assert.Equal((100, 10, 0), (Permissions.MaxDiscountPercentFor(MembershipRole.Staff, null), Permissions.MaxDiscountPercentFor(MembershipRole.Staff, 10),
            Permissions.MaxDiscountPercentFor(MembershipRole.Staff, 0)));
    }

    [Fact]
    public void Tenant_context_applies_the_membership_configuration()
    {
        var context = new TenantContext();
        context.Set(Guid.NewGuid(), true, MembershipRole.Staff, new HashSet<PlatformModule>(), [Permissions.PosDiscount], 15);
        Assert.Equal(15, context.MaxDiscountPercent);
        Assert.Contains(Permissions.PosDiscount, context.Permissions);
        Assert.DoesNotContain(Permissions.PosCloseSession, context.Permissions);
    }

    [Fact]
    public void Only_staff_memberships_can_be_configured()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("staff.pos_rights_role", Membership.Create(Guid.NewGuid(), Guid.NewGuid(), MembershipRole.Owner, now).SetPosRights([], 0).Error?.Code);
        Assert.Equal("staff.pos_rights_role", Membership.Create(Guid.NewGuid(), Guid.NewGuid(), MembershipRole.Manager, now).SetPosRights(null, 10).Error?.Code);
        var staff = Membership.Create(Guid.NewGuid(), Guid.NewGuid(), MembershipRole.Staff, now);
        Assert.Equal("staff.pos_permission_unknown", staff.SetPosRights(["imports.manage"], null).Error?.Code);
        Assert.Equal("staff.max_discount", staff.SetPosRights(null, 101).Error?.Code);
        Assert.True(staff.SetPosRights([Permissions.PosRefund, Permissions.PosRefund, Permissions.PosDiscount], 20).IsSuccess);
        Assert.Equal([Permissions.PosDiscount, Permissions.PosRefund], staff.PosPermissions!);
        Assert.True(staff.SetPosRights(null, null).IsSuccess);
        Assert.Null(staff.PosPermissions);
    }
}

public class PosCustomerAndNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CustomerDetails Details(string name = "Erika Muster", string? email = null, string? vat = null, string? country = null) =>
        new(name, "Muster GmbH", email, "+49 30 1", "Weg 1", "10115", "Berlin", country, vat);

    [Fact]
    public void Customer_data_is_validated_and_normalized()
    {
        var c = Customer.Create(Guid.NewGuid(), Details(vat: "de 123-456-789", country: "de", email: "erika@example.de"), Guid.NewGuid(), Now).Value;
        Assert.Equal(("DE123456789", "DE", "erika@example.de"), (c.VatId, c.CountryCode, c.Email));
        Assert.Equal("pos.customer_name", Customer.Create(Guid.NewGuid(), Details(name: " "), Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.customer_email", Customer.Create(Guid.NewGuid(), Details(email: "nope"), Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.customer_country", Customer.Create(Guid.NewGuid(), Details(country: "DEU"), Guid.NewGuid(), Now).Error?.Code);
        Assert.Equal("pos.customer_vat_id", Customer.Create(Guid.NewGuid(), Details(vat: "DE-!"), Guid.NewGuid(), Now).Error?.Code);
    }

    [Fact]
    public void Identifying_customer_fields_are_marked_as_personal_data()
    {
        var personal = typeof(Customer).GetProperties()
            .Where(p => p.IsDefined(typeof(HandyNet.SharedKernel.PersonalDataAttribute), true)).Select(p => p.Name).Order().ToList();
        Assert.Equal(["City", "Company", "Email", "Name", "Phone", "PostalCode", "Street", "VatId"], personal);
    }

    [Fact]
    public void Line_notes_and_the_customer_are_part_of_the_receipt_and_carried_to_the_storno()
    {
        var customer = Customer.Create(Guid.NewGuid(), Details(email: "e@x.de"), Guid.NewGuid(), Now).Value.ToDocument();
        var header = PosReceiptTests.Header() with { Customer = customer };
        var sale = Receipt.CreateSale(header, [PosReceiptTests.Cable() with { Note = "  Folie montiert " }],
            [new PaymentInput(PaymentMethod.Cash, 19_00)]).Value;
        Assert.Equal("Folie montiert", sale.Lines[0].Note);
        Assert.Equal(customer.Id, sale.CustomerId);
        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        var seller = new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null);
        sale.Seal(fiscal, ReceiptDocument.For(sale, seller, "Kasse", null, fiscal));
        Assert.Equal(("Folie montiert", "Muster GmbH"), (sale.Document!.Lines[0].Note, sale.Document.Customer!.Company));

        var storno = Receipt.CreateStorno(PosReceiptTests.Header(), sale, hasReturns: false, "Fehlbon").Value;
        Assert.Equal((customer.Id, "Folie montiert"), (storno.CustomerId, storno.Lines[0].Note));
        Assert.Equal(customer, storno.Customer);

        var tooLong = PosReceiptTests.Cable() with { Note = new string('x', Receipt.MaxNoteLength + 1) };
        Assert.Equal("pos.line_note", Receipt.CreateSale(PosReceiptTests.Header(), [tooLong], [new PaymentInput(PaymentMethod.Cash, 19_00)]).Error?.Code);
    }

    [Fact]
    public void Request_fingerprint_is_unchanged_for_requests_without_note_or_customer()
    {
        var register = Guid.NewGuid();
        var product = Guid.NewGuid();
        var request = new CreateSaleRequest(register, [new SaleLineRequest(product, null, 2, 10_00, 1_00)], [new SalePaymentRequest(PaymentMethod.Cash, 20_00)]);
        // Formula used before notes and customers existed (receipts issued earlier keep replaying with their stored hash).
        var legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"sale#{register:N}#{product:N}::2:1000:100#Cash:2000")));
        Assert.Equal(legacy, SaleService.HashRequest(request));
        Assert.NotEqual(legacy, SaleService.HashRequest(request with { CustomerId = Guid.NewGuid() }));
        Assert.NotEqual(legacy, SaleService.HashRequest(request with { Lines = [request.Lines[0] with { Note = "x" }] }));
    }

    [Fact]
    public void Reprints_are_numbered_and_need_an_issued_receipt()
    {
        var sale = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()], [new PaymentInput(PaymentMethod.Cash, 19_00)]).Value;
        Assert.Equal("pos.receipt_not_issued", ReceiptReprint.Record(sale, 0, Guid.NewGuid(), Now).Error?.Code);
        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        sale.Seal(fiscal, ReceiptDocument.For(sale, new SellerInfo("S", "S", "W", "1", "B", "DE", null), "K", null, fiscal));
        Assert.Equal(3, ReceiptReprint.Record(sale, 2, Guid.NewGuid(), Now).Value.Sequence);
    }
}
