// Shared find-and-replace core for replace_content and apply_edits: applies one
// literal or regex replacement to in-memory text and reports where the first
// replacement landed so a tool can echo the surrounding lines back to the agent
// instead of making it re-read the file.

using System.Text;

namespace Serena.Core.Tools;

public enum ReplacementMode
{
    Literal,
    Regex,
}

/// <summary>
/// Outcome of one replacement: the new text, how many occurrences changed, and
/// the character offset and length of the first replacement in the new text.
/// </summary>
public sealed record ReplacementResult(string NewContent, int Count, int FirstIndex, int FirstLength);

public static class ContentReplacement
{
    public static ReplacementMode ParseMode(string? mode) =>
        string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase)
            ? ReplacementMode.Regex
            : ReplacementMode.Literal;

    /// <summary>
    /// Applies the replacement. Throws <see cref="InvalidOperationException"/>
    /// when the needle is absent, or matches more than once while
    /// <paramref name="allowMultiple"/> is false.
    /// </summary>
    public static ReplacementResult Apply(
        string content, string needle, string repl, ReplacementMode mode, bool allowMultiple, string relativePath)
    {
        return mode == ReplacementMode.Regex
            ? ApplyRegex(content, needle, repl, allowMultiple, relativePath)
            : ApplyLiteral(content, needle, repl, allowMultiple, relativePath);
    }

    private static ReplacementResult ApplyRegex(
        string content, string needle, string repl, bool allowMultiple, string relativePath)
    {
        string normalizedRepl = TextReplacementHelper.NormalizeBackreferences(repl);
        normalizedRepl = TextReplacementHelper.NormalizeLineEndings(normalizedRepl, content);
        var regex = TextReplacementHelper.CreateSearchRegex(needle);
        var matches = regex.Matches(content);

        ValidateOccurrenceCount(matches.Count, allowMultiple, relativePath, $"Pattern not found in {relativePath}: {needle}");

        var first = matches[0];
        string firstReplacement = first.Result(normalizedRepl);
        string newContent = allowMultiple
            ? regex.Replace(content, normalizedRepl)
            : regex.Replace(content, normalizedRepl, 1);

        return new ReplacementResult(newContent, matches.Count, first.Index, firstReplacement.Length);
    }

    private static ReplacementResult ApplyLiteral(
        string content, string needle, string repl, bool allowMultiple, string relativePath)
    {
        // Normalize line endings: MCP JSON delivers \n but files may have \r\n.
        string normalizedNeedle = TextReplacementHelper.NormalizeLineEndings(needle, content);
        string normalizedRepl = TextReplacementHelper.NormalizeLineEndings(repl, content);

        int count = TextReplacementHelper.CountOccurrences(content, normalizedNeedle);
        ValidateOccurrenceCount(count, allowMultiple, relativePath, $"Text not found in {relativePath}");

        int firstIndex = content.IndexOf(normalizedNeedle, StringComparison.Ordinal);
        string newContent = allowMultiple
            ? content.Replace(normalizedNeedle, normalizedRepl)
            : TextReplacementHelper.ReplaceFirst(content, normalizedNeedle, normalizedRepl);

        return new ReplacementResult(newContent, count, firstIndex, normalizedRepl.Length);
    }

    private static void ValidateOccurrenceCount(int count, bool allowMultiple, string relativePath, string notFoundMessage)
    {
        if (count == 0)
        {
            throw new InvalidOperationException(notFoundMessage);
        }

        if (count > 1 && !allowMultiple)
        {
            throw new InvalidOperationException(
                $"Text matches {count} occurrences in {relativePath}, but allow_multiple_occurrences is false.");
        }
    }

    /// <summary>
    /// One-based line number of a character offset.
    /// </summary>
    public static int LineOf(string content, int index)
    {
        int line = 1;
        int limit = Math.Min(index, content.Length);
        for (int i = 0; i < limit; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    /// <summary>
    /// Renders the lines covering <paramref name="index"/> to
    /// <paramref name="index"/> + <paramref name="length"/>, plus
    /// <paramref name="contextLines"/> lines on each side, numbered from one.
    /// Changed lines are marked with a leading <c>&gt;</c>.
    /// </summary>
    public static string FormatContext(string content, int index, int length, int contextLines)
    {
        string[] lines = content.Split('\n');
        int firstChanged = LineOf(content, index);
        int lastChanged = LineOf(content, Math.Max(index, index + length - 1));
        int from = Math.Max(1, firstChanged - contextLines);
        int to = Math.Min(lines.Length, lastChanged + contextLines);

        var sb = new StringBuilder();
        for (int line = from; line <= to; line++)
        {
            string marker = line >= firstChanged && line <= lastChanged ? ">" : " ";
            sb.Append(marker).Append(' ').Append(line.ToString().PadLeft(5)).Append(" | ")
              .Append(lines[line - 1].TrimEnd('\r')).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
