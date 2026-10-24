using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Reporting.Application;
using HandyNet.Tests.Support;
using AnkaufPayment = HandyNet.Modules.Ankauf.Domain.PaymentMethod;

namespace HandyNet.UnitTests;

/// <summary>ICAO 9303 MRZ parsing with check digits, on official specimen documents.</summary>
public class MrzTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    // ICAO 9303 part 4, specimen passport (TD3).
    private const string IcaoPassport = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\nL898902C36UTO7408122F1204159ZE184226B<<<<<10";

    // ICAO 9303 part 5, specimen ID card (TD1).
    private const string IcaoIdCard = "I<UTOD231458907<<<<<<<<<<<<<<<\n7408122F1204159UTO<<<<<<<<<<<6\nERIKSSON<<ANNA<MARIA<<<<<<<<<<";

    // German specimen Personalausweis "Erika Mustermann" (Bundesdruckerei sample, TD1; no sex in the German MRZ).
    private const string GermanIdCard = "IDD<<T220001293<<<<<<<<<<<<<<<\n6408125<2010315D<<<<<<<<<<<<<4\nMUSTERMANN<<ERIKA<<<<<<<<<<<<<";

    // German specimen Reisepass "Erika Mustermann" (TD3).
    private const string GermanPassport = "P<D<<MUSTERMANN<<ERIKA<<<<<<<<<<<<<<<<<<<<<<\nC01X00T478D<<6408125F2702283<<<<<<<<<<<<<<<4";

    [Theory]
    [InlineData("L898902C3", 6)]
    [InlineData("740812", 2)]
    [InlineData("120415", 9)]
    [InlineData("T22000129", 3)]
    [InlineData("<<<<<<<<<", 0)]
    public void Check_digit_uses_weights_7_3_1(string value, int digit) => Assert.Equal(digit, Mrz.CheckDigit(value));

    [Fact]
    public void Icao_specimen_passport_is_parsed()
    {
        var mrz = Mrz.Parse(IcaoPassport, Today).Value;
        Assert.Equal(MrzFormat.Td3, mrz.Format);
        Assert.Equal(IdDocumentType.Passport, mrz.DocumentType);
        Assert.Equal("L898902C3", mrz.DocumentNumber);
        Assert.Equal(("ERIKSSON", "ANNA MARIA"), (mrz.Surname, mrz.GivenNames));
        Assert.Equal(("UTO", "UTO"), (mrz.IssuingState, mrz.Nationality));
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
        Assert.Equal("F", mrz.Sex);
    }

    [Fact]
    public void Icao_specimen_id_card_is_parsed()
    {
        var mrz = Mrz.Parse(IcaoIdCard, Today).Value;
        Assert.Equal(MrzFormat.Td1, mrz.Format);
        Assert.Equal(IdDocumentType.IdCard, mrz.DocumentType);
        Assert.Equal("D23145890", mrz.DocumentNumber);
        Assert.Equal(("ERIKSSON", "ANNA MARIA"), (mrz.Surname, mrz.GivenNames));
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
    }

    [Fact]
    public void German_id_card_specimen_is_parsed()
    {
        var mrz = Mrz.Parse(GermanIdCard, Today).Value;
        Assert.Equal(IdDocumentType.IdCard, mrz.DocumentType);
        Assert.Equal("ID", mrz.DocumentCode);
        Assert.Equal("T22000129", mrz.DocumentNumber);
        Assert.Equal(("D", "D"), (mrz.IssuingState, mrz.Nationality));
        Assert.Equal(("MUSTERMANN", "ERIKA"), (mrz.Surname, mrz.GivenNames));
        Assert.Equal(new DateOnly(1964, 8, 12), mrz.BirthDate);
        Assert.Equal(new DateOnly(2020, 10, 31), mrz.ExpiryDate);
        Assert.Null(mrz.Sex);
    }

    [Fact]
    public void German_passport_specimen_is_parsed()
    {
        var mrz = Mrz.Parse(GermanPassport, Today).Value;
        Assert.Equal(IdDocumentType.Passport, mrz.DocumentType);
        Assert.Equal("C01X00T47", mrz.DocumentNumber);
        Assert.Equal(new DateOnly(2027, 2, 28), mrz.ExpiryDate);
        Assert.Equal("D", mrz.Nationality);
    }

    [Fact]
    public void Keyboard_wedge_input_without_line_breaks_lower_case_and_spaces_is_accepted()
    {
        var typed = GermanIdCard.Replace("\n", "", StringComparison.Ordinal).ToLowerInvariant();
        Assert.Equal("T22000129", Mrz.Parse(typed, Today).Value.DocumentNumber);
        Assert.Equal("C01X00T47", Mrz.Parse("  " + GermanPassport.Replace("\n", "\r\n", StringComparison.Ordinal) + " \n", Today).Value.DocumentNumber);
        Assert.Equal("C01X00T47", Mrz.Parse(GermanPassport.Replace("\n", "", StringComparison.Ordinal), Today).Value.DocumentNumber);
    }

    /// <summary>Changing one character of a checked field (or its digit) must be detected — the core guarantee of the parser.</summary>
    [Theory]
    [InlineData(GermanIdCard, 0, 6, "documentNumber")]  // document number
    [InlineData(GermanIdCard, 0, 14, "documentNumber")] // its check digit
    [InlineData(GermanIdCard, 1, 1, "birthDate")]
    [InlineData(GermanIdCard, 1, 9, "expiryDate")]
    [InlineData(GermanIdCard, 1, 29, "composite")]
    [InlineData(GermanPassport, 1, 2, "documentNumber")]
    [InlineData(GermanPassport, 1, 15, "birthDate")]
    [InlineData(GermanPassport, 1, 24, "expiryDate")]
    [InlineData(GermanPassport, 1, 43, "composite")]
    [InlineData(IcaoPassport, 1, 30, "personalNumber")]
    public void A_wrong_check_digit_is_refused(string text, int line, int position, string field)
    {
        var lines = text.Split('\n');
        var chars = lines[line].ToCharArray();
        chars[position] = chars[position] switch { '9' => '8', >= '0' and <= '8' => (char)(chars[position] + 1), 'Z' => 'Y', _ => (char)(chars[position] + 1) };
        lines[line] = new string(chars);
        var error = Mrz.Parse(string.Join('\n', lines), Today).Error!;
        Assert.Equal("ankauf.mrz_check_digit", error.Code);
        Assert.Equal(field, error.Details!["field"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("IDD<<T220001293<<<<<<<<<<<<<<<")]
    [InlineData("hello world")]
    [InlineData("IDD<<T220001293<<<<<<<<<<<<<<<\n6408125<2010315D<<<<<<<<<<<<<4\nMUSTERMANN<<ERIKA<<<<<<<<<<<<")]
    [InlineData("IDD<<T220001293<<<<<<<<<<<<<<<\n6408125<2010315D<<<<<<<<<<<<<4\nMUSTERMANN<<ERIKA<<<<<<<<<<<<Ä")]
    public void Anything_but_a_complete_mrz_is_a_format_error(string text) =>
        Assert.Equal("ankauf.mrz_format", Mrz.Parse(text, Today).Error!.Code);

    [Theory]
    [InlineData("P<", IdDocumentType.Passport)]
    [InlineData("ID", IdDocumentType.IdCard)]
    [InlineData("I<", IdDocumentType.IdCard)]
    [InlineData("AR", IdDocumentType.ResidencePermit)]
    [InlineData("IR", IdDocumentType.ResidencePermit)]
    public void Document_codes_map_to_document_types(string code, IdDocumentType type) => Assert.Equal(type, Mrz.DocumentType(code));

    [Fact]
    public void Mrz_matches_the_seller_data_it_was_taken_from()
    {
        var mrz = Mrz.Parse(GermanPassport, Today).Value;
        var seller = new SellerDetails("Erika", "Mustermann", "Heidestr. 17", "51147", "Köln", new DateOnly(1964, 8, 12), IdDocumentType.Passport, "c01x00t47")
        {
            IdExpiryDate = new DateOnly(2027, 2, 28),
        };
        Assert.True(Mrz.Matches(mrz, seller));
        Assert.False(Mrz.Matches(mrz, seller with { BirthDate = new DateOnly(1964, 8, 13) }));
        Assert.False(Mrz.Matches(mrz, seller with { IdExpiryDate = new DateOnly(2027, 3, 1) }));
        Assert.False(Mrz.Matches(mrz, seller with { IdDocumentType = IdDocumentType.IdCard }));
    }
}

public class SellerRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    private static SellerDetails Seller(DateOnly birth, DateOnly? expiry) =>
        new("Erika", "Mustermann", "Heidestr. 17", "51147", "Köln", birth, IdDocumentType.IdCard, " t22-000 129 ") { IdExpiryDate = expiry };

    [Fact]
    public void Sellers_must_be_18_and_the_message_names_the_latest_birth_date()
    {
        var minor = SellerRules.Validate(Seller(new DateOnly(2008, 10, 23), new DateOnly(2030, 1, 1)), Today).Error!;
        Assert.Equal("ankauf.seller_minor", minor.Code);
        Assert.Equal("2008-10-22", minor.Details!["latestBirthDate"]);
        Assert.True(SellerRules.Validate(Seller(new DateOnly(2008, 10, 22), new DateOnly(2030, 1, 1)), Today).IsSuccess);
        // Born on 29 Feb: 18 only from 1 Mar in a non-leap year.
        Assert.Equal(17, SellerRules.AgeOn(new DateOnly(2008, 2, 29), new DateOnly(2026, 2, 28)));
        Assert.Equal(18, SellerRules.AgeOn(new DateOnly(2008, 2, 29), new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void An_expired_or_missing_id_document_is_refused()
    {
        Assert.Equal("ankauf.id_expired", SellerRules.Validate(Seller(new DateOnly(1990, 1, 1), Today.AddDays(-1)), Today).Error!.Code);
        Assert.True(SellerRules.Validate(Seller(new DateOnly(1990, 1, 1), Today), Today).IsSuccess); // valid through its last day
        Assert.Equal("ankauf.id_expiry_required", SellerRules.Validate(Seller(new DateOnly(1990, 1, 1), null), Today).Error!.Code);
    }

    [Fact]
    public void Data_is_normalized_and_codes_are_checked()
    {
        var ok = SellerRules.Validate(Seller(new DateOnly(1990, 1, 1), new DateOnly(2030, 1, 1)) with { Nationality = "d<<", Country = "de" }, Today).Value;
        Assert.Equal("T22000129", ok.IdDocumentNumber);
        Assert.Equal("D", ok.Nationality);
        Assert.Null(ok.Country); // Germany is the default
        Assert.Equal("ankauf.country_code",
            SellerRules.Validate(Seller(new DateOnly(1990, 1, 1), new DateOnly(2030, 1, 1)) with { Nationality = "D1" }, Today).Error!.Code);
        Assert.Equal("ankauf.id_number",
            SellerRules.Validate(Seller(new DateOnly(1990, 1, 1), new DateOnly(2030, 1, 1)) with { IdDocumentNumber = "AB/12" }, Today).Error!.Code);
        Assert.Equal("ankauf.seller_birth_date", SellerRules.Validate(Seller(Today.AddDays(1), new DateOnly(2030, 1, 1)), Today).Error!.Code);
    }

    [Fact]
    public void Anonymized_placeholder_keeps_its_stored_form()
    {
        // The database check ck_intakes_anonymized compares the exact JSON; new optional members must not appear in it.
        var json = System.Text.Json.JsonSerializer.Serialize(SellerDetails.Anonymized, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("{\"firstName\":\"anonymisiert\",\"lastName\":\"anonymisiert\",\"street\":\"anonymisiert\",\"postalCode\":\"00000\",\"city\":\"anonymisiert\","
                     + "\"birthDate\":\"1900-01-01\",\"idDocumentType\":0,\"idDocumentNumber\":\"ANONYMISIERT\"}", json);
    }
}

public class SignatureBindingTests
{
    private static readonly DateTimeOffset Now = IntakeRulesTests.Now;
    private static readonly byte[] Png = TestImages.Png(400, 150);

    private static (Intake Intake, Quote Quote) Inspected()
    {
        var quote = IntakeRulesTests.NewQuote();
        var seller = new SellerDetails("Max", "Mustermann", "Hauptstr. 5", "10115", "Berlin", new DateOnly(1990, 1, 1), IdDocumentType.IdCard, "L01X00T47")
        {
            IdExpiryDate = new DateOnly(2030, 1, 31),
        };
        var intake = Intake.Receive(IntakeRulesTests.Org, quote, "490154203237518", seller, Guid.NewGuid(), Now).Value;
        var findings = new QuoteInput(DeviceCondition.Gut, [], 90, null);
        Assert.True(intake.Inspect(findings, BuybackCalculator.Calculate(300_00, PolicySettings.Default, "APPLE", quote.ModelKey, findings), null, Now).IsSuccess);
        return (intake, quote);
    }

    [Fact]
    public void A_signature_confirms_exactly_the_signed_data_amount_and_method()
    {
        var (intake, quote) = Inspected();
        var signature = IntakeSignature.ForPurchase(intake, quote, 200_00, AnkaufPayment.Cash, Png, Guid.NewGuid(), Now).Value;
        Assert.True(signature.Confirms(intake, quote, 200_00, AnkaufPayment.Cash));
        Assert.True(signature.IsCurrent(intake, quote));
        Assert.Equal(SignaturePayload.Hash(intake, quote, 200_00, AnkaufPayment.Cash), signature.DataHash);

        Assert.False(signature.Confirms(intake, quote, 200_01, AnkaufPayment.Cash));
        Assert.False(signature.Confirms(intake, quote, 200_00, AnkaufPayment.BankTransfer));
        Assert.Null(intake.CheckPayable(quote, 200_00, AnkaufPayment.Cash, signature, signatureRequired: true));
        Assert.Equal("ankauf.signature_invalid", intake.CheckPayable(quote, 150_00, AnkaufPayment.Cash, signature, true)!.Code);
    }

    [Fact]
    public void Any_later_change_of_the_intake_invalidates_the_signature()
    {
        var (intake, quote) = Inspected();
        var signature = IntakeSignature.ForPurchase(intake, quote, 100_00, AnkaufPayment.Cash, Png, Guid.NewGuid(), Now).Value;
        // Re-inspection (other findings and time) → the seller must sign again.
        var findings = new QuoteInput(DeviceCondition.Akzeptabel, [DeviceDefect.ScreenDamage], 80, null);
        intake.Inspect(findings, BuybackCalculator.Calculate(300_00, PolicySettings.Default, "APPLE", quote.ModelKey, findings), null, Now.AddMinutes(5));
        Assert.False(signature.IsCurrent(intake, quote));
        Assert.Equal("ankauf.signature_invalid", intake.CheckPayable(quote, 100_00, AnkaufPayment.Cash, signature, true)!.Code);
    }

    [Fact]
    public void A_tampered_image_or_hash_is_detected()
    {
        var (intake, quote) = Inspected();
        var signature = IntakeSignature.ForPurchase(intake, quote, 200_00, AnkaufPayment.Cash, Png, Guid.NewGuid(), Now).Value;
        signature.Image![^20] ^= 0xFF;
        Assert.False(signature.Confirms(intake, quote, 200_00, AnkaufPayment.Cash));
    }

    [Fact]
    public void Signing_needs_an_inspected_intake_a_png_and_an_amount_within_the_offer()
    {
        var (intake, quote) = Inspected();
        Assert.Equal("ankauf.paid_above_offer",
            IntakeSignature.ForPurchase(intake, quote, intake.InspectedOfferMinor!.Value + 1, AnkaufPayment.Cash, Png, Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("ankauf.signature_image",
            IntakeSignature.ForPurchase(intake, quote, 100_00, AnkaufPayment.Cash, "GIF89a not a png at all, but long enough to pass the size check......"u8.ToArray(),
                Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("ankauf.signature_image", SignatureImage.Validate(TestImages.Png(30, 10)).Error!.Code); // implausible size
        Assert.True(SignatureImage.Validate(Png).IsSuccess);
        Assert.Equal("ankauf.signature_required", intake.CheckPayable(quote, 100_00, AnkaufPayment.Cash, null, signatureRequired: true)!.Code);
        Assert.Null(intake.CheckPayable(quote, 100_00, AnkaufPayment.Cash, null, signatureRequired: false));
    }

    [Fact]
    public void Times_are_hashed_with_database_precision()
    {
        var t = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        var reloaded = new DateTimeOffset(t.Ticks - t.Ticks % 10, t.Offset).ToUniversalTime(); // PostgreSQL keeps microseconds, UTC
        Assert.Equal(SignaturePayload.Time(t), SignaturePayload.Time(reloaded));
        Assert.Equal("2026-10-22T08:00:00.123456Z", SignaturePayload.Time(t));
    }

    [Fact]
    public void Anonymizing_removes_the_image_and_keeps_the_evidence()
    {
        var (intake, quote) = Inspected();
        var signature = IntakeSignature.ForPurchase(intake, quote, 200_00, AnkaufPayment.Cash, Png, Guid.NewGuid(), Now).Value;
        var hash = signature.BindingHash;
        signature.Anonymize(Now.AddYears(11));
        Assert.Null(signature.Image);
        Assert.Equal(hash, signature.BindingHash);
        Assert.Equal(Now.AddYears(11), signature.AnonymizedAt);
    }
}

public class CheckInTests
{
    private static readonly DateTimeOffset Now = IntakeRulesTests.Now;

    [Fact]
    public void A_check_in_link_is_unguessable_short_lived_and_stores_only_the_hash()
    {
        var (checkIn, token) = CheckIn.Create(Guid.NewGuid(), " Handy Müller ", Guid.NewGuid(), Now, 15);
        Assert.True(CheckInToken.LooksValid(token));
        Assert.Equal(CheckInToken.HashOf(token), checkIn.TokenHash);
        Assert.NotEqual(token, checkIn.TokenHash);
        Assert.Equal("Handy Müller", checkIn.ShopName);
        Assert.Equal(CheckInStatus.Open, checkIn.StatusAt(Now.AddMinutes(14)));
        Assert.Equal(CheckInStatus.Expired, checkIn.StatusAt(Now.AddMinutes(15)));
        Assert.False(CheckInToken.LooksValid("short"));
        Assert.NotEqual(token, CheckIn.Create(Guid.NewGuid(), "x", Guid.NewGuid(), Now, 15).Token);
    }

    [Fact]
    public void An_open_check_in_cannot_be_used_and_purging_removes_nothing_twice()
    {
        var (checkIn, _) = CheckIn.Create(Guid.NewGuid(), "Shop", Guid.NewGuid(), Now, 15);
        Assert.Equal("ankauf.checkin_not_submitted", checkIn.Consume(Guid.NewGuid(), Now.AddMinutes(1)).Error!.Code);
        Assert.Equal("ankauf.checkin_expired", checkIn.Consume(Guid.NewGuid(), Now.AddHours(1)).Error!.Code);
        checkIn.Purge(Now.AddDays(1));
        checkIn.Purge(Now.AddDays(2));
        Assert.Equal(Now.AddDays(1), checkIn.PurgedAt);
        Assert.Null(checkIn.Seller);
    }
}

public class AnkaufPayoutTests
{
    private static readonly DateTimeOffset Now = IntakeRulesTests.Now;
    private static readonly Guid Org = Guid.NewGuid();

    private static CashSession Session() =>
        CashSession.Open(Org, Register.Create(Org, "Kasse 1", Guid.NewGuid(), Now).Value, 100_00, Guid.NewGuid(), Now).Value;

    [Fact]
    public void The_ankauf_reason_is_reserved_for_payouts()
    {
        Assert.Equal("pos.reason_code_reserved",
            CashMovement.Record(Session(), CashMovementKind.Withdrawal, 10_00, null, 100_00, Guid.NewGuid(), Now, CashMovementReason.Ankauf).Error!.Code);
        Assert.DoesNotContain(CashMovementReasons.All, d => d.Code == CashMovementReason.Ankauf);
    }

    [Fact]
    public void A_payout_is_a_withdrawal_that_cannot_exceed_the_expected_cash()
    {
        var session = Session();
        Assert.Equal("pos.insufficient_cash",
            CashMovement.RecordPayout(session, CashMovementReason.Ankauf, 100_01, "Ankauf", "ankauf:1", 100_00, Guid.NewGuid(), Now).Error!.Code);
        var payout = CashMovement.RecordPayout(session, CashMovementReason.Ankauf, 80_00, null, "ankauf:1", 100_00, Guid.NewGuid(), Now).Value;
        Assert.Equal((CashMovementKind.Withdrawal, "Ankauf", "ankauf:1"), (payout.Kind, payout.Reason, payout.SourceReference));

        var other = CashMovement.Record(session, CashMovementKind.Withdrawal, 5_00, "Bank", 100_00, Guid.NewGuid(), Now).Value;
        var summary = SessionCalculator.Summarize(100_00, [], [payout, other]);
        Assert.Equal((1, 80_00L), (summary.AnkaufCount, summary.AnkaufPayoutsMinor));
        Assert.Equal(85_00, summary.WithdrawalsMinor); // payouts are withdrawals …
        Assert.Equal(15_00, summary.ExpectedCashMinor); // … and reduce the expected cash
    }
}

public class AnkaufbelegSignatureTests
{
    [Fact]
    public void The_receipt_shows_the_new_seller_fields_and_the_electronic_signature()
    {
        var quote = IntakeRulesTests.NewQuote();
        var seller = new SellerDetails("Erika", "Mustermann", "Heidestr. 17", "51147", "Köln", new DateOnly(1964, 8, 12), IdDocumentType.Passport, "C01X00T47")
        {
            IdExpiryDate = new DateOnly(2027, 2, 28), Nationality = "D", IdIssuingCountry = "D", IdIssuingAuthority = "Stadt Köln",
        };
        var intake = Intake.Receive(IntakeRulesTests.Org, quote, "490154203237518", seller, Guid.NewGuid(), IntakeRulesTests.Now, SellerCaptureMethod.Mrz).Value;
        var findings = new QuoteInput(DeviceCondition.Gut, [], null, null);
        intake.Inspect(findings, BuybackCalculator.Calculate(300_00, PolicySettings.Default, "APPLE", quote.ModelKey, findings), null, IntakeRulesTests.Now);
        var signature = IntakeSignature.ForPurchase(intake, quote, 250_00, AnkaufPayment.Cash, TestImages.Png(400, 150), Guid.NewGuid(), IntakeRulesTests.Now).Value;
        Assert.True(intake.Complete(250_00, AnkaufPayment.Cash, Guid.NewGuid(), IntakeRulesTests.Now, quote, signature, true).IsSuccess);
        Assert.Equal(signature.Id, intake.SignatureId);

        var content = Ankaufbeleg.Build(intake, quote, new BelegDealer("Shop", "Shop GmbH", "A 1", "10115", "Berlin", "DE", null), Ankaufbeleg.GermanTimeZone,
            new BelegSignature(signature.SignedAt, signature.BindingHash, signature.Source, signature.Image)).Value;
        var sellerFields = content.Sections.Single(s => s.Heading == "Verkäufer/in (Privatperson)").Fields.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("28.02.2027", sellerFields["Gültig bis"]);
        Assert.Equal("D", sellerFields["Staatsangehörigkeit"]);
        Assert.Equal("Stadt Köln, D", sellerFields["Ausgestellt von"]);
        Assert.StartsWith("maschinenlesbare Zone", sellerFields["Erfassung"]);
        var signed = content.Sections.Single(s => s.Heading == "Elektronische Unterschrift").Fields.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal(signature.BindingHash[..16].ToUpperInvariant(), signed["Prüfsumme"]);
        Assert.NotNull(content.SellerSignaturePng);
        Assert.DoesNotContain("C01X00T47", string.Join("\n", content.AllText()));
    }
}

public class ReportDayTests
{
    [Fact]
    public void A_report_day_follows_german_local_time_including_daylight_saving()
    {
        var start = ReportDay.StartOf(new DateOnly(2026, 10, 25));
        var end = ReportDay.StartOf(new DateOnly(2026, 10, 26));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(25), end - start); // clocks go back on 25 Oct 2026
        Assert.Equal(new DateOnly(2026, 10, 23), ReportDay.Today(new DateTimeOffset(2026, 10, 22, 22, 30, 0, TimeSpan.Zero)));
    }
}
