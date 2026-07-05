namespace CursorialEdit.Presenters;

/// <summary>
/// The raw-source view's markdown colorizer (M2.WP10 / architecture Decision 12): classifies the
/// structural <b>marks</b> of a literal markdown source line so raw mode can color the syntax distinctly
/// from prose. It reuses the <see cref="MiniHighlighter"/> token vocabulary (<see cref="CodeToken"/> /
/// <see cref="CodeTokenClass"/> / <see cref="MiniHighlighter.BrushFor"/>) so raw-view colors sit in the
/// same palette as the code-block highlighter — a distinct method from the code-block
/// <see cref="MiniHighlighter.Tokenize"/> path (that one drives fenced code; this one drives whole-document
/// raw view), kept separate so neither regresses the other.
/// </summary>
/// <remarks>
/// Tokenization is deliberately <b>line-local</b> (like <see cref="MiniHighlighter"/>): leading
/// heading/quote/list markers, whole-line thematic breaks and fences, inline emphasis/strong/strike
/// delimiters, inline code spans, and link/image brackets — no cross-line fence state. A multi-line
/// construct simply colors each line by what that line shows, which is exactly right for a verbatim view.
/// The WP11 theme layer will later route these classes through <c>Md.*</c> tokens; WP10 emits the
/// <see cref="MiniHighlighter"/> brushes directly (the plan's "just emit the color").
/// </remarks>
internal static class RawMarkdownHighlighter
{
    /// <summary>
    /// The mark spans of a raw markdown source <paramref name="line"/> (block-line-relative UTF-16
    /// offsets): structural marks as <see cref="CodeTokenClass.Keyword"/>, inline code spans as
    /// <see cref="CodeTokenClass.String"/>. Empty for a blank line or plain prose (drawn monochrome).
    /// The caller draws the line verbatim first, then overdraws these token colors.
    /// </summary>
    public static IReadOnlyList<CodeToken> Tokenize(string line)
    {
        if (string.IsNullOrEmpty(line))
            return [];

        int n = line.Length;
        var tokens = new List<CodeToken>();

        int i = 0;
        while (i < n && line[i] == ' ')
            i++;
        int contentStart = i;

        // Whole-line marks: a thematic break (---/***/___) or a fence line (```/~~~) colors entirely.
        if (IsThematicBreak(line, contentStart) || IsFenceLine(line, contentStart))
        {
            tokens.Add(new CodeToken(contentStart, n - contentStart, CodeTokenClass.Keyword));
            return tokens;
        }

        // Leading structural marker: ATX heading prefix, blockquote bars, or a list marker.
        if (i < n && line[i] == '#')
        {
            int h = i;
            while (h < n && line[h] == '#')
                h++;
            if (h - i <= 6 && (h >= n || line[h] == ' ' || line[h] == '\t'))
            {
                tokens.Add(new CodeToken(i, h - i, CodeTokenClass.Keyword));
                i = h;
            }
        }
        else if (i < n && line[i] == '>')
        {
            int q = i;
            while (q < n && (line[q] == '>' || line[q] == ' '))
                q++;
            tokens.Add(new CodeToken(i, q - i, CodeTokenClass.Keyword));
            i = q;
        }
        else
        {
            int marker = ListMarkerEnd(line, i);
            if (marker > i)
            {
                tokens.Add(new CodeToken(i, marker - i, CodeTokenClass.Keyword));
                i = marker;
            }
        }

        // Inline marks over the remainder: code spans (String), emphasis/strong/strike delimiter runs
        // and link/image brackets (Keyword).
        while (i < n)
        {
            char c = line[i];

            if (c == '`')
            {
                int start = i;
                i++;
                while (i < n && line[i] != '`')
                    i++;
                if (i < n)
                    i++; // include the closing backtick
                tokens.Add(new CodeToken(start, i - start, CodeTokenClass.String));
                continue;
            }

            if (c is '*' or '_' or '~')
            {
                int start = i;
                while (i < n && line[i] == c)
                    i++;
                tokens.Add(new CodeToken(start, i - start, CodeTokenClass.Keyword));
                continue;
            }

            if (c is '[' or ']' or '!')
            {
                tokens.Add(new CodeToken(i, 1, CodeTokenClass.Keyword));
                i++;
                continue;
            }

            i++;
        }

        return tokens;
    }

    /// <summary>Whether <paramref name="line"/> from <paramref name="start"/> is a thematic break: ≥ 3 of a single <c>-</c>/<c>*</c>/<c>_</c>, only spaces between.</summary>
    private static bool IsThematicBreak(string line, int start)
    {
        char marker = '\0';
        int count = 0;
        for (int j = start; j < line.Length; j++)
        {
            char c = line[j];
            if (c is ' ' or '\t')
                continue;
            if (c is not ('-' or '*' or '_'))
                return false;
            if (marker == '\0')
                marker = c;
            else if (c != marker)
                return false;
            count++;
        }

        return count >= 3;
    }

    /// <summary>Whether <paramref name="line"/> at <paramref name="start"/> opens/closes a fence: ≥ 3 of <c>`</c> or <c>~</c>.</summary>
    private static bool IsFenceLine(string line, int start)
    {
        if (start >= line.Length)
            return false;

        char c = line[start];
        if (c is not ('`' or '~'))
            return false;

        int count = 0;
        while (start + count < line.Length && line[start + count] == c)
            count++;
        return count >= 3;
    }

    /// <summary>The index just past a leading list marker (<c>- </c>/<c>* </c>/<c>+ </c> or <c>1. </c>/<c>1) </c>), or <paramref name="i"/> when there is none.</summary>
    private static int ListMarkerEnd(string line, int i)
    {
        int n = line.Length;
        if (i >= n)
            return i;

        char c = line[i];
        if (c is '-' or '*' or '+')
        {
            int end = i + 1;
            if (end < n && (line[end] == ' ' || line[end] == '\t'))
                return end + 1; // include the trailing space
            if (end == n)
                return end; // a bare "-" marker
            return i;
        }

        int k = i;
        while (k < n && char.IsAsciiDigit(line[k]) && k - i < 9)
            k++;
        if (k > i && k < n && (line[k] == '.' || line[k] == ')'))
        {
            int end = k + 1;
            if (end < n && (line[end] == ' ' || line[end] == '\t'))
                return end + 1;
            if (end == n)
                return end;
        }

        return i;
    }
}
