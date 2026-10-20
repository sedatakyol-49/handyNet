using System.Globalization;
using System.Text;

namespace HandyNet.SharedKernel.Documents;

public enum PdfFont
{
    /// <summary>Helvetica (standard Type 1 font, not embedded).</summary>
    Regular,

    /// <summary>Helvetica-Bold (standard Type 1 font, not embedded).</summary>
    Bold,
}

/// <summary>
/// Small dependency-free PDF writer for generated business documents (labels, receipts): text lines with word wrap in
/// Helvetica / Helvetica-Bold, horizontal rules, automatic page breaks and an optional footer with page numbers.
/// Text is encoded as WinAnsiEncoding (Windows-1252), so German umlauts, ß and € render with the standard fonts;
/// characters outside that code page are written as "?". Output is plain ASCII (non-ASCII bytes are octal-escaped).
/// Not a layout engine: callers place content top to bottom.
/// </summary>
public sealed class SimplePdf
{
    public const double A4Width = 595.28;
    public const double A4Height = 841.89;

    private const double LineSpacing = 1.3;
    private const double FooterSize = 8;

    private static readonly Encoding WinAnsi = CodePagesEncodingProvider.Instance.GetEncoding(
        1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"))!;

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _page = null!;
    private double _y;

    public SimplePdf(double pageWidth = A4Width, double pageHeight = A4Height, double margin = 50)
    {
        if (pageWidth <= 2 * margin || pageHeight <= 2 * margin)
            throw new ArgumentException("The margins leave no room for content.");
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        Margin = margin;
        NewPage();
    }

    public double PageWidth { get; }
    public double PageHeight { get; }
    public double Margin { get; }
    public double ContentWidth => PageWidth - 2 * Margin;

    /// <summary>Document title (PDF metadata).</summary>
    public string? Title { get; init; }

    /// <summary>When set, printed at the bottom of every page together with "Seite i von n".</summary>
    public string? Footer { get; init; }

    public int PageCount => _pages.Count;

    /// <summary>Writes text, wrapping at word boundaries; explicit line breaks ("\n") are kept.</summary>
    public SimplePdf Text(string text, double size = 10, PdfFont font = PdfFont.Regular, double indent = 0)
    {
        foreach (var line in Wrap(text, size, font, ContentWidth - indent))
        {
            var baseline = NextLine(size);
            DrawText(Margin + indent, baseline, line, size, font);
        }
        return this;
    }

    /// <summary>A label in the left column and a (wrapped) value next to it.</summary>
    public SimplePdf Field(string label, string value, double labelWidth = 150, double size = 10)
    {
        var lines = Wrap(value, size, PdfFont.Regular, ContentWidth - labelWidth);
        KeepTogether(Math.Min(lines.Count, 3) * size * LineSpacing);
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = NextLine(size);
            if (i == 0)
                DrawText(Margin, baseline, label, size, PdfFont.Bold);
            DrawText(Margin + labelWidth, baseline, lines[i], size, PdfFont.Regular);
        }
        return this;
    }

    /// <summary>One line with cells at x offsets (relative to the left margin). Cells are not wrapped.</summary>
    public SimplePdf Columns(double size, PdfFont font, params (double X, string Text)[] cells)
    {
        var baseline = NextLine(size);
        foreach (var (x, text) in cells)
            DrawText(Margin + x, baseline, SingleLine(text), size, font);
        return this;
    }

    public SimplePdf Space(double points)
    {
        _y -= points;
        if (_y < Margin)
            NewPage();
        return this;
    }

    /// <summary>A horizontal rule across the content width.</summary>
    public SimplePdf Rule(double thickness = 0.5) => Rules(thickness, (0, ContentWidth));

    /// <summary>Horizontal line segments on one line (x offsets relative to the left margin), e.g. signature lines.</summary>
    public SimplePdf Rules(double thickness, params (double From, double To)[] segments)
    {
        KeepTogether(8);
        _y -= 4;
        foreach (var (from, to) in segments)
            _page.Append(Invariant($"{thickness:0.##} w {Margin + from:0.##} {_y:0.##} m {Margin + to:0.##} {_y:0.##} l S\n"));
        _y -= 4;
        return this;
    }

