// Shared text manipulation utilities to eliminate duplication across tools, editors, and project managers.

using System.Text.RegularExpressions;

namespace Serena.Core.Tools;

/// <summary>
/// Static helper methods for text replacement operations.
/// Used by ReplaceContentTool, LanguageServerCodeEditor, and MemoriesManager.
/// </summary>
public static class TextReplacementHelper
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Counts occurrences of <paramref name="needle"/> in <paramref name="text"/> (ordinal, case-sensitive).
    /// </summary>
    public static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Normalizes backreference syntax from <c>$!N</c> to <c>$N</c> for regex replacements.
    /// </summary>
    public static string NormalizeBackreferences(string replacement)
    {
        return Regex.Replace(replacement, @"\$!(\d+)", @"$$$1");
    }

    /// <summary>
    /// Replaces only the first occurrence of <paramref name="needle"/> in <paramref name="text"/>.
    /// </summary>
    public static string ReplaceFirst(string text, string needle, string replacement)
    {
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }
        return text[..index] + replacement + text[(index + needle.Length)..];
    }

    /// <summary>
    /// Creates a compiled regex with the standard timeout and options.
    /// </summary>
    public static Regex CreateSearchRegex(string pattern)
    {
        return new Regex(pattern, RegexOptions.Singleline | RegexOptions.Multiline, RegexTimeout);
    }

    /// <summary>
    /// Normalizes line endings in <paramref name="text"/> to match those used in <paramref name="referenceContent"/>.
    /// This is needed because MCP JSON transport delivers strings with LF (\n) but files on Windows
    /// may use CRLF (\r\n). Python avoids this because <c>open()</c> normalizes to LF on read;
    /// C# <c>File.ReadAllTextAsync</c> preserves the original line endings.
    /// </summary>
    public static string NormalizeLineEndings(string text, string referenceContent)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\n'))
        {
            return text;
        }

        bool contentHasCrlf = referenceContent.Contains("\r\n");
        bool textHasCrlf = text.Contains("\r\n");

        if (contentHasCrlf && !textHasCrlf)
        {
            // File has CRLF but text has bare LF — upgrade to CRLF
            return text.Replace("\n", "\r\n");
        }

        if (!contentHasCrlf && textHasCrlf)
        {
            // File has LF but text has CRLF — downgrade to LF
            return text.Replace("\r\n", "\n");
        }

        return text;
    }
}
