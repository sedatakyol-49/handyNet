using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Notifications.Application;
using HandyNet.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>Refresh tokens (rotation, reuse detection, expiry, logout), password reset and email verification.</summary>
[Collection(HandyNetCollection.Name)]
public sealed partial class AuthTokenTests(HandyNetFixture fixture)
{
    private const string Password = "Sicher-Passwort-123";
    private const string NewPassword = "Neues-Passwort-456";

    private sealed record Account(ApiClient Client, string Email, JsonNode Login);

    [GeneratedRegex(@"#token=([A-Za-z0-9_\-%]+)")]
    private static partial Regex TokenInLink();

    private async Task<Account> RegisterAsync(string? language = null)
    {
        var email = $"auth-{Scenario.UniqueSuffix()}@test.de";
        var client = fixture.CreateClient();
        (await client.PostAsync("/api/registration", new
        {
            email,
            displayName = "Auth Tester",
            password = Password,
            language,
            organization = new
            {
                name = $"Auth {email}",
                legalName = $"Auth {email} GmbH",
                street = "Hauptstraße 1",
                postalCode = "10115",
                city = "Berlin",
                isWholesaler = false,
                isRetailer = true,
                isRepairProvider = false,
            },
        })).Expect(HttpStatusCode.Created);
        var login = (await client.PostAsync("/api/auth/login", new { email, password = Password })).Expect(HttpStatusCode.OK).Json;
        await client.LoginAsync(email, Password);
        return new Account(client, email, login);
    }

    private Task<ApiResponse> RefreshAsync(string refreshToken) =>
        fixture.CreateClient().PostAsync("/api/auth/refresh", new { refreshToken });

