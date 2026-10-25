using HandyNet.Modules.Ankauf.Domain;
using HandyNet.SharedKernel.Documents;

namespace HandyNet.Modules.Ankauf.Application;

/// <summary>Lays out <see cref="AnkaufbelegContent"/> on A4 pages.</summary>
internal static class AnkaufbelegPdf
{
    private const double LabelWidth = 140;
    private const double SignatureHeight = 60;

    /// <param name="letterhead">Logo and footer lines of the dealer (its current company profile, ADR-0014).</param>
    /// <param name="sellerSignatureJpeg">The seller's on-screen signature converted to JPEG (SimplePdf embeds JPEG only).</param>
    public static byte[] Render(AnkaufbelegContent content, DocumentLetterhead? letterhead = null, byte[]? sellerSignatureJpeg = null)
    {
        letterhead ??= DocumentLetterhead.None;
        var pdf = new SimplePdf { Title = $"{content.Title} {content.DocumentNumber}", Footer = content.Footer, FooterLines = letterhead.FooterLines };
        letterhead.ApplyLogo(pdf);

        pdf.Text(content.Title, 20, PdfFont.Bold)
            .Text(content.Subtitle, 10)
            .Space(6)
            .Rule(1)
            .Field("Belegnummer", content.DocumentNumber, LabelWidth)
            .Field("Ankaufdatum", content.PurchaseDate, LabelWidth)
            .Rule();

        foreach (var section in content.Sections)
        {
            pdf.Space(8).KeepTogether(60).Text(section.Heading, 12, PdfFont.Bold).Space(2);
            foreach (var field in section.Fields)
                pdf.Field(field.Label, field.Value, LabelWidth);
            if (section.Fields.Count > 0 && section.Paragraphs.Count > 0)
                pdf.Space(4);
            foreach (var paragraph in section.Paragraphs)
                pdf.Text(paragraph);
        }

        // Signature block stays on one page; the seller's electronic signature (if any) stands above the seller's line.
        var half = pdf.ContentWidth / 2;
        pdf.Space(12).KeepTogether(SignatureHeight + 40);
        if (sellerSignatureJpeg is not null)
            pdf.ImageBlock(sellerSignatureJpeg, half - 20, SignatureHeight);
        else
            pdf.Space(40);
        pdf.Rules(0.5, (0, half - 20), (half + 20, pdf.ContentWidth))
            .Columns(9, PdfFont.Regular, (0, content.SignatureLabels[0]), (half + 20, content.SignatureLabels[1]));

        return pdf.ToBytes();
    }
}
