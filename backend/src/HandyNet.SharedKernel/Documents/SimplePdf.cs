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

/// <summary>Horizontal placement of a flowing block (<see cref="SimplePdf.ImageBlock"/>) within the content width.</summary>
public enum PdfAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Small dependency-free PDF writer for generated business documents (labels, receipts, invoices): text lines with word
/// wrap in Helvetica / Helvetica-Bold, horizontal rules, JPEG images, automatic page breaks, an optional footer with page
/// numbers and optional footer lines (letterhead data such as bank details) on every page.
/// Text is encoded as WinAnsiEncoding (Windows-1252), so German umlauts, ß and € render with the standard fonts;
/// characters outside that code page are written as "?". Without images the output is plain ASCII (non-ASCII bytes are
/// octal-escaped); image streams are binary.
/// Not a layout engine: callers place content top to bottom.
/// </summary>
/// <remarks>
/// <para><b>Images</b> (ADR-0014). Only baseline or progressive JPEG is embedded, as an <c>/XObject /Image</c> with
/// <c>/Filter /DCTDecode</c> (the JPEG bytes are copied as they are; grayscale, RGB and CMYK are supported). Convert PNG,
/// WebP or untrusted uploads first with <see cref="Imaging.IImageCodec.ToJpeg"/> — that also strips metadata. The same
/// byte array used several times (e.g. a logo on every page) is embedded once.</para>
/// <list type="bullet">
/// <item><see cref="Image"/> places an image at an absolute position (top-left corner, measured from the top-left corner
/// of the page, in points) on the current page without moving the text cursor — e.g. a logo in the corner.</item>
/// <item><see cref="ImageBlock"/> places an image in the flow at the cursor (aspect ratio kept, fitted into a box),
/// starts a new page if it does not fit and moves the cursor below it — e.g. a customer signature above a signature
/// line: <c>pdf.ImageBlock(signatureJpeg, maxWidth: 180, maxHeight: 60).Rules(0.5, (0, 200))</c>.</item>
/// <item><see cref="FitImage"/> / <see cref="JpegSize"/> compute sizes for custom layouts.</item>
/// </list>
/// </remarks>
public sealed class SimplePdf
{
    public const double A4Width = 595.28;
    public const double A4Height = 841.89;

    private const double LineSpacing = 1.3;
    private const double FooterSize = 8;

    /// <summary>Font size of <see cref="FooterLines"/>.</summary>
    public const double FooterLineSize = 7;

    private static readonly Encoding WinAnsi = CodePagesEncodingProvider.Instance.GetEncoding(
        1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"))!;

    private readonly List<StringBuilder> _pages = [];
    private readonly List<HashSet<int>> _pageImages = [];
    private readonly List<JpegImage> _images = [];
    private StringBuilder _page = null!;
    private HashSet<int> _imagesOnPage = null!;
    private double _y;
    private List<string>? _wrappedFooterLines;

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

    /// <summary>
    /// Small lines printed at the bottom of every page, above <see cref="Footer"/> (e.g. address, managing director,
    /// register and bank details of the issuer). Wrapped to the content width; content stops above them.
    /// </summary>
    public IReadOnlyList<string> FooterLines { get; init; } = [];

    public int PageCount => _pages.Count;

    /// <summary>Lowest y (from the bottom of the page) that flowing content may reach.</summary>
    private double Bottom => Math.Max(Margin, FooterBlockTop() + 8);

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
        if (_y < Bottom)
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

    /// <summary>
    /// Draws a JPEG at an absolute position on the current page: (<paramref name="x"/>, <paramref name="y"/>) is the
    /// image's top-left corner measured from the page's top-left corner, all values in points (1/72 inch). The text
    /// cursor does not move. Throws <see cref="ArgumentException"/> when <paramref name="jpeg"/> is not a JPEG.
    /// </summary>
    public SimplePdf Image(byte[] jpeg, double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image width and height must be positive.");
        var index = Register(jpeg);
        _imagesOnPage.Add(index);
        _page.Append(Invariant($"q {width:0.##} 0 0 {height:0.##} {x:0.##} {PageHeight - y - height:0.##} cm /Im{index} Do Q\n"));
        return this;
    }

    /// <summary>
    /// Draws a JPEG in the flow at the cursor, scaled to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/>
    /// points (aspect ratio kept, never wider than the content width), aligned within the content width; starts a new page
    /// if it does not fit and moves the cursor below the image.
    /// </summary>
    public SimplePdf ImageBlock(byte[] jpeg, double maxWidth, double maxHeight, PdfAlign align = PdfAlign.Left, double indent = 0)
    {
        var (width, height) = FitImage(jpeg, Math.Min(maxWidth, ContentWidth - indent), maxHeight);
        KeepTogether(height);
        var x = align switch
        {
            PdfAlign.Right => Margin + ContentWidth - width,
            PdfAlign.Center => Margin + indent + (ContentWidth - indent - width) / 2,
            _ => Margin + indent,
        };
        Image(jpeg, x, PageHeight - _y, width, height);
        _y -= height;
        return this;
    }

    /// <summary>Size in points of a JPEG scaled to fit the box (aspect ratio kept; may enlarge small images).</summary>
    public static (double Width, double Height) FitImage(byte[] jpeg, double maxWidth, double maxHeight)
    {
        var (w, h) = JpegSize(jpeg);
        var scale = Math.Min(maxWidth / w, maxHeight / h);
        return (w * scale, h * scale);
    }

    /// <summary>Pixel size of a JPEG (from its SOF marker). Throws <see cref="ArgumentException"/> for anything else.</summary>
    public static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        var info = JpegImage.Parse(jpeg);
        return (info.Width, info.Height);
    }

