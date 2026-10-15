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

    [GeneratedRegex(@"stream\n(.*?)\nendstream", RegexOptions.Singleline)]
    private static partial Regex StreamPattern();
}
