using System.Net;
using HandyNet.IntegrationTests.Infrastructure;
using HandyNet.Modules.Ankauf.Application;
using HandyNet.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HandyNet.IntegrationTests;

/// <summary>ADR-0015: seller capture (MRZ, returning sellers, QR self check-in), signature binding, till payout and reports.</summary>
public sealed partial class AnkaufTests
{
    // German specimen passport "Erika Mustermann" (valid until 28.02.2027).
    private const string PassportMrz = "P<D<<MUSTERMANN<<ERIKA<<<<<<<<<<<<<<<<<<<<<<\nC01X00T478D<<6408125F2702283<<<<<<<<<<<<<<<4";

    private static object PassportSeller(string lastName = "Mustermann", string expiry = "2027-02-28") => new
    {
        firstName = "Erika", lastName, street = "Heidestraße 17", postalCode = "51147", city = "Köln", birthDate = "1964-08-12",
        idDocumentType = "Passport", idDocumentNumber = "C01X00T47", idExpiryDate = expiry, nationality = "D", idIssuingCountry = "D",
        idIssuingAuthority = "Stadt Köln",
    };

    private static string Id(ApiResponse response) => response.Json["id"]!.GetValue<string>();

    private async Task<NpgsqlConnection> AppConnectionAsync(Guid? organizationId)
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_org', @org, false)", connection);
        set.Parameters.AddWithValue("org", organizationId?.ToString() ?? string.Empty);
        await set.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task<string> QuoteAsync(ApiClient dealer, string key) =>
        Id((await dealer.PostAsync("/api/ankauf/quotes", new { modelKey = key, condition = "Gut" })).Expect(HttpStatusCode.Created));

