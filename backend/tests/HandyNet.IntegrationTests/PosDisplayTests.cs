using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HandyNet.IntegrationTests.Infrastructure;

namespace HandyNet.IntegrationTests;

/// <summary>
/// Customer display (ADR-0023) on real PostgreSQL: the register's remote link (hash only, one active, revocable), the state
/// kept in memory, and the anonymous long-poll endpoint — which shows nothing but the state and the shop's name and logo and
/// answers the same 404 for unknown, malformed, expired and revoked tokens.
/// </summary>
[Collection(HandyNetCollection.Name)]
public sealed class PosDisplayTests(HandyNetFixture fixture)
{
    private readonly PosSupport _pos = new(fixture);

    private static object Cart(string description = "Ladekabel USB-C", string phase = "cart") => new
    {
        phase,
        lines = new[] { new { description, quantity = 2, unitPriceMinor = 10_00, discountMinor = 1_00, lineGrossMinor = 19_00 } },
        totalMinor = 19_00,
        discountTotalMinor = 1_00,
        currency = "EUR",
        locale = "de",
    };

    private async Task<(ApiClient Shop, Guid Register, string Token)> ShopWithLinkAsync()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var link = (await shop.PostAsync($"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.Created).Json;
        return (shop, register, link["token"]!.GetValue<string>());
    }

    private static string WithoutTraceId(ApiResponse response)
    {
        var body = response.Json.DeepClone().AsObject();
        body.Remove("traceId");
        return $"{(int)response.Status} {body.ToJsonString()}";
    }

    [Fact]
    public async Task Link_is_created_shown_replaced_and_revoked_and_only_its_hash_is_stored()
    {
        var shop = await _pos.ShopAsync();
        var register = await PosSupport.RegisterAsync(shop);
        var url = $"/api/pos/registers/{register}/display-link";
        (await shop.GetAsync(url)).Expect(HttpStatusCode.NoContent);

        var link = (await shop.PostAsync(url)).Expect(HttpStatusCode.Created).Json;
        var token = link["token"]!.GetValue<string>();
        Assert.Equal(43, token.Length); // 256 bit, base64url
        Assert.Equal($"/kundendisplay/{token}", link["path"]!.GetValue<string>());
        var expires = link["expiresAt"]!.GetValue<DateTimeOffset>();
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));