    /// <summary>Starts a new page unless at least <paramref name="height"/> points remain on the current one.</summary>
    public SimplePdf KeepTogether(double height)
    {
        if (_y - height < Bottom)
            NewPage();
        return this;
    }

    public SimplePdf NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        _imagesOnPage = [];
        _pageImages.Add(_imagesOnPage);
        _y = PageHeight - Margin;
        return this;
    }

    public byte[] ToBytes()
    {
        const int firstImageObject = 6;
        var firstPageObject = firstImageObject + _images.Count;
        var kids = string.Join(' ', _pages.Select((_, i) => $"{firstPageObject + 2 * i} 0 R"));
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
            Ascii(Title is null ? "<< /Producer (SEHA) >>" : $"<< /Producer (SEHA) /Title {Utf16Hex(Title)} >>"),
        };
        foreach (var image in _images)
            objects.Add(image.ToObject());

        for (var i = 0; i < _pages.Count; i++)
        {
            var content = new StringBuilder(_pages[i].ToString());
            AppendFooterLines(content);
            if (Footer is not null)
                AppendFooter(content, i + 1, _pages.Count);
            var xObjects = _pageImages[i].Count == 0
                ? ""
                : " /XObject << " + string.Join(' ', _pageImages[i].Order().Select(n => $"/Im{n} {firstImageObject + n} 0 R")) + " >>";
            objects.Add(Ascii(Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}] ")
                              + $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >>{xObjects} >> /Contents {firstPageObject + 2 * i + 1} 0 R >>"));
            objects.Add(Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
        }

        using var pdf = new MemoryStream();
        Write(pdf, "%PDF-1.4\n");
        if (_images.Count > 0)
            pdf.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // marks the file as binary (PDF 7.5.2)
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write(pdf, $"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            Write(pdf, "\nendobj\n");
        }

        var xref = pdf.Position;
        var tail = new StringBuilder("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            tail.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        tail.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R /Info 5 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        Write(pdf, tail.ToString());
        return pdf.ToArray();
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

    private int Register(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        for (var i = 0; i < _images.Count; i++)
        {
            if (ReferenceEquals(_images[i].Data, jpeg))
                return i;
        }
        _images.Add(JpegImage.Parse(jpeg));
        return _images.Count - 1;
    }

    private double NextLine(double size)
    {
        var leading = size * LineSpacing;
        if (_y - leading < Bottom)
            NewPage();
        var baseline = _y - size;
        _y -= leading;
        return baseline;
    }

    private void DrawText(double x, double baseline, string text, double size, PdfFont font) =>
        _page.Append(Invariant($"BT /{(font == PdfFont.Bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {baseline:0.##} Td "))
            .Append(Literal(text)).Append(" Tj ET\n");

    private List<string> WrappedFooterLines() =>
        _wrappedFooterLines ??= FooterLines.Where(l => !string.IsNullOrWhiteSpace(l))
            .SelectMany(l => Wrap(SingleLine(l), FooterLineSize, PdfFont.Regular, ContentWidth)).ToList();

    private double FooterLineHeight => FooterLineSize * LineSpacing;

    /// <summary>Baseline of the lowest footer line.</summary>
    private double FooterLinesBaseline => Margin / 2 + (Footer is null ? 0 : FooterSize + 4);

    /// <summary>Top of the footer-line block (0 when there are none).</summary>
    private double FooterBlockTop()
    {
        var lines = WrappedFooterLines();
        return lines.Count == 0 ? 0 : FooterLinesBaseline + (lines.Count - 1) * FooterLineHeight + FooterLineSize + 4;
    }

    private void AppendFooterLines(StringBuilder content)
    {
        var lines = WrappedFooterLines();
        if (lines.Count == 0)
            return;
        var top = FooterBlockTop();
        content.Append(Invariant($"0.5 w {Margin:0.##} {top:0.##} m {Margin + ContentWidth:0.##} {top:0.##} l S\n"));
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = FooterLinesBaseline + (lines.Count - 1 - i) * FooterLineHeight;
            content.Append(Invariant($"BT /F1 {FooterLineSize:0.##} Tf {Margin:0.##} {baseline:0.##} Td ")).Append(Literal(lines[i])).Append(" Tj ET\n");
        }
    }

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

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static void Write(Stream stream, string s) => stream.Write(Encoding.ASCII.GetBytes(s));

    /// <summary>A JPEG as an image XObject (DCTDecode; the file's bytes are the stream).</summary>
    private sealed record JpegImage(byte[] Data, int Width, int Height, int Components, bool Adobe)
    {
        public byte[] ToObject()
        {
            var colorSpace = Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
            // Adobe CMYK JPEGs store inverted values.
            var decode = Components == 4 && Adobe ? " /Decode [1 0 1 0 1 0 1 0]" : "";
            var head = Ascii(Invariant($"<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} /ColorSpace {colorSpace} ")
                             + $"/BitsPerComponent 8{decode} /Filter /DCTDecode /Length {Data.Length} >>\nstream\n");
            var tail = Ascii("\nendstream");
            var result = new byte[head.Length + Data.Length + tail.Length];
            head.CopyTo(result, 0);
            Data.CopyTo(result, head.Length);
            tail.CopyTo(result, head.Length + Data.Length);
            return result;
        }

        /// <summary>Reads size and components from the first SOF marker; rejects anything that is not an 8-bit JPEG.</summary>
        public static JpegImage Parse(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                throw new ArgumentException("Only JPEG images can be embedded; convert other formats with IImageCodec.ToJpeg first.", nameof(data));
            var adobe = false;
            var i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF)
                    break;
                var marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }                    // fill byte
                if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // no length
                if (marker is 0xD9 or 0xDA)
                    break;                                                // end of image / start of scan before a frame
                var length = (data[i + 2] << 8) | data[i + 3];
                if (length < 2 || i + 2 + length > data.Length)
                    break;
                if (marker == 0xEE && length >= 7 && data.AsSpan(i + 4, 5).SequenceEqual("Adobe"u8))
                    adobe = true;
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && length >= 8)
                {
                    var precision = data[i + 4];
                    var height = (data[i + 5] << 8) | data[i + 6];
                    var width = (data[i + 7] << 8) | data[i + 8];
                    var components = data[i + 9];
                    if (precision != 8 || width == 0 || height == 0 || components is not (1 or 3 or 4))
                        throw new ArgumentException("Unsupported JPEG (8-bit grayscale, RGB or CMYK only).", nameof(data));
                    return new JpegImage(data, width, height, components, adobe);
                }
                i += 2 + length;
            }
            throw new ArgumentException("The JPEG has no readable frame header.", nameof(data));
        }
    }

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
