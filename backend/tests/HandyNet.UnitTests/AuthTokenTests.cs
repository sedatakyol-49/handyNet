using HandyNet.Modules.Identity.Contracts;
using HandyNet.Modules.Identity.Domain;
using HandyNet.Modules.Notifications.Application;
using HandyNet.Modules.Notifications.Domain;

namespace HandyNet.UnitTests;

public class OpaqueTokenTests
{
    [Fact]
    public void Values_are_random_url_safe_and_only_the_hash_is_derived()
    {
        var a = OpaqueToken.NewValue();
        var b = OpaqueToken.NewValue();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes, base64url without padding
        Assert.Matches("^[A-Za-z0-9_-]+$", a);

        var hash = OpaqueToken.Hash(a);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, OpaqueToken.Hash(a));
        Assert.NotEqual(hash, OpaqueToken.Hash(b));
        Assert.DoesNotContain(a, hash);
    }
}

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    [Fact]
    public void A_new_session_stores_only_the_hash()
    {
        var userId = Guid.NewGuid();
        var (token, value) = RefreshToken.StartFamily(userId, Now, Lifetime);
        Assert.Equal(OpaqueToken.Hash(value), token.TokenHash);
        Assert.Equal(userId, token.UserId);
        Assert.Equal(Now + Lifetime, token.ExpiresAt);
        Assert.True(token.IsActive(Now));
    }

    [Fact]
    public void Rotation_consumes_the_token_and_issues_a_successor_in_the_same_family()
    {
        var (token, value) = RefreshToken.StartFamily(Guid.NewGuid(), Now, Lifetime);
        var later = Now.AddDays(3);

        var rotated = token.Rotate(later, Lifetime);

        Assert.True(rotated.IsSuccess);
        var (next, nextValue) = rotated.Value;
        Assert.NotEqual(value, nextValue);
        Assert.Equal(token.FamilyId, next.FamilyId);
        Assert.Equal(token.UserId, next.UserId);
        Assert.Equal(later + Lifetime, next.ExpiresAt); // sliding lifetime
        Assert.Equal(later, token.UsedAt);
        Assert.Equal(next.Id, token.ReplacedById);
        Assert.False(token.IsActive(later));
        Assert.True(next.IsActive(later));
    }

    [Fact]
    public void Using_a_rotated_token_again_is_reported_as_reuse()
    {
        var (token, _) = RefreshToken.StartFamily(Guid.NewGuid(), Now, Lifetime);
        Assert.True(token.Rotate(Now, Lifetime).IsSuccess);

        var again = token.Rotate(Now.AddMinutes(1), Lifetime);

        Assert.Equal(TokenErrors.RefreshReused, again.Error);
    }

    [Fact]
    public void Expired_token_cannot_be_rotated()
    {
        var (token, _) = RefreshToken.StartFamily(Guid.NewGuid(), Now, Lifetime);
        Assert.Equal(TokenErrors.RefreshExpired, token.Rotate(Now + Lifetime, Lifetime).Error);
        Assert.Null(token.UsedAt);
        Assert.True(token.Rotate(Now + Lifetime - TimeSpan.FromSeconds(1), Lifetime).IsSuccess);
    }

    [Fact]
    public void Revoked_token_is_invalid_and_the_first_revocation_reason_is_kept()
    {
        var (token, _) = RefreshToken.StartFamily(Guid.NewGuid(), Now, Lifetime);
        token.Revoke(Now, RefreshTokenRevocation.Logout);
        token.Revoke(Now.AddMinutes(1), RefreshTokenRevocation.Reuse);

        Assert.Equal(RefreshTokenRevocation.Logout, token.RevocationReason);
        Assert.Equal(Now, token.RevokedAt);
        Assert.Equal(TokenErrors.RefreshInvalid, token.Rotate(Now, Lifetime).Error);
    }

    [Fact]
    public void Refresh_errors_are_401()
    {
        Assert.All([TokenErrors.RefreshInvalid, TokenErrors.RefreshExpired, TokenErrors.RefreshReused],
            e => Assert.Equal(HandyNet.SharedKernel.ErrorKind.Unauthorized, e.Kind));
    }
}