        var status = (await shop.GetAsync(url)).Expect(HttpStatusCode.OK).Json;
        Assert.True(status["active"]!.GetValue<bool>());
        Assert.Null(status["token"]); // the token is returned once, on creation
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.display_links WHERE token_hash = '{token}'"));
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>(
            $"SELECT count(*) FROM pos.display_links WHERE register_id = '{register}' AND token_hash = encode(sha256('{token}'::bytea), 'hex')"));
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM infra.audit_log WHERE changes::text LIKE '%{token}%'"));

        var anonymous = fixture.CreateClient();
        (await anonymous.GetAsync($"/api/public/pos-display/{token}")).Expect(HttpStatusCode.OK);

        // A new link revokes the earlier one at once.
        var second = (await shop.PostAsync(url)).Expect(HttpStatusCode.Created).Json["token"]!.GetValue<string>();
        Assert.NotEqual(token, second);
        (await anonymous.GetAsync($"/api/public/pos-display/{token}")).Expect(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/public/pos-display/{second}")).Expect(HttpStatusCode.OK);
        Assert.Equal(1L, await _pos.OwnerScalarAsync<long>($"SELECT count(*) FROM pos.display_links WHERE register_id = '{register}' AND revoked_at IS NULL"));

        (await PosSupport.DeleteAsync(shop, url)).Expect(HttpStatusCode.NoContent);
        (await shop.GetAsync(url)).Expect(HttpStatusCode.NoContent);
        (await anonymous.GetAsync($"/api/public/pos-display/{second}")).Expect(HttpStatusCode.NotFound);
        (await PosSupport.DeleteAsync(shop, url)).Expect(HttpStatusCode.NoContent); // nothing left to revoke

        // The database: a revoked link never comes back, rows are never deleted, a register never has two active links.
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"UPDATE pos.display_links SET revoked_at = NULL WHERE register_id = '{register}'"))?.SqlState);
        Assert.Equal("42501", (await _pos.OwnerTryAsync($"DELETE FROM pos.display_links WHERE register_id = '{register}'"))?.SqlState);
        (await shop.PostAsync(url)).Expect(HttpStatusCode.Created);
        Assert.Equal("23505", (await _pos.OwnerTryAsync(
            $"INSERT INTO pos.display_links (id, organization_id, register_id, token_hash, created_at, expires_at, created_by) "
            + $"VALUES (gen_random_uuid(), '{shop.OrganizationId}', '{register}', repeat('b', 64), now(), now() + interval '1 day', gen_random_uuid())"))?.SqlState);
    }

    [Fact]
    public async Task Public_display_shows_only_the_state_and_the_shop_and_long_polls_for_changes()
    {
        var (shop, register, token) = await ShopWithLinkAsync();
        var anonymous = fixture.CreateClient();
        var url = $"/api/public/pos-display/{token}";

        // Before the till pushed anything: idle.
        var idle = (await anonymous.GetAsync(url)).Expect(HttpStatusCode.OK).Json;
        var idleVersion = idle["version"]!.GetValue<long>();
        Assert.Equal("idle", idle["state"]!["phase"]!.GetValue<string>());
        Assert.Empty(idle["state"]!["lines"]!.AsArray());
        Assert.Equal(["shop", "state", "version"], idle.AsObject().Select(p => p.Key).Order());
        Assert.Equal(["logoUrl", "name"], idle["shop"]!.AsObject().Select(p => p.Key).Order());
        Assert.StartsWith("kassenshop", idle["shop"]!["name"]!.GetValue<string>());

        // Nothing changed: the long poll ends with 204 (the fixture waits 1 s instead of 20 s).
        var started = DateTimeOffset.UtcNow;
        (await anonymous.GetAsync($"{url}?since={idleVersion}")).Expect(HttpStatusCode.NoContent);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(900));

        // The till pushes a cart; unknown fields (e.g. a customer block) are dropped, not stored.
        var pushed = (await shop.PutAsync($"/api/pos/registers/{register}/display-state", new
        {
            phase = "cart",
            lines = new[] { new { description = "Ladekabel USB-C", quantity = 2, unitPriceMinor = 10_00, discountMinor = 1_00, lineGrossMinor = 19_00, productId = Guid.NewGuid() } },
            totalMinor = 19_00, discountTotalMinor = 1_00, currency = "EUR", locale = "de",
            customer = new { name = "Erika Mustermann", email = "erika@example.com" },
            loyalty = new { points = 120, redeemValueMinor = 1_20 },
        })).Expect(HttpStatusCode.OK).Json;
        var version = pushed["version"]!.GetValue<long>();
        Assert.True(version > idleVersion);

        var shown = (await anonymous.GetAsync($"{url}?since={idleVersion}")).Expect(HttpStatusCode.OK);
        Assert.Equal(version, shown.Json["version"]!.GetValue<long>());
        var state = shown.Json["state"]!;
        Assert.Equal(("cart", 19_00L, 1_00L, "EUR", "de"), (state["phase"]!.GetValue<string>(), state["totalMinor"]!.GetValue<long>(),
            state["discountTotalMinor"]!.GetValue<long>(), state["currency"]!.GetValue<string>(), state["locale"]!.GetValue<string>()));
        Assert.Equal(["description", "discountMinor", "lineGrossMinor", "quantity", "unitPriceMinor"], state["lines"]![0]!.AsObject().Select(p => p.Key).Order());
        Assert.Equal(120, state["loyalty"]!["points"]!.GetValue<int>());
        var raw = shown.Json.ToJsonString();
        Assert.DoesNotContain("Erika", raw);
        Assert.DoesNotContain("erika@example.com", raw);
        Assert.DoesNotContain(register.ToString(), raw);
        Assert.DoesNotContain(shop.OrganizationId!.Value.ToString(), raw);

        // A waiting display wakes up as soon as the till pushes the payment.
        var waiting = anonymous.GetAsync($"{url}?since={version}");
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);
        (await shop.PutAsync($"/api/pos/registers/{register}/display-state", new
        {
            phase = "payment", lines = Array.Empty<object>(), totalMinor = 19_00, discountTotalMinor = 1_00,
            payment = new { dueMinor = 19_00, tenderedMinor = 20_00, changeMinor = 1_00, method = "Cash" }, currency = "EUR",
        })).Expect(HttpStatusCode.OK);
        var woken = (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(("payment", 1_00L), (woken["state"]!["phase"]!.GetValue<string>(), woken["state"]!["payment"]!["changeMinor"]!.GetValue<long>()));
        Assert.True(woken["version"]!.GetValue<long>() > version);

        // Nothing of it reached the database or the audit log.
        Assert.Equal(0L, await _pos.OwnerScalarAsync<long>(
            $"SELECT count(*) FROM infra.audit_log WHERE organization_id = '{shop.OrganizationId}' AND (changes::text LIKE '%Ladekabel USB-C%' OR entity_type ILIKE '%DisplayState%')"));

        // Another register of the same shop has its own state.
        var other = await PosSupport.RegisterAsync(shop, "Kasse 2");
        var otherToken = (await shop.PostAsync($"/api/pos/registers/{other}/display-link")).Expect(HttpStatusCode.Created).Json["token"]!.GetValue<string>();
        Assert.Equal("idle", (await anonymous.GetAsync($"/api/public/pos-display/{otherToken}")).Expect(HttpStatusCode.OK).Json["state"]!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_malformed_expired_and_revoked_tokens_get_the_same_404()
    {
        var (shop, register, token) = await ShopWithLinkAsync();
        (await shop.PutAsync($"/api/pos/registers/{register}/display-state", Cart())).Expect(HttpStatusCode.OK);
        var anonymous = fixture.CreateClient();

        var unknown = await anonymous.GetAsync($"/api/public/pos-display/{new string('A', 43)}");
        var malformed = await anonymous.GetAsync("/api/public/pos-display/not-a-token");
        var sqlish = await anonymous.GetAsync("/api/public/pos-display/x'%20OR%20'1'='1");

        // Expired: the link's lifetime is moved into the past (as the owner, with the guard trigger off for this statement).
        var expiredRegister = await PosSupport.RegisterAsync(shop, "Kasse alt");
        var expiredToken = (await shop.PostAsync($"/api/pos/registers/{expiredRegister}/display-link")).Expect(HttpStatusCode.Created).Json["token"]!.GetValue<string>();
        (await anonymous.GetAsync($"/api/public/pos-display/{expiredToken}")).Expect(HttpStatusCode.OK);
        Assert.Null(await _pos.OwnerTryAsync($"""
            ALTER TABLE pos.display_links DISABLE TRIGGER display_links_guard;
            UPDATE pos.display_links SET created_at = now() - interval '2 days', expires_at = now() - interval '1 minute' WHERE register_id = '{expiredRegister}';
            ALTER TABLE pos.display_links ENABLE TRIGGER display_links_guard;
            """));
        var expired = await anonymous.GetAsync($"/api/public/pos-display/{expiredToken}");
        (await shop.GetAsync($"/api/pos/registers/{expiredRegister}/display-link")).Expect(HttpStatusCode.NoContent);

        (await PosSupport.DeleteAsync(shop, $"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.NoContent);
        var revoked = await anonymous.GetAsync($"/api/public/pos-display/{token}");
        var revokedPolling = await anonymous.GetAsync($"/api/public/pos-display/{token}?since=0");

        var expected = WithoutTraceId(unknown);
        Assert.StartsWith("404 ", expected);
        Assert.Contains("pos.display_link_not_found", expected);
        Assert.All(new[] { malformed, sqlish, expired, revoked, revokedPolling }, r => Assert.Equal(expected, WithoutTraceId(r)));
        Assert.DoesNotContain("Ladekabel", expected);
    }

    [Fact]
    public async Task A_display_waiting_on_a_link_that_is_revoked_gets_the_404_not_the_state()
    {
        var (shop, register, token) = await ShopWithLinkAsync();
        var version = (await shop.PutAsync($"/api/pos/registers/{register}/display-state", Cart())).Expect(HttpStatusCode.OK).Json["version"]!.GetValue<long>();
        var anonymous = fixture.CreateClient();

        var waiting = anonymous.GetAsync($"/api/public/pos-display/{token}?since={version}");
        await Task.Delay(150);
        (await PosSupport.DeleteAsync(shop, $"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.NoContent);
        var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((HttpStatusCode.NotFound, "pos.display_link_not_found"), (answer.Status, answer.Code));

        // A new link starts with an idle display: the previous cart is gone.
        var fresh = (await shop.PostAsync($"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.Created).Json["token"]!.GetValue<string>();
        Assert.Equal("idle", (await anonymous.GetAsync($"/api/public/pos-display/{fresh}")).Expect(HttpStatusCode.OK).Json["state"]!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task State_is_validated_limited_to_16_kib_and_needs_the_till()
    {
        var (shop, register, _) = await ShopWithLinkAsync();
        var url = $"/api/pos/registers/{register}/display-state";

        var badPhase = await shop.PutAsync(url, Cart(phase: "checkout"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.display_state_invalid", "phase"), (badPhase.Status, badPhase.Code, badPhase.Json["field"]!.GetValue<string>()));
        var script = await shop.PutAsync(url, new { phase = "done", totalMinor = 0, discountTotalMinor = 0, receiptUrl = "javascript:alert(1)" });
        Assert.Equal(("pos.display_state_invalid", "receiptUrl"), (script.Code, script.Json["field"]!.GetValue<string>()));
        var notJson = await shop.SendAsync(HttpMethod.Put, url, content: new StringContent("{ nope", Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.display_state_invalid"), (notJson.Status, notJson.Code));

        // Just under the limit is accepted, just over is refused.
        string Body(int messageLength) => new JsonObject
        {
            ["phase"] = "idle", ["totalMinor"] = 0, ["discountTotalMinor"] = 0, ["message"] = "ok",
            ["padding"] = new string('x', messageLength),
        }.ToJsonString();
        var fits = Body(16 * 1024 - Body(0).Length);
        Assert.Equal(16 * 1024, Encoding.UTF8.GetByteCount(fits));
        (await shop.SendAsync(HttpMethod.Put, url, content: new StringContent(fits, Encoding.UTF8, "application/json"))).Expect(HttpStatusCode.OK);
        var tooLarge = await shop.SendAsync(HttpMethod.Put, url, content: new StringContent(Body(16 * 1024), Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "pos.display_state_too_large"), (tooLarge.Status, tooLarge.Code));

        // Staff with pos.sell may push and manage the link; without a login or for another tenant's register nothing works.
        var staff = await _pos.MemberAsync(shop, "Staff");
        (await staff.PutAsync(url, Cart())).Expect(HttpStatusCode.OK);
        (await staff.PostAsync($"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.Created);
        var anonymous = fixture.CreateClient();
        (await anonymous.PutAsync(url, Cart())).Expect(HttpStatusCode.Unauthorized);
        var stranger = await _pos.ShopAsync("fremdshop");
        (await stranger.PutAsync(url, Cart())).Expect(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.NotFound);
        (await PosSupport.DeleteAsync(stranger, $"/api/pos/registers/{register}/display-link")).Expect(HttpStatusCode.NotFound);
    }
}
