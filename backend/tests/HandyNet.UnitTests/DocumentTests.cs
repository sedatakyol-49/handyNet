using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HandyNet.SharedKernel.Documents;
using HandyNet.Tests.Support;

namespace HandyNet.UnitTests;

public class SimplePdfTests
{
    private static string Ascii(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Writes_a_valid_header_xref_table_and_trailer()
    {
        var bytes = new SimplePdf { Title = "Prüfung" }.Text("Hallo").Rule().Text("Welt", 14, PdfFont.Bold).ToBytes();
        var pdf = Ascii(bytes);

        Assert.StartsWith("%PDF-1.4\n", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        Assert.All(bytes, b => Assert.True(b < 128, "the file is plain ASCII"));

        // startxref points at the xref table, and every xref entry points at its object.
        var startXref = int.Parse(Regex.Match(pdf, @"startxref\n(\d+)\n%%EOF").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", pdf[startXref..]);
        var header = Regex.Match(pdf[startXref..], @"xref\n0 (\d+)\n");
        var count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        var entries = Regex.Matches(pdf[startXref..], @"(\d{10}) 00000 n \n");
        Assert.Equal(count - 1, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i].Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i + 1} 0 obj\n", pdf[offset..]);
        }
        Assert.Contains($"/Size {count} /Root 1 0 R", pdf);

        // Declared stream lengths match the actual content.
        foreach (Match stream in Regex.Matches(pdf, @"/Length (\d+) >>\nstream\n"))
        {
            var length = int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture);
            var start = stream.Index + stream.Length;
            Assert.StartsWith("\nendstream", pdf[(start + length)..]);
        }
    }

    [Fact]
    public void Umlauts_sharp_s_and_euro_survive_a_round_trip_in_win_ansi_encoding()
    {
        const string text = "Grüße aus Köln: Äpfel, Öl, Übergröße – 1.234,50 € (§ 25a)";
        var bytes = new SimplePdf().Text(text).ToBytes();
        var pdf = Ascii(bytes);

        Assert.Equal(2, Regex.Matches(pdf, "/Encoding /WinAnsiEncoding").Count); // Helvetica and Helvetica-Bold
        Assert.Contains(@"Gr\374\337e", pdf);  // ü = 0xFC, ß = 0xDF in Windows-1252
        Assert.Contains(@"\200 \(\247 25a\)", pdf); // € = 0x80, § = 0xA7, parentheses escaped
        Assert.Equal([text], PdfText.Lines(bytes));
    }

    [Fact]
    public void Characters_outside_windows_1252_become_question_marks()
    {
        var bytes = new SimplePdf().Text("Łódź ✓").ToBytes();
        Assert.Equal(["?ód? ?"], PdfText.Lines(bytes));
    }

    [Fact]
    public void Long_content_flows_onto_further_pages_with_page_numbers()
    {
        var pdf = new SimplePdf { Footer = "Testdokument" };
        for (var i = 1; i <= 150; i++)
            pdf.Text($"Zeile {i}");
        var bytes = pdf.ToBytes();
        var text = Ascii(bytes);

        Assert.True(pdf.PageCount >= 3);
        Assert.Contains($"/Count {pdf.PageCount}", text);
        Assert.Equal(pdf.PageCount, Regex.Matches(text, @"/Type /Page /Parent").Count);
        var lines = PdfText.Lines(bytes);
        Assert.Equal(Enumerable.Range(1, 150).Select(i => $"Zeile {i}"), lines.Where(l => l.StartsWith("Zeile", StringComparison.Ordinal)));
        Assert.Contains($"Seite 1 von {pdf.PageCount}", lines);
        Assert.Contains($"Seite {pdf.PageCount} von {pdf.PageCount}", lines);
        Assert.Equal(pdf.PageCount, lines.Count(l => l == "Testdokument"));
    }

    [Fact]
    public void Long_paragraphs_are_wrapped_within_the_content_width()
    {
        var words = string.Join(' ', Enumerable.Repeat("Eigentumserklärung des Verkäufers", 30));
        var pdf = new SimplePdf();
        var bytes = pdf.Text(words, 10).ToBytes();
        var lines = PdfText.Lines(bytes);

        Assert.True(lines.Count > 5);
        Assert.All(lines, l => Assert.True(SimplePdf.MeasureWidth(l, 10, PdfFont.Regular) <= pdf.ContentWidth, l));
        Assert.Equal(words, string.Join(' ', lines));
    }
}
