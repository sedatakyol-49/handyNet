using HandyNet.Modules.Identity.Domain;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;

namespace HandyNet.UnitTests;

/// <summary>Domain rules of the platform administration (ADR-0013): accounts, organization status/master data, memberships.</summary>
public class PlatformAdministrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();

    private static User NewUser() => User.Create("Person@Example.de", "Person", Now);

    private static OrganizationProfile Profile(string name = "Shop") =>
        new(name, name + " GmbH", "DE123456789", "Weg 1", "10115", "Berlin", "de", false, true, false);

    // ---------------------------------------------------------------------------------------------- users

    [Fact]
    public void Disable_requires_a_reason_refuses_self_and_enable_resets_lockout()
    {
        var user = NewUser();
        Assert.Equal("reason.required", user.Disable(Admin, "  ", Now).Error!.Code);
        Assert.Equal("reason.too_long", user.Disable(Admin, new string('x', AdminReason.MaxLength + 1), Now).Error!.Code);
        Assert.Equal("user.self_disable", user.Disable(user.Id, "x", Now).Error!.Code);

        for (var i = 0; i < User.MaxFailedLogins; i++)
            user.RecordFailedLogin(Now);
        Assert.True(user.IsLockedOut(Now));

        Assert.True(user.Disable(Admin, " Betrug ", Now).IsSuccess);
        Assert.False(user.IsActive);
        Assert.Equal("Betrug", user.DisabledReason);
        Assert.Equal(Now, user.DisabledAt);
        Assert.Equal("user.already_disabled", user.Disable(Admin, "x", Now).Error!.Code);

        Assert.True(user.Enable().IsSuccess);
        Assert.True(user.IsActive);
        Assert.Null(user.DisabledAt);
        Assert.Null(user.DisabledReason);
        Assert.False(user.IsLockedOut(Now));
        Assert.Equal("user.not_disabled", user.Enable().Error!.Code);
    }

    [Fact]
    public void Clear_lockout_only_when_locked_or_failed_attempts_exist()
    {
        var user = NewUser();
        Assert.Equal("user.not_locked_out", user.ClearLockout(Now).Error!.Code);
        user.RecordFailedLogin(Now);
        Assert.True(user.ClearLockout(Now).IsSuccess);
        Assert.Equal(0, user.FailedLoginCount);
        for (var i = 0; i < User.MaxFailedLogins; i++)
            user.RecordFailedLogin(Now);
        Assert.True(user.ClearLockout(Now).IsSuccess);
        Assert.False(user.IsLockedOut(Now));
        // An expired lockout is no lockout.
        for (var i = 0; i < User.MaxFailedLogins; i++)
            user.RecordFailedLogin(Now);
        Assert.Equal("user.not_locked_out", user.ClearLockout(Now + User.LockoutDuration + TimeSpan.FromSeconds(1)).Error!.Code);
    }

    [Fact]
    public void Platform_admin_role_cannot_be_revoked_from_oneself_or_granted_to_disabled_accounts()
    {
        var user = NewUser();
        Assert.Equal("user.not_platform_admin", user.DemoteFromPlatformAdmin(Admin).Error!.Code);
        Assert.True(user.PromoteToPlatformAdmin().IsSuccess);
        Assert.Equal("user.already_platform_admin", user.PromoteToPlatformAdmin().Error!.Code);
        Assert.Equal("user.self_admin_revoke", user.DemoteFromPlatformAdmin(user.Id).Error!.Code);
        Assert.True(user.DemoteFromPlatformAdmin(Admin).IsSuccess);
        Assert.False(user.IsPlatformAdmin);

        user.Disable(Admin, "x", Now);
        Assert.Equal("user.disabled", user.PromoteToPlatformAdmin().Error!.Code);
    }

    [Fact]
    public void Admin_email_actions_report_their_outcome()
    {
        var user = NewUser();
        Assert.True(user.RequestPasswordResetByAdmin(Now, "de").IsSuccess);
        Assert.Contains(user.DomainEvents, e => e is HandyNet.Modules.Identity.Contracts.PasswordResetRequested);
        Assert.Equal("auth.reset_recently_sent", user.RequestPasswordResetByAdmin(Now.AddSeconds(30), "de").Error!.Code);
        Assert.True(user.RequestPasswordResetByAdmin(Now + User.AccountEmailCooldown, "en").IsSuccess);

        Assert.True(user.RequestEmailVerificationByAdmin(Now, null).IsSuccess);
        Assert.Equal("reason.required", user.ConfirmEmailByAdmin(null, Now).Error!.Code);
        Assert.True(user.ConfirmEmailByAdmin("Telefonisch", Now).IsSuccess);
        Assert.True(user.EmailVerified);
        Assert.Equal("auth.email_already_verified", user.ConfirmEmailByAdmin("x", Now).Error!.Code);
        Assert.Equal("auth.email_already_verified", user.RequestEmailVerificationByAdmin(Now.AddHours(1), null).Error!.Code);

        user.Disable(Admin, "x", Now);
        Assert.Equal("user.disabled", user.RequestPasswordResetByAdmin(Now.AddHours(1), null).Error!.Code);
    }

    [Fact]
    public void Display_name_is_required_trimmed_and_limited()
    {
        var user = NewUser();
        Assert.Equal("user.display_name", user.ChangeDisplayName(" ").Error!.Code);
        Assert.Equal("user.display_name", user.ChangeDisplayName(new string('a', User.MaxDisplayNameLength + 1)).Error!.Code);
        Assert.Equal("user.display_name_unchanged", user.ChangeDisplayName(" Person ").Error!.Code);
        Assert.True(user.ChangeDisplayName("  Neue Person ").IsSuccess);
        Assert.Equal("Neue Person", user.DisplayName);
    }

    // ---------------------------------------------------------------------------------------------- organizations

    [Fact]
    public void Status_changes_are_recorded_with_reason_and_admin()
    {
        var owner = Guid.NewGuid();
        var org = Organization.Register(Profile(), owner, Now);
        Assert.Equal("organization.invalid_status", org.Reactivate(null, Admin, Now).Error!.Code);
        Assert.Equal("reason.required", org.Reject(" ", Admin, Now).Error!.Code);
        Assert.True(org.Verify(Admin, Now).IsSuccess);
        Assert.Equal("organization.invalid_status", org.Reject("x", Admin, Now).Error!.Code);
        Assert.True(org.Suspend(" Zahlungsverzug ", Admin, Now.AddDays(1)).IsSuccess);
        Assert.Equal("Zahlungsverzug", org.StatusReason);
        Assert.True(org.Reactivate("bezahlt", Admin, Now.AddDays(2)).IsSuccess);
        Assert.Equal(OrganizationStatus.Verified, org.Status);
        Assert.Null(org.StatusReason);
        Assert.Equal(Now, org.VerifiedAt); // reactivation keeps the first verification

        var history = org.StatusChanges.Select(c => (c.FromStatus, c.ToStatus, c.Reason, c.ChangedBy)).ToList();
        Assert.Equal(
        [
            (null, OrganizationStatus.PendingVerification, null, owner),
            (OrganizationStatus.PendingVerification, OrganizationStatus.Verified, null, Admin),
            (OrganizationStatus.Verified, OrganizationStatus.Suspended, "Zahlungsverzug", Admin),
            (OrganizationStatus.Suspended, OrganizationStatus.Verified, "bezahlt", Admin),
        ], history);
        // Only the first verification sends the "verified" email.
        Assert.Single(org.DomainEvents.OfType<OrganizationVerified>());
    }

    [Fact]
    public void Master_data_update_normalizes_and_detects_no_change()
    {
        var org = Organization.Register(Profile(), Guid.NewGuid(), Now);
        Assert.Equal("organization.unchanged", org.UpdateProfile(Profile() with { CountryCode = "DE", VatId = "de 123456789" }).Error!.Code);
        Assert.Equal("organization.capability_required", org.UpdateProfile(Profile() with { IsRetailer = false }).Error!.Code);

        var updated = Profile("Neu") with { ContactEmail = " Info@Neu.DE ", Website = " https://neu.de ", TaxNumber = " 21/815/08150 ", ContactPhone = "" };
        Assert.True(org.UpdateProfile(updated).IsSuccess);
        Assert.Equal("Neu", org.Name);
        Assert.Equal("info@neu.de", org.ContactEmail);
        Assert.Equal("https://neu.de", org.Website);
        Assert.Equal("21/815/08150", org.TaxNumber);
        Assert.Null(org.ContactPhone);
        Assert.Equal("DE", org.CountryCode);
    }

    [Theory]
    [InlineData("21/815/08150", true)]
    [InlineData("2181508150", true)]
    [InlineData("93815 08152", true)]
    [InlineData("1121081508150", true)]
    [InlineData("12/34", false)]
    [InlineData("12345678901234", false)]
    [InlineData("/2181508150", false)]
    [InlineData("21-815-08150", false)]
    public void Tax_number_format(string value, bool valid) => Assert.Equal(valid, OrganizationContactRules.IsTaxNumber(value));

    [Theory]
    [InlineData("https://www.example.de", true)]
    [InlineData("http://shop.example.de/kontakt", true)]
    [InlineData("www.example.de", false)]
    [InlineData("ftp://example.de", false)]
    [InlineData("https://localhost", false)]
    [InlineData("https://exa mple.de", false)]
    public void Website_format(string value, bool valid) => Assert.Equal(valid, OrganizationContactRules.IsWebsite(value));

    // ---------------------------------------------------------------------------------------------- memberships

    private static (List<Membership> Members, Membership Owner, Membership Staff) Team()
    {
        var org = Guid.NewGuid();
        var owner = Membership.Create(org, Guid.NewGuid(), MembershipRole.Owner, Now);
        var staff = Membership.Create(org, Guid.NewGuid(), MembershipRole.Staff, Now);
        staff.SetPosRights([Permissions.PosRefund], 10);
        return ([owner, staff], owner, staff);
    }

    [Fact]
    public void Last_owner_cannot_be_demoted_or_removed()
    {
        var (members, owner, staff) = Team();
        Assert.Equal(MembershipRules.LastOwner, MembershipRules.ChangeRole(members, owner.UserId, MembershipRole.Manager).Error);
        Assert.Equal(MembershipRules.LastOwner, MembershipRules.CanRemove(members, owner.UserId).Error);
        Assert.True(MembershipRules.CanRemove(members, staff.UserId).IsSuccess);
        Assert.Equal("membership.not_found", MembershipRules.CanRemove(members, Guid.NewGuid()).Error!.Code);

        // With a second owner, one of them may step down.
        Assert.True(MembershipRules.ChangeRole(members, staff.UserId, MembershipRole.Owner).IsSuccess);
        Assert.True(MembershipRules.ChangeRole(members, owner.UserId, MembershipRole.Manager).IsSuccess);
        Assert.Equal("membership.role_unchanged", MembershipRules.ChangeRole(members, owner.UserId, MembershipRole.Manager).Error!.Code);
    }

    [Fact]
    public void Leaving_staff_drops_the_pos_configuration()
    {
        var (members, _, staff) = Team();
        Assert.NotNull(staff.PosPermissions);
        Assert.True(MembershipRules.ChangeRole(members, staff.UserId, MembershipRole.Manager).IsSuccess);
        Assert.Null(staff.PosPermissions);
        Assert.Null(staff.PosMaxDiscountPercent);
        Assert.Equal("membership.role_unknown", staff.ChangeRole((MembershipRole)42).Error!.Code);
    }

    [Fact]
    public void Transfer_makes_the_member_the_only_owner()
    {
        var (members, owner, staff) = Team();
        Assert.Equal("membership.not_found", MembershipRules.TransferOwnership(members, Guid.NewGuid()).Error!.Code);
        Assert.Equal("membership.already_owner", MembershipRules.TransferOwnership(members, owner.UserId).Error!.Code);
        Assert.True(MembershipRules.TransferOwnership(members, staff.UserId).IsSuccess);
        Assert.Equal(MembershipRole.Owner, staff.Role);
        Assert.Equal(MembershipRole.Manager, owner.Role);
        Assert.Single(members, m => m.Role == MembershipRole.Owner);
    }
}
