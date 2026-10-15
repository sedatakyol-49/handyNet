using HandyNet.Modules.Ankauf.Application;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Tests.Support;

namespace HandyNet.UnitTests;

public class AnkaufbelegTests
{
    private static readonly DateTimeOffset Now = IntakeRulesTests.Now; // 12:00 UTC = 14:00 in Germany (CEST)

    private static readonly BelegDealer Dealer = new("Handy Müller", "Handy Müller GmbH", "Königstraße 7", "70173", "Stuttgart", "DE", "DE123456789");

    private static SellerDetails Seller(string idNumber = "L01X00T47") =>
        new("Jürgen", "Weiß", "Lindenstraße 5", "10115", "Berlin", new DateOnly(1990, 5, 1), IdDocumentType.IdCard, idNumber);

    private static (Intake Intake, Quote Quote) Intake(PaymentMethod method = PaymentMethod.Cash, bool complete = true,
        DeviceDefect[]? defects = null, string idNumber = "L01X00T47")
    {
        var quote = IntakeRulesTests.NewQuote();
        var intake = Modules.Ankauf.Domain.Intake.Receive(IntakeRulesTests.Org, quote, "490154203237518", Seller(idNumber), Guid.NewGuid(), Now).Value;
        var findings = new QuoteInput(DeviceCondition.SehrGut, defects ?? [DeviceDefect.HousingDamage, DeviceDefect.ScreenDamage], 88, null);
        var calc = BuybackCalculator.Calculate(300_00, PolicySettings.Default, "APPLE", quote.ModelKey, findings);
        Assert.True(intake.Inspect(findings, calc, "Kratzer Rückseite", Now).IsSuccess);
        if (complete)
            Assert.True(intake.Complete(123_45, method, Guid.NewGuid(), Now).IsSuccess);
        return (intake, quote);
    }

    private static Dictionary<string, string> Fields(AnkaufbelegContent content, string heading) =>
        content.Sections.Single(s => s.Heading == heading).Fields.ToDictionary(f => f.Label, f => f.Value);

    [Theory]
    [InlineData("L01X00T47", "*****0T47")]
    [InlineData(" C4F7G9H2K ", "*****9H2K")]
    [InlineData("12345", "*2345")]
    [InlineData("1234", "****")]
    [InlineData("X1", "**")]
    public void Id_number_shows_only_the_last_four_characters(string number, string masked) =>
        Assert.Equal(masked, Ankaufbeleg.MaskIdNumber(number));

    [Fact]
    public void Document_number_uses_the_date_and_the_random_tail_of_the_intake_id()
    {
        var id = Guid.Parse("0199e5a1-2b3c-7d4e-8f00-1a2b3c4d5e6f");
        Assert.Equal("AB-20261015-3C4D5E6F", Ankaufbeleg.DocumentNumber(id, new DateOnly(2026, 10, 15)));
    }

