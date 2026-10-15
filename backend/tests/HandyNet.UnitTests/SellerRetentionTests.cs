using System.Text.Json;
using HandyNet.Modules.Ankauf.Domain;

namespace HandyNet.UnitTests;

public class SellerRetentionTests
{
    private static readonly DateTimeOffset Now = IntakeRulesTests.Now;

    private static readonly SellerDetails Seller =
        new("Jürgen", "Weiß", "Lindenstraße 5", "10115", "Berlin", new DateOnly(1990, 5, 1), IdDocumentType.Passport, "C4F7G9H2K");

    private static (Intake Intake, Quote Quote) Completed()
    {
        var quote = IntakeRulesTests.NewQuote();
        var intake = Intake.Receive(IntakeRulesTests.Org, quote, "490154203237518", Seller, Guid.NewGuid(), Now).Value;
        var findings = new QuoteInput(DeviceCondition.Gut, [], 90, null);
        var calc = BuybackCalculator.Calculate(300_00, PolicySettings.Default, "APPLE", quote.ModelKey, findings);
        Assert.True(intake.Inspect(findings, calc, null, Now).IsSuccess);
        Assert.True(intake.Complete(200_00, PaymentMethod.Cash, Guid.NewGuid(), Now).IsSuccess);
        return (intake, quote);
    }

    [Fact]
    public void Cutoff_is_the_configured_number_of_years_back()
    {
        Assert.Equal(Now.AddYears(-10), Intake.RetentionCutoff(Now, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Intake.RetentionCutoff(Now, 0));
    }

    [Fact]
    public void Seller_data_is_replaced_by_the_placeholder_and_everything_else_is_kept()
    {
        var (intake, _) = Completed();
        var later = Now.AddYears(11);

        Assert.Equal("ankauf.retention_not_over", intake.AnonymizeSeller(Intake.RetentionCutoff(Now.AddYears(5), 10), later).Error!.Code);
        Assert.Equal(Seller.LastName, intake.Seller.LastName);

        Assert.True(intake.AnonymizeSeller(Intake.RetentionCutoff(later, 10), later).IsSuccess);
        Assert.Equal(SellerDetails.Anonymized, intake.Seller);
        Assert.Equal(later, intake.AnonymizedAt);
        Assert.Equal((IntakeStatus.Completed, 200_00L, PaymentMethod.Cash), (intake.Status, intake.PaidMinor!.Value, intake.PaymentMethod!.Value));
        Assert.Equal("490154203237518", intake.Imei);

        // Idempotent: a second run keeps the first timestamp.
        Assert.True(intake.AnonymizeSeller(Intake.RetentionCutoff(later.AddDays(1), 10), later.AddDays(1)).IsSuccess);
        Assert.Equal(later, intake.AnonymizedAt);
    }

    [Fact]
    public void Open_intakes_are_measured_from_receipt_and_cannot_continue_once_anonymized()
    {
        var quote = IntakeRulesTests.NewQuote();
        var intake = Intake.Receive(IntakeRulesTests.Org, quote, "490154203237518", Seller, Guid.NewGuid(), Now).Value;
        Assert.Equal(Now, intake.RetentionStart);
        Assert.True(intake.AnonymizeSeller(Now.AddSeconds(1), Now.AddYears(11)).IsSuccess);

        var findings = new QuoteInput(DeviceCondition.Gut, [], 90, null);
        var calc = BuybackCalculator.Calculate(300_00, PolicySettings.Default, "APPLE", quote.ModelKey, findings);
        Assert.Equal("ankauf.intake_anonymized", intake.Inspect(findings, calc, null, Now).Error!.Code);
    }

    [Fact]
    public void Ankaufbeleg_of_an_anonymized_intake_shows_anonymisiert_only()
    {
        var (intake, quote) = Completed();
        Assert.True(intake.AnonymizeSeller(Now.AddDays(1), new DateTimeOffset(2036, 11, 2, 10, 0, 0, TimeSpan.Zero)).IsSuccess);

        var content = Ankaufbeleg.Build(intake, quote, new BelegDealer("Handy Müller", "Handy Müller GmbH", "Königstraße 7", "70173", "Stuttgart", "DE", null),
            Ankaufbeleg.GermanTimeZone).Value;
        var seller = content.Sections.Single(s => s.Heading == "Verkäufer/in (Privatperson)");
        Assert.All(seller.Fields, f => Assert.Equal("anonymisiert", f.Value));
        Assert.Contains("02.11.2036", seller.Paragraphs.Single());
        var text = string.Join("\n", content.AllText());
        Assert.DoesNotContain("Weiß", text);
        Assert.DoesNotContain("Lindenstraße", text);
        Assert.DoesNotContain("H2K", text);
        Assert.Contains("200,00 €", text); // purchase record kept
    }

    [Fact]
    public void Placeholder_serializes_to_what_the_database_constraint_expects()
    {
        // Same options as HandyNet.Persistence.JsonColumn (web defaults); ck_intakes_anonymized compares with this document.
        var json = JsonSerializer.Serialize(SellerDetails.Anonymized, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("{\"firstName\":\"anonymisiert\",\"lastName\":\"anonymisiert\",\"street\":\"anonymisiert\",\"postalCode\":\"00000\","
                     + "\"city\":\"anonymisiert\",\"birthDate\":\"1900-01-01\",\"idDocumentType\":0,\"idDocumentNumber\":\"ANONYMISIERT\"}", json);
    }
}
