using System.Text.RegularExpressions;

namespace HandyNet.ArchitectureTests;

/// <summary>
/// Custom validator rules (Must / Matches / Equal / NotEqual with their own text) have no built-in error code, so the UI
/// could only show the English server message. Every such rule must carry a stable <c>validation.*</c> code.
/// </summary>
public class ValidatorErrorCodeTests
{
    private static readonly Regex CustomRule = new(@"\.(Must|Matches|Equal|NotEqual|Custom)\(", RegexOptions.Compiled);

    [Fact]
    public void Every_custom_validator_rule_has_an_error_code()
    {
        var src = Path.Combine(FindBackendRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var text = File.ReadAllText(file);
            var from = 0;
            while (true)
            {
                var start = text.IndexOf("RuleFor", from, StringComparison.Ordinal);
                if (start < 0) break;
                var end = StatementEnd(text, start);
                var statement = text[start..end];
                from = end;
                var matches = CustomRule.Matches(statement);
                for (var i = 0; i < matches.Count; i++)
                {
                    var segmentEnd = i + 1 < matches.Count ? matches[i + 1].Index : statement.Length;
                    var segment = statement[matches[i].Index..segmentEnd];
                    // Equal/NotEqual keep their built-in code unless the rule overrides the text (then the specifics are lost).
                    var builtIn = matches[i].Groups[1].Value is "Equal" or "NotEqual" &&
                                  !segment.Contains("WithMessage(", StringComparison.Ordinal);
                    if (!builtIn && !segment.Contains("WithErrorCode(", StringComparison.Ordinal))
                    {
                        var line = text[..start].Count(c => c == '\n') + 1;
                        missing.Add($"{Path.GetRelativePath(src, file)}:{line} {matches[i].Value}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, "Add .WithErrorCode(\"validation.<name>\") to:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_error_code_is_translated_in_all_locales()
    {
        var backend = FindBackendRoot();
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(backend, "src"), "*.cs", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "WithErrorCode\\(\"validation\\.([A-Za-z]+)\""))
                codes.Add(m.Groups[1].Value);
        Assert.NotEmpty(codes);

        var locales = Path.GetFullPath(Path.Combine(backend, "..", "frontend", "src", "app", "core", "i18n", "locales"));
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(locales, "*.json"))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var validation = doc.RootElement.GetProperty("errors").GetProperty("validation");
            foreach (var code in codes)
                if (!validation.TryGetProperty(code, out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                    missing.Add($"{Path.GetFileName(file)}: errors.validation.{code}");
        }

        Assert.True(missing.Count == 0, "Missing translations:\n" + string.Join("\n", missing));
    }

    /// <summary>End of the statement: the first ';' outside parentheses and string literals (regular, verbatim, interpolated).</summary>
    private static int StatementEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var verbatim = text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@');
                for (i++; i < text.Length; i++)
                {
                    if (verbatim)
                    {
                        if (text[i] != '"') continue;
                        if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                        break;
                    }

                    if (text[i] == '\\') { i++; continue; }
                    if (text[i] == '"') break;
                }
            }
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ';' && depth <= 0) return i;
        }

        return text.Length;
    }

    private static string FindBackendRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
