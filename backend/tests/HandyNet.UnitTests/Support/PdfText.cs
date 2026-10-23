using System.Text;
using System.Text.RegularExpressions;

namespace HandyNet.Tests.Support;

/// <summary>
/// Reads back the text of PDFs written by <c>SimplePdf</c> (uncompressed streams, literal strings in WinAnsiEncoding),
/// independently of the writer's code: one entry per <c>Tj</c> operator, in drawing order.
/// </summary>
public static partial class PdfText
{
    private static readonly Encoding WinAnsi = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    public static IReadOnlyList<string> Lines(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var lines = new List<string>();
        foreach (Match stream in StreamPattern().Matches(text))
        {
            if (IsImage(text, stream.Index))
                continue; // binary JPEG data of an image XObject, not a content stream
            var content = stream.Groups[1].Value;
            for (var i = 0; i < content.Length; i++)
            {
                if (content[i] != '(')
                    continue;
                var bytes = new List<byte>();
                for (i++; content[i] != ')'; i++)
                {
                    if (content[i] != '\\')
                    {
                        bytes.Add((byte)content[i]);
                        continue;
                    }
                    i++;
                    if (char.IsAsciiDigit(content[i]))
                    {
                        bytes.Add(Convert.ToByte(content.Substring(i, 3), 8));
                        i += 2;
                    }
                    else
                    {
                        bytes.Add((byte)content[i]);
                    }
                }
                lines.Add(WinAnsi.GetString(bytes.ToArray()));
            }
        }
        return lines;
    }

    public static string All(byte[] pdf) => string.Join("\n", Lines(pdf));

    /// <summary>Number of embedded JPEG images (XObjects with /Subtype /Image and /Filter /DCTDecode).</summary>
    public static int ImageCount(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        return StreamPattern().Matches(text).Count(m => IsImage(text, m.Index) && Dictionary(text, m.Index).Contains("/Filter /DCTDecode"));
    }

    /// <summary>Number of times content streams draw an image (<c>/ImN Do</c>).</summary>
    public static int ImageDraws(byte[] pdf) => DrawPattern().Matches(Encoding.Latin1.GetString(pdf)).Count;

    private static string Dictionary(string text, int streamIndex)
    {
        var start = text.LastIndexOf(" obj\n", streamIndex, StringComparison.Ordinal);
        return start < 0 ? "" : text[start..streamIndex];
    }

    private static bool IsImage(string text, int streamIndex) => Dictionary(text, streamIndex).Contains("/Subtype /Image");

    [GeneratedRegex(@"/Im\d+ Do")]
    private static partial Regex DrawPattern();

    [GeneratedRegex(@"stream\n(.*?)\nendstream", RegexOptions.Singleline)]
    private static partial Regex StreamPattern();
}
