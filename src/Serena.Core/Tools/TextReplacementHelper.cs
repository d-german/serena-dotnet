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
}
