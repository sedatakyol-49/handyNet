using System.Text.RegularExpressions;
using HandyNet.Modules.Notifications.Domain;
using HandyNet.Modules.Pos.Contracts;

namespace HandyNet.UnitTests;

public partial class EmailLocalizationTests
{
    public static TheoryData<string> Locales => new(EmailText.Locales);

    [GeneratedRegex(@"\{[a-z]+\}")]
    private static partial Regex Token();

    private const string Link = "https://app.example/x#token=abc123";

    private static void AssertClean(RenderedEmail e, params string[] mustContain)
    {
        Assert.False(string.IsNullOrWhiteSpace(e.Subject));
        Assert.False(string.IsNullOrWhiteSpace(e.Body));
        foreach (var part in new[] { e.Subject, e.Body, e.Html ?? "" })
            Assert.DoesNotMatch(Token(), part);
        foreach (var s in mustContain)
            Assert.Contains(s, e.Subject + "\n" + e.Body);
    }

    [Fact]
    public void Seven_locales_are_covered() => Assert.Equal(["de", "en", "tr", "es", "ru", "ar", "fa"], EmailText.Locales);

    [Fact]
    public void Every_text_exists_in_every_locale_with_identical_placeholders()
    {
        foreach (var key in EmailText.Keys)
        {
            var reference = Token().Matches(EmailText.Get(key, "de")).Select(m => m.Value).Order().ToArray();
            foreach (var locale in EmailText.Locales)
            {
                var text = EmailText.Get(key, locale);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{key}/{locale} is empty");
                Assert.True(reference.SequenceEqual(Token().Matches(text).Select(m => m.Value).Order()), $"{key}/{locale} placeholders differ from de");
            }
        }
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("TR", "tr")]
    [InlineData("fa_IR", "fa")]
    [InlineData("fr", "de")]
    [InlineData("", "de")]
    [InlineData(null, "de")]
    public void Unknown_languages_fall_back_to_german(string? input, string expected) => Assert.Equal(expected, EmailText.Normalize(input));

    [Theory, MemberData(nameof(Locales))]
    public void Account_emails_render_in_every_locale(string locale)
    {
        var verify = EmailTemplates.EmailVerification("NAMEX", Link, TimeSpan.FromHours(48), locale);
        AssertClean(verify, "NAMEX", Link, "48");
        var reset = EmailTemplates.PasswordReset("NAMEX", Link, TimeSpan.FromMinutes(30), locale);
        AssertClean(reset, "NAMEX", Link, "30");
        Assert.Contains("SEHA", verify.Body + reset.Body, StringComparison.Ordinal);
        Assert.NotEqual(verify.Subject, reset.Subject);
    }

    [Theory, MemberData(nameof(Locales))]
    public void Order_and_organization_emails_render_in_every_locale(string locale)
    {
        AssertClean(EmailTemplates.OrderPlacedForSeller("SELLERX", "BUYERX", 123456, Link, locale), "SELLERX", "BUYERX", "1.234,56 €", Link);
        foreach (var status in new[] { "Confirmed", "Shipped", "Rejected", "Cancelled", "Other" })
        {
            var mail = EmailTemplates.OrderStatusForCounterparty(status, "PARTYX", "REASONX", Link, locale);
            AssertClean(mail, "REASONX", Link);
            if (status != "Other")
                Assert.Contains("PARTYX", mail.Body);
        }
        Assert.DoesNotContain("REASONX", EmailTemplates.OrderStatusForCounterparty("Shipped", "PARTYX", null, Link, locale).Body);
        AssertClean(EmailTemplates.OrganizationVerified("ORGX", Link, locale), "ORGX", Link);
    }

    [Fact]
    public void Every_locale_has_distinct_account_subjects()
    {
        var subjects = EmailText.Locales.Select(l => EmailTemplates.EmailVerification("A", Link, TimeSpan.FromHours(1), l).Subject).ToList();
        Assert.Equal(subjects.Count, subjects.Distinct().Count());
    }

    [Fact]
    public void Unknown_language_renders_german()
    {
        var unknown = EmailTemplates.EmailVerification("A", Link, TimeSpan.FromHours(2), "fr");
        Assert.Equal(EmailTemplates.EmailVerification("A", Link, TimeSpan.FromHours(2), "de"), unknown);
    }

    [Theory]
    [InlineData("ru", 1, "1 часа")]
    [InlineData("ru", 48, "48 часов")]
    [InlineData("ar", 2, "ساعتين")]
    [InlineData("es", 1, "1 hora")]
    public void Durations_are_inflected(string locale, int hours, string expected) =>
        Assert.Equal(expected, EmailText.Duration(TimeSpan.FromHours(hours), locale));

    private static readonly DateTimeOffset At = new(2026, 3, 2, 10, 30, 0, TimeSpan.Zero);

    private static EReceiptContent Receipt() => new("TITLEX", "SHOPX", "LEGALX", ["Hauptstr. 1"], "DE123", "REGX", 42, At, "EUR",
        [new EReceiptLine("ITEMX", "350000000000001", 1, 10_00, 1_00, 9_00, "A", "NOTEX")],
        [new EReceiptVat("A 19 %", 7_56, 1_44, 9_00)], 9_00, [new EReceiptPayment("Cash", 9_00, 10_00)], 1_00, ["FOOTX"], 1);

    private static SalesDocumentEmailRequested Document(string kind) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, "TITLEX", "NR-1",
        "kunde@example.de", "SHOPX", 119_00, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 16), new DateOnly(2026, 4, 1), "doc.pdf", Convert.ToBase64String([1, 2, 3]));

    [Theory, MemberData(nameof(Locales))]
    public void Receipt_and_sales_documents_render_in_every_locale_and_rtl_only_for_ar_fa(string locale)
    {
        var rtl = locale is "ar" or "fa";

        var receipt = EReceiptTemplate.Render(Receipt(), locale);
        AssertClean(receipt, "SHOPX", "ITEMX", "NOTEX", "FOOTX", "42", "10,00 €");
        Assert.Contains($"<html lang=\"{locale}\"", receipt.Html);
        Assert.Equal(rtl, receipt.Html!.Contains("dir=\"rtl\""));

        foreach (var kind in new[] { "Quotation", "Invoice" })
        {
            var doc = SalesDocumentTemplate.Render(Document(kind), locale);
            AssertClean(doc, "SHOPX", "119,00 €", "02.03.2026");
            Assert.Contains("NR-1", doc.Body);
            Assert.Contains($"<html lang=\"{locale}\"", doc.Html);
            Assert.Equal(rtl, doc.Html!.Contains("dir=\"rtl\""));
            Assert.NotNull(doc.Attachment);
        }
    }

    [Fact]
    public void Placeholder_free_german_receipt_is_unchanged_by_the_default_language()
    {
        Assert.Equal(EReceiptTemplate.Render(Receipt()), EReceiptTemplate.Render(Receipt(), "de"));
        Assert.Contains("Rückgeld 1,00 €", EReceiptTemplate.Render(Receipt()).Body);
    }
}