public class AccountTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Token_is_single_use()
    {
        var (token, value) = AccountToken.Issue(Guid.NewGuid(), AccountTokenPurpose.PasswordReset, Now, TimeSpan.FromHours(1));
        Assert.Equal(OpaqueToken.Hash(value), token.TokenHash);

        Assert.True(token.Use(Now.AddMinutes(5)).IsSuccess);
        Assert.Equal(TokenErrors.LinkInvalid, token.Use(Now.AddMinutes(6)).Error);
    }

    [Fact]
    public void Expired_token_is_rejected_with_its_own_code()
    {
        var (token, _) = AccountToken.Issue(Guid.NewGuid(), AccountTokenPurpose.PasswordReset, Now, TimeSpan.FromHours(1));
        Assert.Equal(TokenErrors.LinkExpired, token.Use(Now.AddHours(1)).Error);
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public void Superseded_token_is_invalid_and_revoking_a_used_token_changes_nothing()
    {
        var (revoked, _) = AccountToken.Issue(Guid.NewGuid(), AccountTokenPurpose.EmailVerification, Now, TimeSpan.FromHours(48));
        revoked.Revoke(Now);
        Assert.Equal(TokenErrors.LinkInvalid, revoked.Use(Now).Error);

        var (used, _) = AccountToken.Issue(Guid.NewGuid(), AccountTokenPurpose.EmailVerification, Now, TimeSpan.FromHours(48));
        Assert.True(used.Use(Now).IsSuccess);
        used.Revoke(Now);
        Assert.Null(used.RevokedAt);
    }

    [Fact]
    public void Lifetime_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountToken.Issue(Guid.NewGuid(), AccountTokenPurpose.PasswordReset, Now, TimeSpan.Zero));
}

public class UserAccountRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Verification_request_raises_an_event_without_a_token_and_respects_the_cooldown()
    {
        var user = User.Create("a@b.de", "A", Now);

        Assert.True(user.RequestEmailVerification(Now, "en").IsSuccess);
        var raised = Assert.IsType<EmailVerificationRequested>(Assert.Single(user.PopDomainEvents()));
        Assert.Equal(new EmailVerificationRequested(user.Id, "en"), raised);

        Assert.Equal("auth.verification_recently_sent", user.RequestEmailVerification(Now.AddSeconds(30), null).Error!.Code);
        Assert.Empty(user.PopDomainEvents());
        Assert.True(user.RequestEmailVerification(Now + User.AccountEmailCooldown, null).IsSuccess);
        Assert.Equal("de", Assert.IsType<EmailVerificationRequested>(Assert.Single(user.PopDomainEvents())).Language);
    }

    [Fact]
    public void Verified_users_get_no_further_verification_email()
    {
        var user = User.Create("a@b.de", "A", Now);
        user.MarkEmailVerified(Now);
        Assert.True(user.EmailVerified);
        Assert.Equal("auth.email_already_verified", user.RequestEmailVerification(Now, null).Error!.Code);
        Assert.Empty(user.PopDomainEvents());
    }

    [Fact]
    public void Password_reset_request_is_throttled_silently()
    {
        var user = User.Create("a@b.de", "A", Now);
        Assert.True(user.RequestPasswordReset(Now, "de"));
        Assert.False(user.RequestPasswordReset(Now.AddSeconds(59), "de"));
        Assert.Single(user.PopDomainEvents());
        Assert.True(user.RequestPasswordReset(Now.AddMinutes(1), "de"));
        Assert.IsType<PasswordResetRequested>(Assert.Single(user.PopDomainEvents()));
    }

    [Fact]
    public void Reset_sets_the_password_lifts_the_lockout_and_verifies_the_address()
    {
        var user = User.Create("a@b.de", "A", Now);
        user.SetPasswordHash("old");
        for (var i = 0; i < User.MaxFailedLogins; i++)
            user.RecordFailedLogin(Now);
        Assert.True(user.IsLockedOut(Now));

        user.ResetPassword("new", Now);

        Assert.Equal("new", user.PasswordHash);
        Assert.False(user.IsLockedOut(Now));
        Assert.Equal(0, user.FailedLoginCount);
        Assert.True(user.EmailVerified);
    }

    [Theory]
    [InlineData(null, "de")]
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("tr", "de")]
    public void Email_language_falls_back_to_german(string? input, string expected) =>
        Assert.Equal(expected, EmailLanguage.Normalize(input));
}

