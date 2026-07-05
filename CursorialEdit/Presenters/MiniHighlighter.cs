using Cursorial.Drawing.Media;
using Cursorial.Output;

namespace CursorialEdit.Presenters;

/// <summary>The syntax-token classes the <see cref="MiniHighlighter"/> recognizes (a fixed, small budget — §2.1 [DECISION]).</summary>
internal enum CodeTokenClass
{
    /// <summary>A language keyword.</summary>
    Keyword,

    /// <summary>A string / quoted literal.</summary>
    String,

    /// <summary>A comment.</summary>
    Comment,

    /// <summary>A numeric literal.</summary>
    Number,
}

/// <summary>One highlighted token span of a code line (block-line-relative UTF-16 offsets).</summary>
internal readonly record struct CodeToken(int Start, int Length, CodeTokenClass Class);

/// <summary>The languages the built-in highlighter covers (the framework's own set); everything else is monochrome.</summary>
internal enum CodeLanguage
{
    /// <summary>Unrecognized — rendered monochrome in the code fill (spec-legal, §2.1 [DECISION]).</summary>
    Unknown,
    CSharp,
    Xaml,
    Json,
    Markdown,
    Shell,
}

/// <summary>
/// The time-boxed built-in syntax highlighter (§2.1 [DECISION]): a fixed token-class budget
/// (keyword / string / comment / number) over the framework's own languages — C#, XAML, JSON,
/// Markdown, shell — and <b>monochrome</b> for every other language (spec-legal). Tokenization is
/// deliberately <b>line-local</b> (no cross-line comment/string state) to stay cheap and bounded; a
/// multi-line construct simply loses highlighting on its continuation lines rather than costing state.
/// Broader/precise highlighting is [DEFER].
/// </summary>
internal static class MiniHighlighter
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "var", "virtual", "void", "volatile", "while", "async", "await", "record",
        "nameof", "when", "yield", "get", "set", "init", "global",
    };

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "fi", "for", "in", "do", "done", "while", "until", "case", "esac",
        "function", "return", "echo", "export", "local", "cd", "set", "unset", "read", "source",
        "exit", "test", "select", "time",
    };

    private static readonly HashSet<string> JsonKeywords = new(StringComparer.Ordinal) { "true", "false", "null" };

    // Token COLORS now live in the Md.* theme tokens (MarkdownStyles.CodeTokenBrush / RawMarkBrush); this
    // class only CLASSIFIES tokens. (The former per-class brushes + BrushFor were retired in WP11a.)

    /// <summary>Maps a fence info string (<see cref="Document.Model.Block.FenceInfo"/>) to a language.</summary>
    public static CodeLanguage Detect(string? fenceInfo)
    {
        if (string.IsNullOrWhiteSpace(fenceInfo))
            return CodeLanguage.Unknown;

        // The info string's first word is the language (per CommonMark).
        string token = fenceInfo.Trim();
        int space = token.IndexOfAny([' ', '\t']);
        if (space >= 0)
            token = token[..space];

        return token.ToLowerInvariant() switch
        {
            "csharp" or "cs" or "c#" or "dotnet" => CodeLanguage.CSharp,
            "xml" or "xaml" or "html" or "axaml" => CodeLanguage.Xaml,
            "json" or "jsonc" => CodeLanguage.Json,
            "markdown" or "md" => CodeLanguage.Markdown,
            "sh" or "bash" or "shell" or "zsh" or "console" => CodeLanguage.Shell,
            _ => CodeLanguage.Unknown,
        };
    }

    /// <summary>
    /// The non-plain token spans of <paramref name="line"/> under <paramref name="language"/> (the
    /// caller draws the plain line first and overdraws these). Empty for the monochrome
    /// <see cref="CodeLanguage.Unknown"/> language.
    /// </summary>
    public static IReadOnlyList<CodeToken> Tokenize(CodeLanguage language, string line)
    {
        if (language == CodeLanguage.Unknown || string.IsNullOrEmpty(line))
            return [];

        var tokens = new List<CodeToken>();
        int i = 0;
        int n = line.Length;

        while (i < n)
        {
            char c = line[i];

            // Line comments.
            if (language == CodeLanguage.CSharp && c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                tokens.Add(new CodeToken(i, n - i, CodeTokenClass.Comment));
                break;
            }

            if (language == CodeLanguage.Shell && c == '#')
            {
                tokens.Add(new CodeToken(i, n - i, CodeTokenClass.Comment));
                break;
            }

            // Single-line block comments.
            if (language == CodeLanguage.CSharp && c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? n : end + 2;
                tokens.Add(new CodeToken(i, stop - i, CodeTokenClass.Comment));
                i = stop;
                continue;
            }

            if (language == CodeLanguage.Xaml && c == '<' && line.AsSpan(i).StartsWith("<!--"))
            {
                int end = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                int stop = end < 0 ? n : end + 3;
                tokens.Add(new CodeToken(i, stop - i, CodeTokenClass.Comment));
                i = stop;
                continue;
            }

            // Strings.
            if (c == '"' || (c == '\'' && language is CodeLanguage.CSharp or CodeLanguage.Shell))
            {
                // Backslash escapes a quote only in languages that use C-style escaping. XAML/XML (and
                // Markdown) do not — a value ending in `\` there must NOT swallow its closing quote.
                bool cStyleEscapes = language is CodeLanguage.CSharp or CodeLanguage.Json or CodeLanguage.Shell;
                int start = i;
                i++;
                while (i < n && line[i] != c)
                {
                    if (cStyleEscapes && line[i] == '\\' && i + 1 < n)
                        i++;
                    i++;
                }

                if (i < n)
                    i++; // include the closing quote
                tokens.Add(new CodeToken(start, i - start, CodeTokenClass.String));
                continue;
            }

            // Numbers (C#/JSON). Scanned precisely so a trailing member access (`2.ToString()`) or dotted
            // run (`255.255.255`) is not swallowed as one number; hex letters count only after a 0x prefix.
            if (language is CodeLanguage.CSharp or CodeLanguage.Json && char.IsAsciiDigit(c)
                && (i == 0 || !IsIdentChar(line[i - 1])))
            {
                int start = i;
                if (c == '0' && i + 1 < n && (line[i + 1] is 'x' or 'X'))
                {
                    i += 2;
                    while (i < n && (char.IsAsciiHexDigit(line[i]) || line[i] == '_'))
                        i++;
                }
                else
                {
                    while (i < n && (char.IsAsciiDigit(line[i]) || line[i] == '_'))
                        i++;
                    if (i + 1 < n && line[i] == '.' && char.IsAsciiDigit(line[i + 1])) // a decimal point, not `.Member`
                    {
                        i++;
                        while (i < n && (char.IsAsciiDigit(line[i]) || line[i] == '_'))
                            i++;
                    }
                }

                tokens.Add(new CodeToken(start, i - start, CodeTokenClass.Number));
                continue;
            }

            // Identifiers → keywords.
            if (IsIdentStart(c))
            {
                int start = i;
                while (i < n && IsIdentChar(line[i]))
                    i++;

                if (KeywordsFor(language) is { } keywords && keywords.Contains(line[start..i]))
                    tokens.Add(new CodeToken(start, i - start, CodeTokenClass.Keyword));
                continue;
            }

            // Markdown: leading ATX marker and backtick spans.
            if (language == CodeLanguage.Markdown)
            {
                if (c == '`')
                {
                    int start = i;
                    i++;
                    while (i < n && line[i] != '`')
                        i++;
                    if (i < n)
                        i++;
                    tokens.Add(new CodeToken(start, i - start, CodeTokenClass.String));
                    continue;
                }

                if (c == '#' && i == 0)
                {
                    tokens.Add(new CodeToken(0, n, CodeTokenClass.Keyword));
                    break;
                }
            }

            i++;
        }

        return tokens;
    }

    private static HashSet<string>? KeywordsFor(CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => CSharpKeywords,
        CodeLanguage.Shell => ShellKeywords,
        CodeLanguage.Json => JsonKeywords,
        _ => null,
    };

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
