// Identifier occurrences per file, captured at index time so a reference
// question ("who uses PatientContentRepository?") can be answered from the
// index in milliseconds without a language server. The document symbol
// outline knows where things are declared; this knows where names appear.
//
// It is textual, not semantic: it cannot tell two unrelated members with the
// same name apart. It is also deliberately selective, keeping only
// PascalCase identifiers of three characters or more, which in C# and
// TypeScript are the types, members and constants a reader asks about, and
// skipping locals, parameters, keywords and private fields. Comments are
// masked out; string literals are kept because route names and configuration
// keys live there.

using System.Text;

namespace Serena.Lsp.Caching;

public static class OccurrenceIndexer
{
    /// <summary>Files larger than this are not scanned (generated code, bundles).</summary>
    public const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the file and extracts occurrences. Null when the file is missing,
    /// too large or unreadable; never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, int[]>? TryExtractFromFile(string absolutePath)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }
            return Extract(File.ReadAllText(absolutePath, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps each indexable identifier to the sorted, distinct one-based line
    /// numbers it appears on, with comments masked out.
    /// </summary>
    public static IReadOnlyDictionary<string, int[]> Extract(string content)
    {
        var lines = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        string masked = MaskComments(content);

        int line = 1;
        int i = 0;
        int n = masked.Length;
        while (i < n)
        {
            char c = masked[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                int start = i;
                while (i < n && IsIdentifierPart(masked[i]))
                {
                    i++;
                }
                if (IsIndexable(masked, start, i - start))
                {
                    string token = masked.Substring(start, i - start);
                    if (!lines.TryGetValue(token, out var set))
                    {
                        set = [];
                        lines[token] = set;
                    }
                    set.Add(line);
                }
                continue;
            }

            if (char.IsDigit(c))
            {
                // Skip numeric literals so "1E5" or hex digits never look like identifiers.
                while (i < n && (char.IsLetterOrDigit(masked[i]) || masked[i] == '_' || masked[i] == '.'))
                {
                    i++;
                }
                continue;
            }

            i++;
        }

        return lines.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    internal static bool IsIndexable(string text, int start, int length) =>
        length >= 3 && char.IsUpper(text[start]);

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Replaces line and block comments with spaces, keeping newlines so line
    /// numbers are unchanged. String literals (regular, verbatim and raw) are
    /// copied through so a "//" inside a URL string is not taken for a comment.
    /// </summary>
    internal static string MaskComments(string content)
    {
        var sb = new StringBuilder(content.Length);
        int i = 0;
        int n = content.Length;
        while (i < n)
        {
            char c = content[i];
            char next = i + 1 < n ? content[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < n && content[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }
                continue;
            }

            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && !(content[i] == '*' && i + 1 < n && content[i + 1] == '/'))
                {
                    sb.Append(content[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < n)
                {
                    sb.Append("  ");
                    i += 2;
                }
                continue;
            }

            if (c == '"')
            {
                i = CopyString(content, i, sb);
                continue;
            }

            if (c == '\'' )
            {
                // Char literal: copy up to the closing quote on the same line.
                int end = i + 1;
                while (end < n && content[end] != '\'' && content[end] != '\n')
                {
                    if (content[end] == '\\')
                    {
                        end++;
                    }
                    end++;
                }
                end = Math.Min(n, end + 1);
                sb.Append(content, i, end - i);
                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int CopyString(string content, int start, StringBuilder sb)
    {
        int n = content.Length;
        bool verbatim = start > 0 && content[start - 1] == '@';
        bool raw = start + 2 < n && content[start + 1] == '"' && content[start + 2] == '"';

        if (raw)
        {
            int quotes = 0;
            while (start + quotes < n && content[start + quotes] == '"')
            {
                quotes++;
            }
            string fence = new('"', quotes);
            int close = content.IndexOf(fence, start + quotes, StringComparison.Ordinal);
            int end = close < 0 ? n : close + quotes;
            sb.Append(content, start, end - start);
            return end;
        }

        int i = start + 1;
        while (i < n)
        {
            char c = content[i];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < n && content[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
            }
            else
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '"' || c == '\n')
                {
                    i++;
                    break;
                }
            }
            i++;
        }
        int stop = Math.Min(i, n);
        sb.Append(content, start, stop - start);
        return stop;
    }
}
