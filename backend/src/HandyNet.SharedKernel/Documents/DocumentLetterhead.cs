namespace HandyNet.SharedKernel.Documents;

/// <summary>
/// What a tenant puts on its own documents (POS invoices, Ankaufbeleg, …): the logo as a JPEG ready for
/// <see cref="SimplePdf.Image"/> and the footer lines (address and contact, managing director and register, bank details).
/// Built by the Tenancy module from the company profile (ADR-0014); <see cref="None"/> when nothing is configured or the
/// data cannot be loaded.
/// </summary>
public sealed record DocumentLetterhead(byte[]? LogoJpeg, IReadOnlyList<string> FooterLines)
{
    /// <summary>Largest logo box on A4 documents, in points (≈ 60 × 22 mm).</summary>
    public const double LogoMaxWidth = 170;
    public const double LogoMaxHeight = 62;

    public static readonly DocumentLetterhead None = new(null, []);

    /// <summary>Draws the logo right-aligned at the cursor (top of the first page) and leaves a small gap below it.</summary>
    public SimplePdf ApplyLogo(SimplePdf pdf)
    {
        if (LogoJpeg is null)
            return pdf;
        return pdf.ImageBlock(LogoJpeg, LogoMaxWidth, LogoMaxHeight, PdfAlign.Right).Space(8);
    }
}