public class AccountEmailTests
{
    [Fact]
    public void Verification_email_in_german_and_english()
    {
        var de = EmailTemplates.EmailVerification("Anna", "https://app/x#token=abc", TimeSpan.FromHours(48), "de");
        Assert.Equal("Bitte bestätigen Sie Ihre E-Mail-Adresse", de.Subject);
        Assert.Contains("Guten Tag Anna", de.Body);
        Assert.Contains("https://app/x#token=abc", de.Body);
        Assert.Contains("48 Stunden gültig", de.Body);

        var en = EmailTemplates.EmailVerification("Anna", "https://app/x#token=abc", TimeSpan.FromHours(48), "en");
        Assert.Equal("Please confirm your email address", en.Subject);
        Assert.Contains("valid for 48 hours", en.Body);
        Assert.Contains("https://app/x#token=abc", en.Body);
    }

    [Fact]
    public void Reset_email_in_german_and_english()
    {
        var de = EmailTemplates.PasswordReset("Anna", "L", TimeSpan.FromHours(1), "de");
        Assert.Equal("HandyNet-Passwort zurücksetzen", de.Subject);
        Assert.Contains("1 Stunde gültig", de.Body);
        Assert.Contains("auf allen Geräten abgemeldet", de.Body);

        var en = EmailTemplates.PasswordReset("Anna", "L", TimeSpan.FromMinutes(30), "en");
        Assert.Equal("Reset your HandyNet password", en.Subject);
        Assert.Contains("valid for 30 minutes", en.Body);
    }

    [Fact]
    public void Link_carries_the_token_in_the_fragment()
    {
        var link = AccountLinks.Build("https://handynet.de/", AccountLinks.ResetPasswordPath, "a-b_c");
        Assert.Equal("https://handynet.de/passwort-zuruecksetzen#token=a-b_c", link);
    }

    [Fact]
    public void Account_email_bodies_are_redacted_after_delivery_others_are_kept()
    {
        var now = DateTimeOffset.UnixEpoch;
        var reset = EmailMessage.Queue(Guid.NewGuid(), EmailTemplates.PasswordResetTemplate, "a@b.de", null, new RenderedEmail("s", "secret link"), now);
        var order = EmailMessage.Queue(Guid.NewGuid(), "order.placed", "a@b.de", null, new RenderedEmail("s", "order body"), now);
        Assert.Equal("secret link", reset.Body); // kept until delivered

        reset.MarkSent(now);
        order.MarkSent(now);

        Assert.Equal(EmailMessage.RedactedBody, reset.Body);
        Assert.Equal("order body", order.Body);
    }

    [Fact]
    public void Account_email_body_is_redacted_when_delivery_gives_up()
    {
        var now = DateTimeOffset.UnixEpoch;
        var email = EmailMessage.Queue(Guid.NewGuid(), EmailTemplates.EmailVerificationTemplate, "a@b.de", null, new RenderedEmail("s", "secret link"), now);
        email.MarkAttemptFailed("smtp down", now);
        Assert.Equal("secret link", email.Body); // still needed for the retry
        for (var i = 1; i < EmailMessage.MaxAttempts; i++)
            email.MarkAttemptFailed("smtp down", now);
        Assert.Equal(EmailStatus.Failed, email.Status);
        Assert.Equal(EmailMessage.RedactedBody, email.Body);
    }
}