    private async Task<string> InspectedIntakeAsync(ApiClient dealer, string key, object? seller = null)
    {
        var intakeId = Id((await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = seller ?? AdultSeller }))
            .Expect(HttpStatusCode.Created));
        (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/inspect", new { condition = "Gut" })).Expect(HttpStatusCode.OK);
        return intakeId;
    }

    [Fact]
    public async Task Mrz_capture_is_verified_and_returning_sellers_are_found_only_in_the_own_shop()
    {
        var (dealer, key, _, _) = await PricedModelAsync();

        // MRZ with a wrong check digit, or data that differs from the MRZ, is refused.
        var badDigit = PassportMrz[..^1] + "5";
        Assert.Equal("ankauf.mrz_check_digit",
            (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = PassportSeller(), mrz = badDigit })).Code);
        Assert.Equal("ankauf.mrz_mismatch",
            (await dealer.PostAsync("/api/ankauf/intakes",
                new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = PassportSeller(expiry: "2027-03-01"), mrz = PassportMrz })).Code);
        // Expired documents are refused with a clear code.
        Assert.Equal("ankauf.id_expired",
            (await dealer.PostAsync("/api/ankauf/intakes",
                new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = PassportSeller(expiry: "2020-01-31") })).Code);

        var lastName = "Mustermann" + Scenario.UniqueSuffix();
        var seller = new
        {
            firstName = "Erika", lastName, street = "Heidestraße 17", postalCode = "51147", city = "Köln", birthDate = "1964-08-12",
            idDocumentType = "Passport", idDocumentNumber = "C01X00T47", idExpiryDate = "2027-02-28", nationality = "D",
        };
        var intake = (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller, mrz = PassportMrz }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Mrz", intake["captureMethod"]!.GetValue<string>());
        var card = intake["seller"]!;
        Assert.Equal("*****0T47", card["idDocumentNumberMasked"]!.GetValue<string>());
        Assert.Equal("2027-02-28", card["idExpiryDate"]!.GetValue<string>());
        Assert.Equal("D", card["nationality"]!.GetValue<string>());
        Assert.DoesNotContain("C01X00T47", intake.ToJsonString());

        // Returning seller: found by name in the own shop (without ID data); invisible to another shop.
        await InspectedIntakeAsync(dealer, key, seller);
        var found = (await dealer.GetAsync($"/api/ankauf/sellers?search={lastName[..12]}")).Expect(HttpStatusCode.OK).Json.AsArray();
        var hit = found.Single(s => s!["lastName"]!.GetValue<string>() == lastName)!;
        Assert.Equal(2, hit["intakeCount"]!.GetValue<int>());
        Assert.Equal("1964-08-12", hit["birthDate"]!.GetValue<string>());
        Assert.Null(hit["idDocumentNumber"]);
        Assert.Empty((await dealer.GetAsync("/api/ankauf/sellers?search=a")).Json.AsArray()); // at least 2 characters

        var otherShop = await DealerAsync();
        Assert.Empty((await otherShop.GetAsync($"/api/ankauf/sellers?search={lastName}")).Expect(HttpStatusCode.OK).Json.AsArray());
    }

    [Fact]
    public async Task The_signature_is_bound_to_data_and_price_and_a_change_requires_a_new_one()
    {
        var (dealer, key, _, _) = await PricedModelAsync();
        var intakeId = await InspectedIntakeAsync(dealer, key);
        var png = Convert.ToBase64String(SignaturePng);

        Assert.Equal("ankauf.signature_required",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 200m, paymentMethod = "BankTransfer" })).Code);
        Assert.Equal("ankauf.signature_image",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/signature", new { paidAmount = 200m, paymentMethod = "BankTransfer", image = "bm90IGEgcG5n" })).Code);
        Assert.Equal("ankauf.paid_amount",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/signature", new { paidAmount = 200.005m, paymentMethod = "BankTransfer", image = png })).Code);

        var signed = (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/signature",
            new { paidAmount = 200m, paymentMethod = "BankTransfer", image = "data:image/png;base64," + png })).Expect(HttpStatusCode.OK).Json;
        Assert.True(signed["signature"]!["valid"]!.GetValue<bool>());
        Assert.Equal(20000, signed["signature"]!["paidMinor"]!.GetValue<long>());
        Assert.Equal(64, signed["signature"]!["bindingHash"]!.GetValue<string>().Length);

        // Other amount or method than signed → refused.
        Assert.Equal("ankauf.signature_invalid",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 190m, paymentMethod = "BankTransfer" })).Code);
        Assert.Equal("ankauf.signature_invalid",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 200m, paymentMethod = "Cash" })).Code);

        // Re-inspection changes the signed data → the signature is no longer valid.
        var reinspected = (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/inspect", new { condition = "Gut", batteryHealthPercent = 85 }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.False(reinspected["signature"]!["valid"]!.GetValue<bool>());
        Assert.Equal("ankauf.signature_invalid",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 200m, paymentMethod = "BankTransfer" })).Code);

        var done = (await PayAsync(dealer, intakeId, 200m, "BankTransfer")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Completed", done["status"]!.GetValue<string>());
        Assert.True(done["signature"]!["valid"]!.GetValue<bool>());
        Assert.Null(done["cashMovementId"]);
        var signatureId = done["signature"]!["id"]!.GetValue<string>();

        var (status, media, _, image) = await dealer.GetFileAsync($"/api/ankauf/intakes/{intakeId}/signatures/{signatureId}/image");
        Assert.Equal((HttpStatusCode.OK, "image/png"), (status, media));
        Assert.Equal(SignaturePng, image);
        var otherShop = await DealerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await otherShop.GetFileAsync($"/api/ankauf/intakes/{intakeId}/signatures/{signatureId}/image")).Status);

        var belegPdf = (await dealer.GetFileAsync($"/api/ankauf/intakes/{intakeId}/beleg")).Body;
        var text = PdfText.All(belegPdf);
        Assert.Contains("Elektronische Unterschrift", text);
        // The signature image is drawn above the seller's signature line (PNG converted to JPEG; no logo set in this shop).
        Assert.Equal((1, 1), (PdfText.ImageCount(belegPdf), PdfText.ImageDraws(belegPdf)));
        Assert.Contains(done["signature"]!["bindingHash"]!.GetValue<string>()[..16].ToUpperInvariant(), text);

        // The database keeps signatures append-only (no edit, no delete, other tenants see nothing).
        await using var app = await AppConnectionAsync(dealer.OrganizationId);
        var edit = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync(app, $"UPDATE ankauf.intake_signatures SET agreed_paid_minor = 1 WHERE id = '{signatureId}'"));
        Assert.Equal("42501", edit.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(app, $"DELETE FROM ankauf.intake_signatures WHERE id = '{signatureId}'"));
        Assert.Equal("42501", delete.SqlState);
        await using var other = await AppConnectionAsync(otherShop.OrganizationId);
        Assert.Equal(0L, await ScalarAsync(other, $"SELECT count(*) FROM ankauf.intake_signatures WHERE intake_id = '{intakeId}'"));
        await using var none = await AppConnectionAsync(null);
        Assert.Equal(0L, await ScalarAsync(none, "SELECT count(*) FROM ankauf.intake_signatures"));
    }

    [Fact]
    public async Task A_cash_purchase_is_taken_from_an_open_till_and_shows_in_the_z_report_and_the_daily_report()
    {
        var (dealer, key, _, _) = await PricedModelAsync();
        (await (await _scenario.AdminAsync()).PutAsync($"/api/admin/organizations/{dealer.OrganizationId}/modules", new { modules = new[] { "Ankauf", "Inventory", "Pos" } }))
            .Expect(HttpStatusCode.OK);
        var register = Id((await dealer.PostAsync("/api/pos/registers", new { name = "Kasse A" })).Expect(HttpStatusCode.Created));
        var closedRegister = Id((await dealer.PostAsync("/api/pos/registers", new { name = "Kasse B" })).Expect(HttpStatusCode.Created));
        var sessionId = Id((await dealer.PostAsync($"/api/pos/registers/{register}/sessions", new { openingCashMinor = 250_00 })).Expect(HttpStatusCode.Created));

        var tills = (await dealer.GetAsync("/api/ankauf/payout-registers")).Expect(HttpStatusCode.OK).Json;
        Assert.True(tills["tillRequired"]!.GetValue<bool>());
        var tillA = tills["registers"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == register)!;
        Assert.Equal(250_00, tillA["expectedCashMinor"]!.GetValue<long>());

        var intakeId = await InspectedIntakeAsync(dealer, key);
        (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/signature",
            new { paidAmount = 220m, paymentMethod = "Cash", image = Convert.ToBase64String(SignaturePng) })).Expect(HttpStatusCode.OK);

        Assert.Equal("ankauf.register_required",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash" })).Code);
        Assert.Equal("pos.no_open_session",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash", registerId = closedRegister })).Code);
        Assert.Equal("pos.register_not_found",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash", registerId = Guid.NewGuid() })).Code);
        // Nothing was stocked by the refused attempts (one transaction).
        Assert.Equal("Inspected", (await dealer.GetAsync($"/api/ankauf/intakes/{intakeId}")).Json["status"]!.GetValue<string>());

        // Not enough cash in the drawer: take some out first, then the payout is refused.
        (await dealer.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Withdrawal", amountMinor = 100_00, reasonCode = "BankDeposit" }))
            .Expect(HttpStatusCode.Created);
        Assert.Equal("pos.insufficient_cash",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash", registerId = register })).Code);
        (await dealer.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Deposit", amountMinor = 100_00, reasonCode = "ChangeFund" }))
            .Expect(HttpStatusCode.Created);

        var done = (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash", registerId = register }))
            .Expect(HttpStatusCode.OK).Json;
        Assert.NotNull(done["cashMovementId"]);
        Assert.Equal(register, done["cashRegisterId"]!.GetValue<string>());
        // Completing again is refused; no second payout.
        Assert.Equal("ankauf.not_inspected",
            (await dealer.PostAsync($"/api/ankauf/intakes/{intakeId}/complete", new { paidAmount = 220m, paymentMethod = "Cash", registerId = register })).Code);

        // The "Ankauf" reason is reserved for these payouts.
        Assert.Equal("pos.reason_code_reserved",
            (await dealer.PostAsync($"/api/pos/sessions/{sessionId}/cash-movements", new { kind = "Withdrawal", amountMinor = 1_00, reasonCode = "Ankauf" })).Code);
        Assert.DoesNotContain((await dealer.GetAsync("/api/pos/cash-movement-reasons")).Json.AsArray(), r => r!["code"]!.GetValue<string>() == "Ankauf");

        // A bank transfer purchase does not touch the till.
        var transferId = await InspectedIntakeAsync(dealer, key);
        (await PayAsync(dealer, transferId, 150m, "BankTransfer")).Expect(HttpStatusCode.OK);

        var session = (await dealer.GetAsync($"/api/pos/sessions/{sessionId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, session["summary"]!["ankaufCount"]!.GetValue<int>());
        Assert.Equal(220_00, session["summary"]!["ankaufPayoutsMinor"]!.GetValue<long>());
        Assert.Equal(250_00 - 100_00 + 100_00 - 220_00, session["expectedCashMinor"]!.GetValue<long>());
        var payout = session["movements"]!.AsArray().Single(m => m!["reasonCode"]?.GetValue<string>() == "Ankauf")!;
        Assert.Equal("Withdrawal", payout["kind"]!.GetValue<string>());
        Assert.StartsWith("Ankauf ", payout["reason"]!.GetValue<string>());

        // Z-report: the closed session keeps the Ankauf figures.
        var closed = (await dealer.PostAsync($"/api/pos/sessions/{sessionId}/close", new { countedCashMinor = 30_00 })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(220_00, closed["summary"]!["ankaufPayoutsMinor"]!.GetValue<long>());
        Assert.Equal(0, closed["differenceMinor"]!.GetValue<long>());

        // Tagesbericht: purchases by payment method, the devices, and the till payout.
        var report = (await dealer.GetAsync("/api/reports/daily")).Expect(HttpStatusCode.OK).Json;
        var ankauf = report["ankauf"]!;
        Assert.Equal(2, ankauf["completedCount"]!.GetValue<int>());
        Assert.Equal(370_00, ankauf["paidTotalMinor"]!.GetValue<long>());
        var byMethod = ankauf["byPaymentMethod"]!.AsArray().ToDictionary(m => m!["method"]!.GetValue<string>(), m => m!["amountMinor"]!.GetValue<long>());
        Assert.Equal((220_00L, 150_00L), (byMethod["Cash"], byMethod["BankTransfer"]));
        Assert.Equal(2, ankauf["devices"]!.AsArray().Count);
        Assert.Contains(ankauf["devices"]!.AsArray(), d => d!["paidFromTill"]!.GetValue<bool>());
        Assert.Equal(220_00, report["pos"]!["ankaufPayoutsMinor"]!.GetValue<long>());
        Assert.Equal(370_00, report["totals"]!["boughtMinor"]!.GetValue<long>());
        Assert.Null(report["purchases"]); // module B2BPurchasing not enabled
        Assert.Equal("reports.date_in_future", (await dealer.GetAsync($"/api/reports/daily?date={DateTime.UtcNow.AddDays(3):yyyy-MM-dd}")).Code);

        // Another shop's report contains none of it.
        var otherShop = await DealerAsync();
        Assert.Equal(0, (await otherShop.GetAsync("/api/reports/daily")).Expect(HttpStatusCode.OK).Json["ankauf"]!["completedCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Self_check_in_is_single_use_short_lived_and_only_visible_to_its_shop()
    {
        var (dealer, key, _, _) = await PricedModelAsync();
        var created = (await dealer.PostAsync("/api/ankauf/checkins")).Expect(HttpStatusCode.Created).Json;
        var checkInId = created["id"]!.GetValue<string>();
        var token = created["token"]!.GetValue<string>();
        Assert.Equal(43, token.Length);
        Assert.Equal("/checkin/" + token, created["path"]!.GetValue<string>());
        Assert.InRange(DateTimeOffset.Parse(created["expiresAt"]!.GetValue<string>()) - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.1));

        var phone = fixture.CreateClient(); // anonymous
        var info = (await phone.GetAsync($"/api/public/ankauf/checkins/{token}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Open", info["status"]!.GetValue<string>());
        Assert.StartsWith("ankaufshop ", info["shopName"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync($"/api/public/ankauf/checkins/{new string('a', 43)}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/api/public/ankauf/checkins/kurz")).Status);

        var lastName = "Check" + Scenario.UniqueSuffix();
        object Seller(string birth) => new
        {
            firstName = "Ayşe", lastName, street = "Lindenweg 3", postalCode = "50667", city = "Köln", birthDate = birth,
            idDocumentType = "IdCard", idDocumentNumber = "T22000129", idExpiryDate = "2031-10-31", nationality = "D",
        };
        Assert.Equal("ankauf.seller_minor",
            (await phone.PostAsync($"/api/public/ankauf/checkins/{token}", new { seller = Seller(DateTime.UtcNow.AddYears(-17).ToString("yyyy-MM-dd")), consent = true })).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await phone.PostAsync($"/api/public/ankauf/checkins/{token}", new { seller = Seller("1990-03-04"), consent = false })).Status);

        // Before submission the shop sees no data and cannot use it.
        Assert.Equal("Open", (await dealer.GetAsync($"/api/ankauf/checkins/{checkInId}")).Json["status"]!.GetValue<string>());
        Assert.Equal("ankauf.checkin_not_submitted",
            (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = AdultSeller, checkInId })).Code);

        (await phone.PostAsync($"/api/public/ankauf/checkins/{token}",
            new { seller = Seller("1990-03-04"), consent = true, signature = Convert.ToBase64String(SignaturePng) })).Expect(HttpStatusCode.NoContent);
        Assert.Equal("ankauf.checkin_already_submitted",
            (await phone.PostAsync($"/api/public/ankauf/checkins/{token}", new { seller = Seller("1991-03-04"), consent = true })).Code);

        var submitted = (await dealer.GetAsync($"/api/ankauf/checkins/{checkInId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Submitted", submitted["status"]!.GetValue<string>());
        Assert.Equal(lastName, submitted["seller"]!["lastName"]!.GetValue<string>());
        Assert.True(submitted["hasSignature"]!.GetValue<bool>());
        Assert.Equal(SignaturePng, (await dealer.GetFileAsync($"/api/ankauf/checkins/{checkInId}/signature")).Body);

        // Another shop can neither read nor use it.
        var otherShop = await DealerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await otherShop.GetAsync($"/api/ankauf/checkins/{checkInId}")).Status);
        Assert.Equal("ankauf.checkin_not_found",
            (await otherShop.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(otherShop, key), imei = NewImei(), seller = AdultSeller, checkInId })).Code);
        await using (var asOther = await AppConnectionAsync(otherShop.OrganizationId))
            Assert.Equal(0L, await ScalarAsync(asOther, $"SELECT count(*) FROM ankauf.checkins WHERE id = '{checkInId}'"));
        await using (var noTenant = await AppConnectionAsync(null))
            Assert.Equal(0L, await ScalarAsync(noTenant, "SELECT count(*) FROM ankauf.checkins"));

        // The employee verifies the data against the ID and takes it into the intake (corrections allowed).
        var intake = (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = Seller("1990-03-04"), checkInId }))
            .Expect(HttpStatusCode.Created).Json;
        Assert.Equal("SelfCheckIn", intake["captureMethod"]!.GetValue<string>());
        Assert.Equal("SelfCheckIn", intake["checkInSignature"]!["source"]!.GetValue<string>());
        Assert.Null(intake["signature"]); // the purchase itself still needs a signature at the counter

        // Single use: consumed, its personal data removed, the public link is gone.
        var consumed = (await dealer.GetAsync($"/api/ankauf/checkins/{checkInId}")).Json;
        Assert.Equal("Consumed", consumed["status"]!.GetValue<string>());
        Assert.Null(consumed["seller"]);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync($"/api/public/ankauf/checkins/{token}")).Status);
        Assert.Equal("ankauf.checkin_used",
            (await dealer.PostAsync("/api/ankauf/intakes", new { quoteId = await QuoteAsync(dealer, key), imei = NewImei(), seller = Seller("1990-03-04"), checkInId })).Code);
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        Assert.Equal(true, await ScalarAsync(owner,
            $"SELECT seller IS NULL AND signature_image IS NULL AND purged_at IS NOT NULL AND intake_id IS NOT NULL FROM ankauf.checkins WHERE id = '{checkInId}'"));

        // Expired links accept nothing.
        var late = (await dealer.PostAsync("/api/ankauf/checkins")).Expect(HttpStatusCode.Created).Json;
        var lateToken = late["token"]!.GetValue<string>();
        await ScalarAsync(owner, $"""
            SET session_replication_role = replica;
            UPDATE ankauf.checkins SET created_at = created_at - interval '1 hour', expires_at = expires_at - interval '1 hour' WHERE id = '{late["id"]}';
            SET session_replication_role = origin;
            """);
        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync($"/api/public/ankauf/checkins/{lateToken}")).Status);
        Assert.Equal(HttpStatusCode.NotFound,
            (await phone.PostAsync($"/api/public/ankauf/checkins/{lateToken}", new { seller = Seller("1990-03-04"), consent = true })).Status);

        // The database refuses a second submission and re-filling consumed data even for direct writes.
        await using var app = await AppConnectionAsync(dealer.OrganizationId);
        var refill = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync(app, $"UPDATE ankauf.checkins SET seller = '{{}}'::jsonb WHERE id = '{checkInId}'"));
        Assert.Equal("42501", refill.SqlState);
    }

    [Fact]
    public async Task The_public_check_in_form_is_rate_limited()
    {
        await using var limited = fixture.Factory.WithWebHostBuilder(web => web.UseSetting("RateLimits:AnkaufCheckInPerMinute", "3"));
        var client = new ApiClient(limited.CreateClient());
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.GetAsync($"/api/public/ankauf/checkins/{new string('b', 43)}")).Status);
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
    }

    [Fact]
    public async Task Retention_covers_the_new_seller_fields_signatures_and_unused_check_ins_and_the_audit_log_has_no_personal_data()
    {
        var (dealer, key, _, _) = await PricedModelAsync();
        var lastName = "Retention" + Scenario.UniqueSuffix();
        var intakeId = await InspectedIntakeAsync(dealer, key, new
        {
            firstName = "Erika", lastName, street = "Heidestraße 17", postalCode = "51147", city = "Köln", birthDate = "1964-08-12",
            idDocumentType = "Passport", idDocumentNumber = "C01X00T47", idExpiryDate = "2027-02-28", nationality = "TUR", idIssuingCountry = "D",
            idIssuingAuthority = "Stadt Köln", country = "AT",
        });
        (await PayAsync(dealer, intakeId, 100m, "BankTransfer")).Expect(HttpStatusCode.OK);

        // An unused check-in with submitted data.
        var checkIn = (await dealer.PostAsync("/api/ankauf/checkins")).Expect(HttpStatusCode.Created).Json;
        (await fixture.CreateClient().PostAsync($"/api/public/ankauf/checkins/{checkIn["token"]}", new
        {
            seller = new
            {
                firstName = "Ali", lastName, street = "A 1", postalCode = "10115", city = "Berlin", birthDate = "1980-01-01",
                idDocumentType = "IdCard", idDocumentNumber = "L01X00T47", idExpiryDate = "2030-01-01",
            },
            consent = true,
            signature = Convert.ToBase64String(SignaturePng),
        })).Expect(HttpStatusCode.NoContent);

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await ScalarAsync(owner, $"""
            SET session_replication_role = replica;
            UPDATE ankauf.intakes SET received_at = received_at - interval '11 years', completed_at = completed_at - interval '11 years' WHERE id = '{intakeId}';
            UPDATE ankauf.checkins SET created_at = created_at - interval '3 days', expires_at = expires_at - interval '3 days' WHERE id = '{checkIn["id"]}';
            SET session_replication_role = origin;
            """);

        var job = new SellerDataRetention(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AnkaufOptions { SellerDataRetentionYears = 10 }),
            fixture.Factory.Services.GetRequiredService<IHostEnvironment>(),
            fixture.Factory.Services.GetRequiredService<HandyNet.SharedKernel.IClock>(),
            NullLogger<SellerDataRetention>.Instance);
        Assert.True(await job.PurgeCheckInsAsync(CancellationToken.None) >= 1);
        Assert.True(await job.RunOnceAsync(CancellationToken.None) >= 1);

        // Seller: exactly the placeholder (nationality, expiry, authority, country gone); signature image removed.
        Assert.Equal("{\"city\": \"anonymisiert\", \"street\": \"anonymisiert\", \"lastName\": \"anonymisiert\", \"birthDate\": \"1900-01-01\", "
                     + "\"firstName\": \"anonymisiert\", \"postalCode\": \"00000\", \"idDocumentType\": 0, \"idDocumentNumber\": \"ANONYMISIERT\"}",
            await ScalarAsync(owner, $"SELECT seller::text FROM ankauf.intakes WHERE id = '{intakeId}'"));
        Assert.Equal(true, await ScalarAsync(owner,
            $"SELECT bool_and(image IS NULL AND anonymized_at IS NOT NULL) FROM ankauf.intake_signatures WHERE intake_id = '{intakeId}'"));
        Assert.Equal(true, await ScalarAsync(owner,
            $"SELECT seller IS NULL AND signature_image IS NULL AND purged_at IS NOT NULL FROM ankauf.checkins WHERE id = '{checkIn["id"]}'"));
        Assert.Equal("anonymisiert", (await dealer.GetAsync($"/api/ankauf/intakes/{intakeId}")).Json["sellerName"]!.GetValue<string>());
        Assert.Null((await dealer.GetAsync($"/api/ankauf/intakes/{intakeId}")).Json["seller"]);
        var anonymizedPdf = (await dealer.GetFileAsync($"/api/ankauf/intakes/{intakeId}/beleg")).Body;
        Assert.Contains("gelöscht", PdfText.All(anonymizedPdf));
        Assert.Equal(0, PdfText.ImageCount(anonymizedPdf)); // the signature image is gone with the retention period

        // Audit log: none of the personal data or the signature image was ever written there.
        Assert.Equal(0L, await ScalarAsync(owner, $"""
            SELECT count(*) FROM infra.audit_log
            WHERE changes::text LIKE '%{lastName}%' OR changes::text LIKE '%C01X00T47%' OR changes::text LIKE '%Stadt Köln%' OR changes::text LIKE '%iVBORw0KGgo%'
            """));
        Assert.True((long)(await ScalarAsync(owner,
            $"SELECT count(*) FROM infra.audit_log WHERE entity_type = 'IntakeSignature' AND changes::text LIKE '%[personal data]%'"))! >= 1);
    }
}
