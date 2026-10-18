using HandyNet.Modules.Pos.Domain;

namespace HandyNet.UnitTests;

public class PosPinTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Theory]
    [InlineData("4711")]
    [InlineData("80524")]
    [InlineData("135792")]
    [InlineData("1235")]
    public void Accepts_four_to_six_digit_pins(string pin) => Assert.Null(PinPolicy.Check(pin));

    [Theory]
    [InlineData(null, "pos.pin_format")]
    [InlineData("", "pos.pin_format")]
    [InlineData("123", "pos.pin_format")]
    [InlineData("1234567", "pos.pin_format")]
    [InlineData("12a4", "pos.pin_format")]
    [InlineData("١٢٣٥", "pos.pin_format")] // Arabic-Indic digits are not ASCII digits
    [InlineData("0000", "pos.pin_too_simple")]
    [InlineData("1234", "pos.pin_too_simple")]
    [InlineData("987654", "pos.pin_too_simple")]
    [InlineData("01234", "pos.pin_too_simple")]
    public void Refuses_malformed_and_trivial_pins(string? pin, string code) => Assert.Equal(code, PinPolicy.Check(pin)?.Code);

    [Fact]
    public void Hash_is_salted_pbkdf2_and_verifies_only_the_right_pin()
    {
        var a = PinHasher.Hash("4711");
        var b = PinHasher.Hash("4711");
        Assert.StartsWith($"pbkdf2-sha256${PinHasher.Iterations}$", a);
        Assert.NotEqual(a, b); // per-hash salt
        Assert.DoesNotContain("4711", a);
        Assert.True(PinHasher.Verify("4711", a));
        Assert.True(PinHasher.Verify("4711", b));
        Assert.False(PinHasher.Verify("4712", a));
        Assert.False(PinHasher.Verify("4711", null));
        Assert.False(PinHasher.Verify("4711", "plain$4711"));
        Assert.False(PinHasher.Verify("4711", "pbkdf2-sha256$100000$not base64$x"));
    }

    [Fact]
    public void Five_wrong_pins_lock_for_fifteen_minutes_and_a_new_pin_lifts_the_lock()
    {
        var pin = CashierPin.For(Org, User, Now, User);
        Assert.Equal(PinCheck.NotSet, pin.Verify("4711", Now));
        Assert.True(pin.Set("4711", User, Now).IsSuccess);

        for (var i = 1; i < CashierPin.MaxFailedAttempts; i++)
        {
            Assert.Equal(PinCheck.Invalid, pin.Verify("0815", Now));
            Assert.Equal(CashierPin.MaxFailedAttempts - i, pin.RemainingAttempts);
        }
        Assert.Equal(PinCheck.Locked, pin.Verify("0815", Now));
        Assert.Equal(Now.AddMinutes(CashierPin.LockoutMinutes), pin.LockedUntil);
        // Even the right PIN is refused while locked.
        Assert.Equal(PinCheck.Locked, pin.Verify("4711", Now.AddMinutes(14)));
        // After the lockout a fresh series starts; the right PIN works and resets the counter.
        Assert.Equal(PinCheck.Invalid, pin.Verify("0815", Now.AddMinutes(16)));
        Assert.Equal(CashierPin.MaxFailedAttempts - 1, pin.RemainingAttempts);
        Assert.Equal(PinCheck.Valid, pin.Verify("4711", Now.AddMinutes(16)));
        Assert.Equal(CashierPin.MaxFailedAttempts, pin.RemainingAttempts);

        // Owner sets a new PIN while locked: unlocked immediately.
        for (var i = 0; i < CashierPin.MaxFailedAttempts; i++)
            pin.Verify("0815", Now.AddMinutes(20));
        Assert.True(pin.IsLocked(Now.AddMinutes(20)));
        Assert.True(pin.Set("2580", User, Now.AddMinutes(21)).IsSuccess);
        Assert.False(pin.IsLocked(Now.AddMinutes(21)));
        Assert.Equal(PinCheck.Valid, pin.Verify("2580", Now.AddMinutes(21)));

        Assert.Equal("pos.pin_too_simple", pin.Set("1111", User, Now).Error?.Code);
        Assert.True(pin.Set(null, User, Now).IsSuccess);
        Assert.False(pin.HasPin);
    }

    [Fact]
    public void Cashier_session_ends_by_lock_expiry_or_inactivity()
    {
        var session = CashierSession.Start(Org, Guid.NewGuid(), User, Guid.NewGuid(), OpaqueToken.HashOf(OpaqueToken.New()), Now, TimeSpan.FromHours(12));
        Assert.True(session.IsActive(Now.AddHours(11), autoLockMinutes: null));
        Assert.False(session.IsActive(Now.AddHours(12), autoLockMinutes: null));
        // Auto-lock after 5 idle minutes; activity (touch) keeps it alive.
        Assert.True(session.IsActive(Now.AddMinutes(4), 5));
        Assert.False(session.IsActive(Now.AddMinutes(5), 5));
        Assert.True(session.NeedsTouch(Now.AddSeconds(30)));
        Assert.False(session.NeedsTouch(Now.AddSeconds(29)));
        session.Touch(Now.AddMinutes(4));
        Assert.True(session.IsActive(Now.AddMinutes(8), 5));
        session.End(CashierSessionEnd.Locked, Now.AddMinutes(8));
        Assert.False(session.IsActive(Now.AddMinutes(8), null));
        session.End(CashierSessionEnd.Replaced, Now.AddMinutes(9));
        Assert.Equal(CashierSessionEnd.Locked, session.EndedReason); // first end wins
    }

    [Fact]
    public void Opaque_tokens_are_random_url_safe_and_stored_as_sha256()
    {
        var token = OpaqueToken.New();
        Assert.True(OpaqueToken.LooksValid(token));
        Assert.NotEqual(token, OpaqueToken.New());
        Assert.Equal(64, OpaqueToken.HashOf(token).Length);
        Assert.False(OpaqueToken.LooksValid(null));
        Assert.False(OpaqueToken.LooksValid(token + "x"));
        Assert.False(OpaqueToken.LooksValid(token[..42] + "!"));
    }

    [Fact]
    public void Approval_needs_a_manager_with_the_rights_and_is_used_exactly_once()
    {
        var manager = Guid.NewGuid();
        var managerRights = HandyNet.SharedKernel.Tenancy.Permissions.For(HandyNet.SharedKernel.Tenancy.MembershipRole.Manager, null);
        PosApproval Grant(params string[] permissions) => PosApproval.Grant(Org, Guid.NewGuid(), permissions, manager,
            HandyNet.SharedKernel.Tenancy.MembershipRole.Manager, managerRights, User, User, "h", Now, TimeSpan.FromMinutes(2)).Value;

        Assert.Equal("pos.approval_permissions", PosApproval.Grant(Org, Guid.NewGuid(), [], manager,
            HandyNet.SharedKernel.Tenancy.MembershipRole.Manager, managerRights, User, User, "h", Now, TimeSpan.FromMinutes(2)).Error?.Code);
        Assert.Equal("pos.approval_permissions", PosApproval.Grant(Org, Guid.NewGuid(), ["pos.manage"], manager,
            HandyNet.SharedKernel.Tenancy.MembershipRole.Manager, managerRights, User, User, "h", Now, TimeSpan.FromMinutes(2)).Error?.Code);
        Assert.Equal("pos.approver_not_manager", PosApproval.Grant(Org, Guid.NewGuid(), ["pos.discount"], manager,
            HandyNet.SharedKernel.Tenancy.MembershipRole.Staff, managerRights, User, User, "h", Now, TimeSpan.FromMinutes(2)).Error?.Code);
        Assert.Equal("pos.approval_self", PosApproval.Grant(Org, Guid.NewGuid(), ["pos.discount"], User,
            HandyNet.SharedKernel.Tenancy.MembershipRole.Manager, managerRights, User, User, "h", Now, TimeSpan.FromMinutes(2)).Error?.Code);

        var approval = Grant("pos.refund", "pos.discount", "pos.discount");
        Assert.Equal(["pos.discount", "pos.refund"], approval.Permissions);
        Assert.True(approval.IsUsable(Now.AddSeconds(119)));
        Assert.False(approval.IsUsable(Now.AddSeconds(120)));
        Assert.Equal("pos.approval_target", approval.MarkUsed(ApprovalUse.Sale, null, null, null, Now).Error?.Code);
        Assert.True(approval.MarkUsed(ApprovalUse.Sale, Guid.NewGuid(), null, null, Now).IsSuccess);
        Assert.Equal("pos.approval_used", approval.MarkUsed(ApprovalUse.Sale, Guid.NewGuid(), null, null, Now).Error?.Code);
        Assert.Equal("pos.approval_used", Grant("pos.refund").MarkUsed(ApprovalUse.Return, Guid.NewGuid(), null, null, Now.AddMinutes(3)).Error?.Code);
    }

    [Fact]
    public void E_receipt_email_renders_the_receipt_as_text_and_encoded_html()
    {
        var content = new HandyNet.Modules.Pos.Contracts.EReceiptContent("Kassenbeleg", "Handy <Shop>", "Handy Shop GmbH", ["Hauptstr. 1", "10115 Berlin"],
            "DE123456789", "Kasse 1", 42, Now, "EUR",
            [new HandyNet.Modules.Pos.Contracts.EReceiptLine("Panzerglas", null, 2, 10_00, 1_00, 19_00, "A", "montiert & geprüft"),
             new HandyNet.Modules.Pos.Contracts.EReceiptLine("iPhone 12", "350000000000001", 1, 300_00, 0, 300_00, "§25a", null)],
            [new HandyNet.Modules.Pos.Contracts.EReceiptVat("A 19 %", 15_97, 3_03, 19_00)], 319_00,
            [new HandyNet.Modules.Pos.Contracts.EReceiptPayment("Cash", 319_00, 320_00)], 1_00, ["Gebrauchtgegenstände/Sonderregelung"]);
        var email = HandyNet.Modules.Notifications.Domain.EReceiptTemplate.Render(content);
        Assert.Equal("Kassenbeleg Nr. 42 – Handy <Shop>", email.Subject);
        Assert.Contains("2 × Panzerglas  19,00 € A", email.Body);
        Assert.Contains("Rabatt -1,00 €", email.Body);
        Assert.Contains("IMEI 350000000000001", email.Body);
        Assert.Contains("Bar 320,00 €", email.Body);
        Assert.Contains("Rückgeld 1,00 €", email.Body);
        Assert.Contains("Handy &lt;Shop&gt;", email.Html);
        Assert.Contains("montiert &amp; geprüft", email.Html);
        Assert.DoesNotContain("<Shop>", email.Html);
    }

    [Fact]
    public void E_receipt_requests_validate_the_address_limit_per_receipt_and_mask_it()
    {
        Assert.Equal("pos.receipt_email_invalid", ReceiptEmail.NormalizeAddress("kein-at").Error?.Code);
        Assert.Equal("pos.receipt_email_invalid", ReceiptEmail.NormalizeAddress("Name <a@b.de>").Error?.Code);
        Assert.Equal("kunde@example.de", ReceiptEmail.NormalizeAddress(" Kunde@Example.de ").Value);
        Assert.Equal("k***@example.de", HandyNet.Modules.Pos.Application.EReceiptService.Mask("kunde@example.de"));
        Assert.Equal("***@x.de", HandyNet.Modules.Pos.Application.EReceiptService.Mask("a@x.de"));
    }

    [Fact]
    public void Register_auto_lock_is_off_or_between_1_and_240_minutes()
    {
        var register = Register.Create(Org, "Kasse", Guid.NewGuid(), Now).Value;
        Assert.True(register.SetAutoLock(5).IsSuccess);
        Assert.Equal(5, register.AutoLockMinutes);
        Assert.True(register.SetAutoLock(0).IsSuccess);
        Assert.Null(register.AutoLockMinutes);
        Assert.Equal("pos.auto_lock_minutes", register.SetAutoLock(241).Error?.Code);
        Assert.Equal("pos.auto_lock_minutes", register.SetAutoLock(-1).Error?.Code);
    }
}