    /// <summary>Starts a new page unless at least <paramref name="height"/> points remain on the current one.</summary>
    public SimplePdf KeepTogether(double height)
    {
        if (_y - height < Margin)
            NewPage();
        return this;
    }

    public SimplePdf NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        _y = PageHeight - Margin;
        return this;
    }

    public byte[] ToBytes()
    {
        const int firstPageObject = 6;
        var kids = string.Join(' ', _pages.Select((_, i) => $"{firstPageObject + 2 * i} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            Title is null ? "<< /Producer (SEHA) >>" : $"<< /Producer (SEHA) /Title {Utf16Hex(Title)} >>",
        };

        for (var i = 0; i < _pages.Count; i++)
        {
            var content = new StringBuilder(_pages[i].ToString());
            if (Footer is not null)
                AppendFooter(content, i + 1, _pages.Count);
            objects.Add(Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}] ")
                        + $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {firstPageObject + 2 * i + 1} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        // Everything above is ASCII, so string lengths are byte offsets.
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R /Info 5 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Approximate rendered width in points (Helvetica metrics; accented letters measured as their base letter).</summary>
    public static double MeasureWidth(string text, double size, PdfFont font)
    {
        var widths = font == PdfFont.Bold ? BoldWidths : RegularWidths;
        double units = 0;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
            units += c switch
            {
                >= ' ' and <= '~' => widths[c - ' '],
                'ß' => 611,
                '€' or '§' or '–' => 556,
                '—' => 1000,
                _ => BaseLetter(c) is var b and >= ' ' and <= '~' ? widths[b - ' '] : 556,
            };
        return units * size / 1000;
    }

    /// <summary>"ä" → "a", "Ü" → "U"; other characters unchanged.</summary>
    private static char BaseLetter(char c) => char.IsLetter(c) ? c.ToString().Normalize(NormalizationForm.FormD)[0] : c;

    private double NextLine(double size)
    {
        var leading = size * LineSpacing;
        if (_y - leading < Margin)
            NewPage();
        var baseline = _y - size;
        _y -= leading;
        return baseline;
    }

    private void DrawText(double x, double baseline, string text, double size, PdfFont font) =>
        _page.Append(Invariant($"BT /{(font == PdfFont.Bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {baseline:0.##} Td "))
            .Append(Literal(text)).Append(" Tj ET\n");

    private void AppendFooter(StringBuilder content, int page, int total)
    {
        var baseline = Margin / 2;
        var pageText = $"Seite {page} von {total}";
        var pageWidth = MeasureWidth(pageText, FooterSize, PdfFont.Regular);
        var footer = SingleLine(Footer!);
        content.Append(Invariant($"BT /F1 {FooterSize:0.##} Tf {Margin:0.##} {baseline:0.##} Td ")).Append(Literal(footer)).Append(" Tj ET\n");
        content.Append(Invariant($"BT /F1 {FooterSize:0.##} Tf {Margin + ContentWidth - pageWidth:0.##} {baseline:0.##} Td "))
            .Append(Literal(pageText)).Append(" Tj ET\n");
    }

    private static List<string> Wrap(string text, double size, PdfFont font, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : $"{current} {word}";
                if (MeasureWidth(candidate, size, font) <= width)
                {
                    current.Clear().Append(candidate);
                    continue;
                }

                if (current.Length > 0)
                    lines.Add(current.ToString());
                current.Clear();

                // A single word wider than the line is split by characters.
                foreach (var c in word)
                {
                    if (current.Length > 0 && MeasureWidth(current.ToString() + c, size, font) > width)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }
                    current.Append(c);
                }
            }
            lines.Add(current.ToString());
        }
        return lines;
    }

    private static string SingleLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    /// <summary>A PDF literal string in WinAnsiEncoding; delimiters, control and non-ASCII bytes are escaped.</summary>
    private static string Literal(string text)
    {
        var bytes = WinAnsi.GetBytes(text.Normalize(NormalizationForm.FormC));
        var s = new StringBuilder(bytes.Length + 2).Append('(');
        foreach (var b in bytes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
                s.Append('\\').Append((char)b);
            else if (b is < 32 or > 126)
                s.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else
                s.Append((char)b);
        }
        return s.Append(')').ToString();
    }

    private static string Utf16Hex(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    // Adobe AFM widths for ' ' (32) to '~' (126).
    private static readonly int[] RegularWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] BoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];
}