    private static string Refresh(JsonNode tokens) => tokens["refreshToken"]!.GetValue<string>();

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Drives outbox + email workers until an email matching the filter was delivered; returns the token from its link.</summary>
    private async Task<(string Subject, string Body, string Token)> AwaitEmailAsync(string recipient, Func<string, bool> subject, string? notToken = null)
    {
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        var worker = fixture.Factory.Services.GetRequiredService<EmailDeliveryWorker>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var match = fixture.Emails.Sent
                .Where(e => e.To == recipient && subject(e.Subject))
                .Select(e => (e.Subject, e.Body, Token: Uri.UnescapeDataString(TokenInLink().Match(e.Body).Groups[1].Value)))
                .LastOrDefault(e => e.Token != notToken);
            if (match.Subject is not null)
            {
                Assert.False(string.IsNullOrEmpty(match.Token), $"No token link in: {match.Body}");
                return match;
            }
            Assert.True(DateTime.UtcNow < deadline, $"No matching email to {recipient} within 30 s.");
            await outbox.DispatchBatchAsync(CancellationToken.None);
            await worker.DeliverBatchAsync(CancellationToken.None);
            await Task.Delay(100);
        }
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<string?> TextAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var command = new NpgsqlCommand(sql, owner);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (string?)await command.ExecuteScalarAsync();
    }

    private Task<long> ExecuteAsync(string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync($"WITH changed AS ({sql} RETURNING 1) SELECT count(*) FROM changed", parameters);

    // ---------- refresh tokens ----------

    [Fact]
    public async Task Login_returns_a_refresh_token_that_is_stored_only_as_hash()
    {
        var account = await RegisterAsync();
        var refresh = Refresh(account.Login);
        var refreshExpires = account.Login["refreshExpiresAt"]!.GetValue<DateTimeOffset>();

        Assert.Equal(43, refresh.Length);
        Assert.InRange(refreshExpires, DateTimeOffset.UtcNow.AddDays(13.9), DateTimeOffset.UtcNow.AddDays(14.1));
        Assert.False(account.Login["emailVerified"]!.GetValue<bool>());
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM identity.refresh_tokens WHERE token_hash = @h", ("h", Sha256(refresh))));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM identity.refresh_tokens WHERE token_hash = @t", ("t", refresh)));
    }

    [Fact]
    public async Task Refresh_rotates_and_reusing_a_rotated_token_revokes_the_whole_family()
    {
        var account = await RegisterAsync();
        var first = Refresh(account.Login);

        var rotated = (await RefreshAsync(first)).Expect(HttpStatusCode.OK).Json;
        var second = Refresh(rotated);
        Assert.NotEqual(first, second);
        Assert.Equal(account.Login["userId"]!.GetValue<string>(), rotated["userId"]!.GetValue<string>());

        // The new access token works.
        var me = await fixture.CreateClient().SendAsync(HttpMethod.Get, "/api/me", headers: new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {rotated["accessToken"]!.GetValue<string>()}",
        });
        Assert.Equal(account.Email, me.Expect(HttpStatusCode.OK).Json["email"]!.GetValue<string>());

        var third = Refresh((await RefreshAsync(second)).Expect(HttpStatusCode.OK).Json);

        // An attacker replays the first (already rotated) token …
        var replay = (await RefreshAsync(first)).Expect(HttpStatusCode.Unauthorized);
        Assert.Equal("auth.refresh_token_reused", replay.Code);

        // … which ends the session for everyone: the newest token of the family is dead too.
        Assert.Equal("auth.refresh_token_invalid", (await RefreshAsync(third)).Expect(HttpStatusCode.Unauthorized).Code);
        Assert.Equal(0, await ScalarAsync(
            "SELECT count(*) FROM identity.refresh_tokens WHERE family_id = (SELECT family_id FROM identity.refresh_tokens WHERE token_hash = @h) AND revoked_at IS NULL",
            ("h", Sha256(first))));
        Assert.Equal("Reuse", await TextAsync("SELECT revocation_reason FROM identity.refresh_tokens WHERE token_hash = @h", ("h", Sha256(third))));
    }

    [Fact]
    public async Task Reuse_detection_does_not_end_other_sessions_of_the_user()
    {
        var account = await RegisterAsync();
        var otherDevice = Refresh((await fixture.CreateClient().PostAsync("/api/auth/login", new { email = account.Email, password = Password }))
            .Expect(HttpStatusCode.OK).Json);
        var first = Refresh(account.Login);
        (await RefreshAsync(first)).Expect(HttpStatusCode.OK);
        (await RefreshAsync(first)).Expect(HttpStatusCode.Unauthorized);

        (await RefreshAsync(otherDevice)).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var account = await RegisterAsync();
        var refresh = Refresh(account.Login);
        Assert.Equal(1, await ExecuteAsync(
            "UPDATE identity.refresh_tokens SET created_at = now() - interval '15 days', expires_at = now() - interval '1 minute' WHERE token_hash = @h",
            ("h", Sha256(refresh))));

        var response = (await RefreshAsync(refresh)).Expect(HttpStatusCode.Unauthorized);
        Assert.Equal("auth.refresh_token_expired", response.Code);
    }

    [Fact]
    public async Task Unknown_or_malformed_refresh_tokens_are_rejected()
    {
        Assert.Equal("auth.refresh_token_invalid", (await RefreshAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))).Expect(HttpStatusCode.Unauthorized).Code);
        Assert.Equal("validation.failed", (await RefreshAsync("")).Expect(HttpStatusCode.UnprocessableEntity).Code);
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_is_idempotent()
    {
        var account = await RegisterAsync();
        var refresh = Refresh(account.Login);
        var anonymous = fixture.CreateClient();

        (await anonymous.PostAsync("/api/auth/logout", new { refreshToken = refresh })).Expect(HttpStatusCode.NoContent);
        Assert.Equal("auth.refresh_token_invalid", (await RefreshAsync(refresh)).Expect(HttpStatusCode.Unauthorized).Code);
        Assert.Equal("Logout", await TextAsync("SELECT revocation_reason FROM identity.refresh_tokens WHERE token_hash = @h", ("h", Sha256(refresh))));

        (await anonymous.PostAsync("/api/auth/logout", new { refreshToken = refresh })).Expect(HttpStatusCode.NoContent);
        (await anonymous.PostAsync("/api/auth/logout", new { refreshToken = "unknown-token" })).Expect(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_everywhere_revokes_every_session()
    {
        var account = await RegisterAsync();
        var second = Refresh((await fixture.CreateClient().PostAsync("/api/auth/login", new { email = account.Email, password = Password }))
            .Expect(HttpStatusCode.OK).Json);

        (await account.Client.PostAsync("/api/auth/logout-all")).Expect(HttpStatusCode.NoContent);

        (await RefreshAsync(Refresh(account.Login))).Expect(HttpStatusCode.Unauthorized);
        (await RefreshAsync(second)).Expect(HttpStatusCode.Unauthorized);
        (await fixture.CreateClient().PostAsync("/api/auth/logout-all")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_the_password_revokes_all_refresh_tokens_and_starts_a_new_session()
    {
        var account = await RegisterAsync();
        var other = Refresh((await fixture.CreateClient().PostAsync("/api/auth/login", new { email = account.Email, password = Password }))
            .Expect(HttpStatusCode.OK).Json);

        var wrong = (await account.Client.PostAsync("/api/auth/change-password", new { currentPassword = "falsch-123456", newPassword = NewPassword }))
            .Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("auth.current_password_invalid", wrong.Code);
        Assert.Equal("password.weak", (await account.Client.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "kurz" }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);
        other = Refresh((await RefreshAsync(other)).Expect(HttpStatusCode.OK).Json); // failed attempts revoke nothing

        var changed = (await account.Client.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = NewPassword }))
            .Expect(HttpStatusCode.OK).Json;

        (await RefreshAsync(Refresh(account.Login))).Expect(HttpStatusCode.Unauthorized);
        Assert.Equal("auth.refresh_token_invalid", (await RefreshAsync(other)).Expect(HttpStatusCode.Unauthorized).Code);
        Assert.Equal(0, await ScalarAsync(
            "SELECT count(*) FROM identity.refresh_tokens t JOIN identity.users u ON u.id = t.user_id WHERE u.email = @e AND t.revoked_at IS NULL AND t.used_at IS NULL AND t.token_hash <> @h",
            ("e", account.Email), ("h", Sha256(Refresh(changed)))));
        (await RefreshAsync(Refresh(changed))).Expect(HttpStatusCode.OK);
        (await fixture.CreateClient().PostAsync("/api/auth/login", new { email = account.Email, password = NewPassword })).Expect(HttpStatusCode.OK);
    }

    // ---------- password reset ----------

    [Fact]
    public async Task Password_reset_happy_path_sets_the_password_lifts_lockout_and_revokes_sessions()
    {
        var account = await RegisterAsync();
        var anonymous = fixture.CreateClient();

        // Lock the account with wrong passwords.
        for (var i = 0; i < 5; i++)
            await anonymous.PostAsync("/api/auth/login", new { email = account.Email, password = "falsches-Passwort-1" });
        Assert.Equal("auth.locked_out", (await anonymous.PostAsync("/api/auth/login", new { email = account.Email, password = Password }))
            .Expect(HttpStatusCode.Forbidden).Code);

        (await anonymous.PostAsync("/api/auth/password-reset/request", new { email = account.Email.ToUpperInvariant() })).Expect(HttpStatusCode.Accepted);
        var email = await AwaitEmailAsync(account.Email, s => s == "HandyNet-Passwort zurücksetzen");
        Assert.Contains("/passwort-zuruecksetzen#token=", email.Body);
        Assert.Contains("1 Stunde gültig", email.Body);

        // Only the hash is stored; the queued email body is redacted once delivered; the outbox never had the token.
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM identity.account_tokens WHERE token_hash = @h AND purpose = 'PasswordReset'", ("h", Sha256(email.Token))));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM notifications.email_messages WHERE body LIKE '%' || @t || '%'", ("t", email.Token)));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM infra.outbox_messages WHERE payload::text LIKE '%' || @t || '%'", ("t", email.Token)));

        (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = email.Token, newPassword = NewPassword })).Expect(HttpStatusCode.NoContent);

        Assert.Equal("auth.invalid_credentials", (await anonymous.PostAsync("/api/auth/login", new { email = account.Email, password = Password }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);
        var login = (await anonymous.PostAsync("/api/auth/login", new { email = account.Email, password = NewPassword })).Expect(HttpStatusCode.OK).Json;
        Assert.True(login["emailVerified"]!.GetValue<bool>()); // the link proved control of the mailbox
        Assert.Equal("auth.refresh_token_invalid", (await RefreshAsync(Refresh(account.Login))).Expect(HttpStatusCode.Unauthorized).Code);
        Assert.Equal("PasswordReset", await TextAsync("SELECT revocation_reason FROM identity.refresh_tokens WHERE token_hash = @h", ("h", Sha256(Refresh(account.Login)))));

        // Single use.
        var reused = (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = email.Token, newPassword = "Noch-ein-Passwort-789" }))
            .Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("auth.token_invalid", reused.Code);
    }

    [Fact]
    public async Task Password_reset_rejects_expired_unknown_and_superseded_tokens()
    {
        var account = await RegisterAsync();
        var anonymous = fixture.CreateClient();
        (await anonymous.PostAsync("/api/auth/password-reset/request", new { email = account.Email })).Expect(HttpStatusCode.Accepted);
        var first = await AwaitEmailAsync(account.Email, s => s.Contains("Passwort"));

        // A weak password does not burn the link.
        Assert.Equal("password.weak", (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = first.Token, newPassword = "kurz" }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);

        Assert.Equal(1, await ExecuteAsync(
            "UPDATE identity.account_tokens SET created_at = now() - interval '2 hours', expires_at = now() - interval '1 minute' WHERE token_hash = @h",
            ("h", Sha256(first.Token))));
        Assert.Equal("auth.token_expired", (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = first.Token, newPassword = NewPassword }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);

        Assert.Equal("auth.token_invalid", (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = "nicht-existent", newPassword = NewPassword }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);

        // A newer link supersedes the older one (past the per-user cooldown).
        Assert.Equal(1, await ExecuteAsync("UPDATE identity.users SET password_reset_requested_at = now() - interval '2 minutes' WHERE email = @e", ("e", account.Email)));
        Assert.Equal(1, await ExecuteAsync(
            "UPDATE identity.account_tokens SET expires_at = now() + interval '1 hour' WHERE token_hash = @h", ("h", Sha256(first.Token))));
        (await anonymous.PostAsync("/api/auth/password-reset/request", new { email = account.Email })).Expect(HttpStatusCode.Accepted);
        var second = await AwaitEmailAsync(account.Email, s => s.Contains("Passwort"), notToken: first.Token);
        Assert.Equal("auth.token_invalid", (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = first.Token, newPassword = NewPassword }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);
        (await anonymous.PostAsync("/api/auth/password-reset/confirm", new { token = second.Token, newPassword = NewPassword })).Expect(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Password_reset_request_does_not_reveal_whether_an_account_exists()
    {
        var account = await RegisterAsync();
        var anonymous = fixture.CreateClient();
        var unknownEmail = $"niemand-{Scenario.UniqueSuffix()}@test.de";

        var known = await anonymous.PostAsync("/api/auth/password-reset/request", new { email = account.Email, language = "en" });
        var unknown = await anonymous.PostAsync("/api/auth/password-reset/request", new { email = unknownEmail });
        var throttled = await anonymous.PostAsync("/api/auth/password-reset/request", new { email = account.Email }); // within cooldown

        Assert.All([known, unknown, throttled], r => Assert.Equal(HttpStatusCode.Accepted, r.Status));
        Assert.Null(known.Body);
        Assert.Null(unknown.Body);
        Assert.Null(throttled.Body);

        var english = await AwaitEmailAsync(account.Email, s => s == "Reset your HandyNet password");
        Assert.Contains("valid for 1 hour", english.Body);

        // Let the workers settle, then: one email for the known account (cooldown held), none for the unknown address.
        var outbox = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 3; i++)
            await outbox.DispatchBatchAsync(CancellationToken.None);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM notifications.email_messages WHERE recipient = @r AND template = 'auth.password_reset'", ("r", account.Email)));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM notifications.email_messages WHERE recipient = @r", ("r", unknownEmail)));

        Assert.Equal("validation.failed", (await anonymous.PostAsync("/api/auth/password-reset/request", new { email = "kein-email" }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);
    }

    [Fact]
    public async Task Email_endpoints_are_rate_limited_per_client()
    {
        await using var limited = fixture.Factory.WithWebHostBuilder(web => web.UseSetting("RateLimits:AccountEmailsPerHour", "2"));
        var client = new ApiClient(limited.CreateClient());
        var email = $"niemand-{Scenario.UniqueSuffix()}@test.de";

        (await client.PostAsync("/api/auth/password-reset/request", new { email })).Expect(HttpStatusCode.Accepted);
        (await client.PostAsync("/api/auth/password-reset/request", new { email })).Expect(HttpStatusCode.Accepted);
        (await client.PostAsync("/api/auth/password-reset/request", new { email })).Expect(HttpStatusCode.TooManyRequests);
    }

    // ---------- email verification ----------

    [Fact]
    public async Task Registration_sends_a_verification_link_that_verifies_the_address_once()
    {
        var account = await RegisterAsync();
        Assert.False((await account.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json["emailVerified"]!.GetValue<bool>());

        var email = await AwaitEmailAsync(account.Email, s => s == "Bitte bestätigen Sie Ihre E-Mail-Adresse");
        Assert.Contains("/email-bestaetigen#token=", email.Body);

        // Unverified users can use the app; a resend right after registration is throttled.
        Assert.Equal("auth.verification_recently_sent", (await account.Client.PostAsync("/api/auth/verify-email/resend")).Expect(HttpStatusCode.Conflict).Code);

        var anonymous = fixture.CreateClient();
        (await anonymous.PostAsync("/api/auth/verify-email", new { token = email.Token })).Expect(HttpStatusCode.NoContent);

        Assert.True((await account.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json["emailVerified"]!.GetValue<bool>());
        Assert.True((await anonymous.PostAsync("/api/auth/login", new { email = account.Email, password = Password }))
            .Expect(HttpStatusCode.OK).Json["emailVerified"]!.GetValue<bool>());
        Assert.Equal("auth.token_invalid", (await anonymous.PostAsync("/api/auth/verify-email", new { token = email.Token })).Expect(HttpStatusCode.UnprocessableEntity).Code);
        Assert.Equal("auth.email_already_verified", (await account.Client.PostAsync("/api/auth/verify-email/resend")).Expect(HttpStatusCode.Conflict).Code);
    }

    [Fact]
    public async Task Verification_can_be_resent_in_english_and_expired_links_are_rejected()
    {
        var account = await RegisterAsync(language: "en");
        var first = await AwaitEmailAsync(account.Email, s => s == "Please confirm your email address");
        Assert.Contains("valid for 48 hours", first.Body);

        Assert.Equal(1, await ExecuteAsync(
            "UPDATE identity.account_tokens SET created_at = now() - interval '3 days', expires_at = now() - interval '1 minute' WHERE token_hash = @h",
            ("h", Sha256(first.Token))));
        Assert.Equal("auth.token_expired", (await account.Client.PostAsync("/api/auth/verify-email", new { token = first.Token }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);

        Assert.Equal(1, await ExecuteAsync("UPDATE identity.users SET verification_email_requested_at = now() - interval '2 minutes' WHERE email = @e", ("e", account.Email)));
        Assert.Equal("validation.failed", (await account.Client.PostAsync("/api/auth/verify-email/resend", new { language = "fr" }))
            .Expect(HttpStatusCode.UnprocessableEntity).Code);
        (await account.Client.PostAsync("/api/auth/verify-email/resend", new { language = "de" })).Expect(HttpStatusCode.Accepted);
        var second = await AwaitEmailAsync(account.Email, s => s == "Bitte bestätigen Sie Ihre E-Mail-Adresse");

        (await account.Client.PostAsync("/api/auth/verify-email", new { token = second.Token })).Expect(HttpStatusCode.NoContent);
        Assert.True((await account.Client.GetAsync("/api/me")).Json["emailVerified"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.CreateClient().PostAsync("/api/auth/verify-email/resend")).Status);
    }

    [Fact]
    public async Task App_role_cannot_delete_tokens()
    {
        var account = await RegisterAsync();
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        foreach (var table in new[] { "identity.refresh_tokens", "identity.account_tokens" })
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE user_id = '{account.Login["userId"]!.GetValue<string>()}'", app);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // permission denied
        }
    }
}
