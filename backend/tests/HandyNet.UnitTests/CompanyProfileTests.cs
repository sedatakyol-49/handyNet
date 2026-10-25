using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HandyNet.FileStorage.Imaging;
using HandyNet.Modules.Ankauf.Application;
using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Application;
using HandyNet.Modules.Tenancy.Domain;
using HandyNet.SharedKernel.Documents;
using HandyNet.SharedKernel.Imaging;
using HandyNet.Tests.Support;
using SkiaSharp;

namespace HandyNet.UnitTests;

/// <summary>Company data maintained by the tenant (ADR-0014): IBAN/BIC/VAT rules, legal-change history, letterhead lines.</summary>
public class CompanyProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    private static Organization NewOrganization() =>
        Organization.Register(new OrganizationProfile("Handy Shop", "Handy Shop GmbH", "DE123456789", "Hauptstraße 1", "10115", "Berlin", "DE",
            false, true, false), Owner, Now);

    private static CompanyProfile Profile(Organization o) => o.CompanyProfile;

    [Theory]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("de89 3704 0044 0532 0130 00", true)]          // spaces and lower case are fine
    [InlineData("AT611904300234573201", true)]
    [InlineData("GB29NWBK60161331926819", true)]
    [InlineData("DE89370400440532013001", false)]               // check digits wrong
    [InlineData("DE8937040044053201300", false)]                // German IBANs have 22 characters
    [InlineData("DE89-3704-0044-0532-0130-00", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("", false)]
    public void Iban_needs_the_country_length_and_a_valid_mod_97_checksum(string iban, bool valid) =>
        Assert.Equal(valid, CompanyProfileRules.IsValidIban(iban));

    [Theory]
    [InlineData("COBADEFFXXX", true)]
    [InlineData("COBADEFF", true)]
    [InlineData("cobadeff", true)]
    [InlineData("COBADE", false)]
    [InlineData("COBADEFFXX", false)]
    [InlineData("C0BADEFF", false)] // digit in the bank code
    public void Bic_has_8_or_11_characters(string bic, bool valid) => Assert.Equal(valid, CompanyProfileRules.IsValidBic(bic));

    [Theory]
    [InlineData("DE123456789", "DE", true)]
    [InlineData("DE 123 456 789", "DE", true)]
    [InlineData("DE12345678", "DE", false)]
    [InlineData("ATU12345678", "DE", false)]  // a German company has a German VAT ID
    [InlineData("ATU12345678", "AT", true)]
    [InlineData("12345678", "AT", false)]
    public void Vat_id_format_depends_on_the_country(string vat, string country, bool valid) =>
        Assert.Equal(valid, CompanyProfileRules.IsValidVatId(vat, country));

    [Fact]
    public void Document_settings_are_normalized_and_stored()
    {
        var organization = NewOrganization();
        var result = organization.UpdateCompanyProfile(Profile(organization) with
        {
            Iban = "de89 3704 0044 0532 0130 00",
            Bic = "cobadeff xxx",
            BankName = " Commerzbank ",
            ReceiptFooter = "Mo–Fr 10–19 Uhr  \r\nUmtausch binnen 14 Tagen\r\n",
            PaymentTermsDays = 14,
            ContactEmail = "Info@Shop.DE",
        }, Owner, Now);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("DE89370400440532013000", organization.Iban);
        Assert.Equal("COBADEFFXXX", organization.Bic);
        Assert.Equal("Commerzbank", organization.BankName);
        Assert.Equal("Mo–Fr 10–19 Uhr\nUmtausch binnen 14 Tagen", organization.ReceiptFooter);
        Assert.Equal(14, organization.PaymentTermsDays);
        Assert.Equal("info@shop.de", organization.ContactEmail);
        Assert.Empty(result.Value.LegalFieldsChanged);
        Assert.Single(organization.StatusChanges); // only the registration — no legal change
    }

    [Theory]
    [InlineData("DE89370400440532013001", null, null, "company.invalid_iban")]
    [InlineData(null, "COBADEFFXXX", null, "company.invalid_iban")] // BIC without IBAN
    [InlineData(null, null, "Sparkasse", "company.invalid_iban")]  // bank name without IBAN
    [InlineData("DE89370400440532013000", "COBA", null, "company.invalid_bic")]
    public void Invalid_bank_details_are_refused(string? iban, string? bic, string? bank, string code)
    {
        var organization = NewOrganization();
        var result = organization.UpdateCompanyProfile(Profile(organization) with { Iban = iban, Bic = bic, BankName = bank }, Owner, Now);
        Assert.Equal(code, result.Error?.Code);
        Assert.Null(organization.Iban);
    }

    [Fact]
    public void Receipt_texts_payment_terms_and_vat_are_limited()
    {
        var organization = NewOrganization();
        var p = Profile(organization);
        Assert.Equal("company.invalid_receipt_header",
            organization.UpdateCompanyProfile(p with { ReceiptHeader = string.Join('\n', Enumerable.Repeat("x", 13)) }, Owner, Now).Error?.Code);
        Assert.Equal("company.invalid_receipt_footer",
            organization.UpdateCompanyProfile(p with { ReceiptFooter = new string('x', CompanyProfileRules.MaxReceiptFooterLength + 1) }, Owner, Now).Error?.Code);
        Assert.Equal("company.invalid_payment_terms", organization.UpdateCompanyProfile(p with { PaymentTermsDays = 366 }, Owner, Now).Error?.Code);
        Assert.Equal("company.invalid_payment_terms", organization.UpdateCompanyProfile(p with { PaymentTermsDays = -1 }, Owner, Now).Error?.Code);
        Assert.Equal("company.invalid_vat_id", organization.UpdateCompanyProfile(p with { VatId = "DE1234" }, Owner, Now).Error?.Code);
        Assert.Equal("company.invalid_legal_name", organization.UpdateCompanyProfile(p with { LegalName = " " }, Owner, Now).Error?.Code);
    }

    [Fact]
    public void A_change_of_legal_name_or_vat_id_is_recorded_for_the_platform_admin_without_changing_the_status()
    {
        var organization = NewOrganization();
        organization.Verify(Guid.NewGuid(), Now);
        var historyBefore = organization.StatusChanges.Count;

        var result = organization.UpdateCompanyProfile(Profile(organization) with { LegalName = "Handy Shop Berlin GmbH", VatId = "DE987654321" }, Owner, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(["LegalName", "VatId"], result.Value.LegalFieldsChanged);
        Assert.Equal(OrganizationStatus.Verified, organization.Status); // no re-verification for now
        var entry = organization.StatusChanges.Last();
        Assert.Equal(historyBefore + 1, organization.StatusChanges.Count);
        Assert.Equal(OrganizationHistoryKind.LegalDetailsChanged, entry.Kind);
        Assert.Equal(OrganizationStatus.Verified, entry.FromStatus);
        Assert.Equal(OrganizationStatus.Verified, entry.ToStatus);
        Assert.Equal(Owner, entry.ChangedBy);
        Assert.Contains("Handy Shop GmbH → Handy Shop Berlin GmbH", entry.Reason);
        Assert.Contains("DE123456789 → DE987654321", entry.Reason);
    }

    [Fact]
    public void Saving_unchanged_data_is_a_no_op_and_a_display_name_change_is_not_a_legal_change()
    {
        var organization = NewOrganization();
        var unchanged = organization.UpdateCompanyProfile(Profile(organization) with { VatId = "de 123456789" }, Owner, Now);
        Assert.False(unchanged.Value.Changed);

        var renamed = organization.UpdateCompanyProfile(Profile(organization) with { Name = "Handy Shop Mitte" }, Owner, Now);
        Assert.True(renamed.Value.Changed);
        Assert.Empty(renamed.Value.LegalFieldsChanged);
        Assert.Equal("Handy Shop Mitte", organization.Name);
        Assert.Single(organization.StatusChanges);
    }

    [Fact]
    public void Letterhead_footer_lists_contact_register_tax_and_bank_details()
    {
        var organization = NewOrganization();
        organization.UpdateCompanyProfile(Profile(organization) with
        {
            ContactPhone = "+49 30 123456",
            ManagingDirector = "Erika Muster",
            RegisterCourt = "Amtsgericht Charlottenburg",
            RegisterNumber = "HRB 12345",
            TaxNumber = "12/345/67890",
            Iban = "DE89370400440532013000",
            Bic = "COBADEFFXXX",
            BankName = "Commerzbank",
        }, Owner, Now);

        var lines = OrganizationBrandingSource.FooterLines(organization.CompanyProfile);

        Assert.Equal("Handy Shop GmbH · Hauptstraße 1, 10115 Berlin · Tel. +49 30 123456", lines[0]);
        Assert.Equal("Geschäftsführung: Erika Muster · Registergericht: Amtsgericht Charlottenburg HRB 12345 · USt-IdNr.: DE123456789 · Steuernummer: 12/345/67890",
            lines[1]);
        Assert.Equal("Bankverbindung: Commerzbank · IBAN DE89 3704 0044 0532 0130 00 · BIC COBADEFFXXX", lines[2]);
    }

    [Fact]
    public void Letterhead_without_optional_data_has_only_the_address_line()
    {
        var organization = Organization.Register(new OrganizationProfile("A", "A GmbH", null, "Weg 1", "10115", "Berlin", "DE", false, true, false), Owner, Now);
        Assert.Equal(["A GmbH · Weg 1, 10115 Berlin"], OrganizationBrandingSource.FooterLines(organization.CompanyProfile));
    }
}

/// <summary>Shared SkiaSharp codec (IImageCodec) as used for company logos.</summary>
public class SkiaImageCodecTests
{
    private readonly SkiaImageCodec _codec = new();

    private static readonly RenditionSpec[] LogoSpecs =
    [
        new(OrganizationLogo.ScreenMaxSide, RasterFormat.Png),
        new(OrganizationLogo.PrintMaxSide, RasterFormat.Jpeg, OrganizationLogo.PrintJpegQuality, FlattenOnWhite: true),
    ];

    private static byte[] TransparentPng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Logo_upload_becomes_a_small_png_and_a_print_jpeg_without_metadata()
    {
        var upload = TestImages.Jpeg(2400, 600, exifOrientation: 1);
        Assert.True(TestImages.Contains(upload, TestImages.ExifSecret));

        var result = _codec.Render(upload, OrganizationLogo.Limits, LogoSpecs);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var (screen, print) = (result.Value.Images[0], result.Value.Images[1]);
        Assert.Equal(RasterFormat.Jpeg, result.Value.OriginalFormat);
        Assert.Equal(RasterFormat.Png, ImageMagic.Detect(screen.Bytes));
        Assert.Equal(RasterFormat.Jpeg, ImageMagic.Detect(print.Bytes));
        Assert.Equal((480, 120), (screen.Width, screen.Height));
        Assert.Equal((1200, 300), (print.Width, print.Height));
        Assert.Equal((1200, 300), SimplePdf.JpegSize(print.Bytes));
        foreach (var output in new[] { screen.Bytes, print.Bytes })
        {
            Assert.False(TestImages.Contains(output, TestImages.ExifSecret));
            Assert.False(TestImages.Contains(output, "Exif"));
        }
    }

    [Fact]
    public void Transparency_stays_in_the_png_and_becomes_white_in_the_jpeg()
    {
        var result = _codec.Render(TransparentPng(100, 100), OrganizationLogo.Limits, LogoSpecs);
        Assert.True(result.IsSuccess);
        using var png = SKBitmap.Decode(result.Value.Images[0].Bytes);
        using var jpeg = SKBitmap.Decode(result.Value.Images[1].Bytes);
        Assert.Equal(0, png.GetPixel(2, 2).Alpha);
        var corner = jpeg.GetPixel(2, 2);
        Assert.True(corner.Red > 245 && corner.Green > 245 && corner.Blue > 245, corner.ToString());
        Assert.True(jpeg.GetPixel(50, 50).Red > 200 && jpeg.GetPixel(50, 50).Green < 60); // the red square
    }

    [Fact]
    public void Wrong_type_too_large_too_small_and_corrupt_uploads_are_refused()
    {
        Assert.Equal("image.unsupported_type", _codec.Render("GIF89a......"u8.ToArray(), OrganizationLogo.Limits, LogoSpecs).Error?.Code);
        Assert.Equal("image.unsupported_type",
            _codec.Render(Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"), OrganizationLogo.Limits, LogoSpecs).Error?.Code);
        Assert.Equal("image.too_large", _codec.Render(new byte[OrganizationLogo.MaxUploadBytes + 1], OrganizationLogo.Limits, LogoSpecs).Error?.Code);
        Assert.Equal("image.empty", _codec.Render([], OrganizationLogo.Limits, LogoSpecs).Error?.Code);
        Assert.Equal("image.too_small", _codec.Render(TestImages.Png(10, 40), OrganizationLogo.Limits, LogoSpecs).Error?.Code);
        byte[] fake = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 1, 2, 3, 4, 5, 6, 7];
        Assert.Equal("image.corrupt", _codec.Render(fake, OrganizationLogo.Limits, LogoSpecs).Error?.Code);
    }

    [Fact]
    public void Webp_is_converted_to_jpeg_for_pdfs()
    {
        var jpeg = _codec.ToJpeg(TestImages.Webp(300, 200), new ImageDecodeLimits(5 * 1024 * 1024), maxSide: 150);
        Assert.True(jpeg.IsSuccess);
        Assert.Equal("image/jpeg", jpeg.Value.ContentType);
        Assert.Equal((150, 100), SimplePdf.JpegSize(jpeg.Value.Bytes));
    }
}

/// <summary>Images and footer lines in the shared PDF writer (ADR-0014).</summary>
public class SimplePdfImageTests
{
    private static string Latin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static byte[] Jpeg(int w, int h) => new SkiaImageCodec().ToJpeg(TestImages.Png(w, h), new ImageDecodeLimits(5 * 1024 * 1024)).Value.Bytes;

    [Fact]
    public void A_jpeg_is_embedded_once_as_dct_xobject_and_drawn_where_placed()
    {
        var logo = Jpeg(400, 100);
        var pdf = new SimplePdf();
        pdf.Image(logo, x: 400, y: 40, width: 120, height: 30).Text("Rechnung");
        pdf.NewPage().Image(logo, 50, 50, 100, 25);
        var bytes = pdf.ToBytes();
        var text = Latin1(bytes);

        Assert.Equal(1, PdfText.ImageCount(bytes));
        Assert.Equal(2, PdfText.ImageDraws(bytes));
        Assert.Contains("/Subtype /Image /Width 400 /Height 100 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", text);
        Assert.Contains($"/Length {logo.Length} >>", text);
        // top-left (400, 40) of a 30 pt high image on an A4 page → PDF y = 841.89 − 40 − 30
        Assert.Contains("q 120 0 0 30 400 771.89 cm /Im0 Do Q", text);
        Assert.Equal(2, Regex.Matches(text, @"/XObject << /Im0 6 0 R >>").Count); // both pages reference it
        Assert.Equal(["Rechnung"], PdfText.Lines(bytes));
        Assert.True(bytes.AsSpan().IndexOf(logo) > 0, "the JPEG bytes are copied unchanged");
    }

    [Fact]
    public void Xref_offsets_stay_valid_with_binary_image_data()
    {
        var bytes = new SimplePdf { Footer = "Test" }.ImageBlock(Jpeg(300, 300), 100, 100).Text("Unter dem Bild").ToBytes();
        var pdf = Latin1(bytes);
        Assert.StartsWith("%PDF-1.4\n%", pdf);
        var startXref = int.Parse(Regex.Match(pdf, @"startxref\n(\d+)\n%%EOF").Groups[1].Value, CultureInfo.InvariantCulture);
        var entries = Regex.Matches(pdf[startXref..], @"(\d{10}) 00000 n \n");
        Assert.NotEmpty(entries);
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj\n", pdf[int.Parse(entries[i].Groups[1].Value, CultureInfo.InvariantCulture)..]);
        foreach (Match stream in Regex.Matches(pdf, @"/Length (\d+) >>\nstream\n"))
            Assert.StartsWith("\nendstream", pdf[(stream.Index + stream.Length + int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture))..]);
    }

    [Fact]
    public void Image_block_keeps_the_aspect_ratio_aligns_right_and_moves_the_cursor()
    {
        var pdf = new SimplePdf();
        var bytes = pdf.ImageBlock(Jpeg(400, 100), maxWidth: 170, maxHeight: 62, align: PdfAlign.Right).Text("Darunter").ToBytes();
        var text = Latin1(bytes);
        // 400×100 into 170×62 → 170×42.5, right edge at the right margin (595.28 − 50), top at the top margin.
        Assert.Contains("q 170 0 0 42.5 375.28 749.39 cm /Im0 Do Q", text);
        // The next line starts below the image: baseline = 841.89 − 50 − 42.5 − 10.
        Assert.Contains("BT /F1 10 Tf 50 739.39 Td (Darunter) Tj ET", text);
        Assert.Equal((170, 42.5), SimplePdf.FitImage(Jpeg(400, 100), 170, 62));
    }

    [Fact]
    public void An_image_that_does_not_fit_starts_a_new_page()
    {
        var pdf = new SimplePdf();
        for (var i = 0; i < 52; i++)
            pdf.Text($"Zeile {i}");
        pdf.ImageBlock(Jpeg(100, 100), 200, 200);
        Assert.Equal(2, pdf.PageCount);
    }

    [Fact]
    public void Only_jpeg_can_be_embedded()
    {
        var pdf = new SimplePdf();
        Assert.Throws<ArgumentException>(() => pdf.Image(TestImages.Png(10, 10), 0, 0, 10, 10));
        Assert.Throws<ArgumentException>(() => SimplePdf.JpegSize([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Image(Jpeg(10, 10), 0, 0, 0, 10));
    }

    [Fact]
    public void Footer_lines_appear_on_every_page_and_content_stays_above_them()
    {
        var pdf = new SimplePdf { Footer = "Dok", FooterLines = ["Firma GmbH · Weg 1, 10115 Berlin", "Bankverbindung: IBAN DE89 3704 0044 0532 0130 00", " "] };
        for (var i = 0; i < 120; i++)
            pdf.Text($"Zeile {i}");
        var bytes = pdf.ToBytes();
        var lines = PdfText.Lines(bytes);

        Assert.True(pdf.PageCount >= 3);
        Assert.Equal(pdf.PageCount, lines.Count(l => l == "Firma GmbH · Weg 1, 10115 Berlin"));
        Assert.Equal(pdf.PageCount, lines.Count(l => l == "Bankverbindung: IBAN DE89 3704 0044 0532 0130 00"));
        // Lowest content baseline is above the footer block (2 lines at 7 pt above the page footer).
        var lowest = Regex.Matches(Latin1(bytes), @"BT /F1 10 Tf 50 ([\d.]+) Td \(Zeile").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Min();
        Assert.True(lowest > 25 + 12 + 9.1 + 7, $"lowest baseline {lowest}");
    }
}

/// <summary>The tenant letterhead on POS invoices and the Ankaufbeleg.</summary>
public class TenantDocumentLetterheadTests
{
    private static readonly DocumentLetterhead Letterhead = new(
        new SkiaImageCodec().ToJpeg(TestImages.Png(300, 100), new ImageDecodeLimits(5 * 1024 * 1024)).Value.Bytes,
        ["Handy Shop GmbH · Hauptstraße 1, 10115 Berlin", "Bankverbindung: Commerzbank · IBAN DE89 3704 0044 0532 0130 00"]);

    private static PosInvoiceDocument Invoice() => new("Rechnung", "RE-2026-000001", new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 23),
        new SellerInfo("Handy Shop", "Handy Shop GmbH", "Hauptstraße 1", "10115", "Berlin", "DE", "DE123456789"),
        new InvoiceCustomer("Erika Mustermann", null, "Lindenweg 3", "10405", "Berlin", "DE", null), "Kasse 1", 7,
        [], [], 0, 0, 0, 0, ["Bar 0,00 €"], []);

    [Fact]
    public void Pos_invoice_carries_the_logo_and_footer_lines()
    {
        var pdf = PosInvoicePdf.Render(Invoice(), Letterhead);
        Assert.Equal(1, PdfText.ImageCount(pdf));
        Assert.Equal(1, PdfText.ImageDraws(pdf));
        var lines = PdfText.Lines(pdf);
        Assert.Contains("RE-2026-000001", lines);
        Assert.Contains("Bankverbindung: Commerzbank · IBAN DE89 3704 0044 0532 0130 00", lines);
    }

    [Fact]
    public void Without_letterhead_documents_stay_as_before()
    {
        var pdf = PosInvoicePdf.Render(Invoice());
        Assert.Equal(0, PdfText.ImageCount(pdf));
        Assert.All(pdf, b => Assert.True(b < 128));
    }

    [Fact]
    public void Ankaufbeleg_carries_the_logo_and_footer_lines()
    {
        var content = new AnkaufbelegContent("Ankaufbeleg", "Ankauf eines Gebrauchtgeräts", "AB-2026-000001", "23.10.2026",
            [new BelegSection("Gerät", [new BelegField("IMEI", "490154203237518")], [])], ["Verkäufer", "Käufer"], "Ankaufbeleg AB-2026-000001");
        var pdf = AnkaufbelegPdf.Render(content, Letterhead);
        Assert.Equal(1, PdfText.ImageCount(pdf));
        var lines = PdfText.Lines(pdf);
        Assert.Contains("490154203237518", lines);
        Assert.Contains("Handy Shop GmbH · Hauptstraße 1, 10115 Berlin", lines);
    }
}