    [Fact]
    public void Contains_every_required_field()
    {
        var (intake, quote) = Intake();
        var content = Ankaufbeleg.Build(intake, quote, Dealer, Ankaufbeleg.GermanTimeZone).Value;

        Assert.Equal("Ankaufbeleg", content.Title);
        Assert.Equal(Ankaufbeleg.DocumentNumber(intake.Id, new DateOnly(2026, 10, 15)), content.DocumentNumber);
        Assert.Equal("15.10.2026, 14:00 Uhr", content.PurchaseDate);

        var dealer = Fields(content, "Käufer (Händler)");
        Assert.Equal("Handy Müller", dealer["Name"]);
        Assert.Equal("Handy Müller GmbH", dealer["Firma"]);
        Assert.Equal("Königstraße 7, 70173 Stuttgart", dealer["Anschrift"]);
        Assert.Equal("DE123456789", dealer["USt-IdNr."]);

        var seller = Fields(content, "Verkäufer/in (Privatperson)");
        Assert.Equal("Jürgen Weiß", seller["Name"]);
        Assert.Equal("Lindenstraße 5, 10115 Berlin", seller["Anschrift"]);
        Assert.Equal("01.05.1990", seller["Geburtsdatum"]);
        Assert.Equal("Personalausweis", seller["Ausweisdokument"]);
        Assert.Equal("*****0T47", seller["Ausweisnummer"]);

        var device = Fields(content, "Gerät");
        Assert.Equal("Apple", device["Hersteller"]);
        Assert.Equal("iPhone 13", device["Modell"]);
        Assert.Equal("128 GB", device["Speicher"]);
        Assert.Equal("490154203237518", device["IMEI"]);
        Assert.Equal("Sehr gut", device["Zustand (geprüft)"]);
        Assert.Equal("Displayschaden, Gehäuseschaden", device["Mängel"]);
        Assert.Equal("88 %", device["Akkuzustand"]);
        Assert.Equal("Kratzer Rückseite", device["Prüfvermerk"]);

        var payment = Fields(content, "Ankaufpreis und Zahlung");
        Assert.Equal("123,45 €", payment["Ankaufpreis"]);
        Assert.Equal("Barzahlung", payment["Zahlungsart"]);

        var text = string.Join("\n", content.AllText());
        Assert.Contains("§ 25a UStG", text);
        Assert.Contains("keine gesondert ausgewiesene Umsatzsteuer", text);
        Assert.Contains("alleiniger Eigentümer", text);
        Assert.Contains("frei von Rechten Dritter", text);
        Assert.Contains("in bar erhalten", text);
        Assert.Equal(2, content.SignatureLabels.Count);
        Assert.Contains(content.SignatureLabels, l => l.Contains("Verkäufer/in"));
        Assert.Contains(content.SignatureLabels, l => l.Contains("Käufer (Händler)"));

        // The full ID number never appears.
        Assert.DoesNotContain("L01X00T47", text);
    }

    [Fact]
    public void Bank_transfer_and_no_defects_are_worded_accordingly()
    {
        var (intake, quote) = Intake(PaymentMethod.BankTransfer, defects: []);
        var content = Ankaufbeleg.Build(intake, quote, Dealer with { LegalName = "Handy Müller", VatId = null }, Ankaufbeleg.GermanTimeZone).Value;

        Assert.Equal("Überweisung", Fields(content, "Ankaufpreis und Zahlung")["Zahlungsart"]);
        Assert.Equal("keine festgestellt", Fields(content, "Gerät")["Mängel"]);
        Assert.DoesNotContain("in bar erhalten", string.Join("\n", content.AllText()));
        Assert.False(Fields(content, "Käufer (Händler)").ContainsKey("Firma"));
        Assert.False(Fields(content, "Käufer (Händler)").ContainsKey("USt-IdNr."));
    }

    [Fact]
    public void Only_completed_intakes_have_a_receipt()
    {
        var (inspected, quote) = Intake(complete: false);
        Assert.Equal("ankauf.not_completed", Ankaufbeleg.Build(inspected, quote, Dealer, TimeZoneInfo.Utc).Error!.Code);

        var (rejected, quote2) = Intake(complete: false);
        rejected.Reject("Gerät gesperrt", Now);
        Assert.Equal("ankauf.not_completed", Ankaufbeleg.Build(rejected, quote2, Dealer, TimeZoneInfo.Utc).Error!.Code);
    }

    [Fact]
    public void Rendered_pdf_contains_the_content_with_umlauts_and_euro()
    {
        var (intake, quote) = Intake();
        var content = Ankaufbeleg.Build(intake, quote, Dealer, Ankaufbeleg.GermanTimeZone).Value;
        var pdf = AnkaufbelegPdf.Render(content);
        var text = PdfText.All(pdf);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        foreach (var expected in new[] { "Ankaufbeleg", content.DocumentNumber, "Jürgen Weiß", "Königstraße 7, 70173 Stuttgart", "490154203237518",
                     "123,45 €", "*****0T47", "Ort, Datum, Unterschrift Verkäufer/in", "Ort, Datum, Unterschrift Käufer (Händler)" })
            Assert.Contains(expected, text);
        Assert.Contains("§ 25a UStG", text.Replace("\n", " "));
        Assert.DoesNotContain("L01X00T47", text);
    }
}
