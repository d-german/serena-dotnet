// ToolResultFormatter - Phase D3
// Progressive truncation and compact formatting for tool results.
// Ported from serena/result_formatter.py.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serena.Core.Tools;

/// <summary>
/// Formats and truncates tool results to fit within max_answer_chars limits.
/// Uses a progressive fallback chain: full → shortened → raw truncation.
/// </summary>
public static class ToolResultFormatter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Default maximum answer characters when no explicit limit is set.
    /// </summary>
    public const int DefaultMaxChars = 50_000;

    /// <summary>
    /// Formats a result string, applying truncation if it exceeds maxChars.
    /// Returns the original string if within limits, otherwise progressively truncates.
    /// </summary>
    public static string Format(string result, int maxChars = -1)
    {
        if (maxChars < 0)
        {
            maxChars = DefaultMaxChars;
        }

        if (result.Length <= maxChars)
        {
            return result;
        }

        return TruncateWithMessage(result, maxChars);
    }

    /// <summary>
    /// Formats a collection of items as JSON, applying progressive truncation.
    /// First tries full serialization, then reduces items until within limits.
    /// </summary>
    public static string FormatCollection<T>(
        IReadOnlyList<T> items,
        int maxChars = -1,
        Func<T, Dictionary<string, object?>>? toDict = null)
    {
        if (maxChars < 0)
        {
            maxChars = DefaultMaxChars;
        }

        // Try full serialization first
        string full = SerializeItems(items, toDict);
        if (full.Length <= maxChars)
        {
            return full;
        }

        // Progressive reduction: try with fewer items
        for (int count = items.Count / 2; count >= 1; count /= 2)
        {
            var subset = items.Take(count).ToList();
            string partial = SerializeItems(subset, toDict);
            string result = partial + $"\n\n... ({items.Count - count} more items truncated, {items.Count} total)";

            if (result.Length <= maxChars)
            {
                return result;
            }
        }

        // Last resort: raw truncation of single item
        if (items.Count > 0)
        {
            string single = SerializeItems(items.Take(1).ToList(), toDict);
            return TruncateWithMessage(single, maxChars);
        }

        return "[]";
    }

    /// <summary>
    /// Formats symbol overview data (grouped by kind) with truncation.
    /// </summary>
    public static string FormatSymbolOverview(
        Dictionary<string, List<Dictionary<string, object?>>> overview,
        int maxChars = -1)
    {
        if (maxChars < 0)
        {
            maxChars = DefaultMaxChars;
        }

        string full = JsonSerializer.Serialize(overview, s_jsonOptions);
        if (full.Length <= maxChars)
        {
            return full;
        }

        // Try compact format: just names and kinds
        var compact = new Dictionary<string, List<string>>();
        foreach (var (kind, symbols) in overview)
        {
            compact[kind] = symbols
                .Select(s => s.TryGetValue("name_path", out var np) ? np?.ToString() ?? "?" : "?")
                .ToList();
        }

        string compactResult = JsonSerializer.Serialize(compact, s_jsonOptions);
        if (compactResult.Length <= maxChars)
        {
            return compactResult;
        }

        return TruncateWithMessage(compactResult, maxChars);
    }

    /// <summary>
    /// Formats a directory-level symbol overview (per-file, then per-kind) with progressive truncation.
    /// </summary>
    public static string FormatDirectoryOverview(
        Dictionary<string, Dictionary<string, List<Dictionary<string, object?>>>> directoryOverview,
        int maxChars = -1)
    {
        if (maxChars < 0)
        {
            maxChars = DefaultMaxChars;
        }

        string full = JsonSerializer.Serialize(directoryOverview, s_jsonOptions);
        if (full.Length <= maxChars)
        {
            return full;
        }

        // Try compact format: file -> kind -> names
        var compact = new Dictionary<string, Dictionary<string, List<string>>>();
        foreach (var (file, overview) in directoryOverview)
        {
            var fileCompact = new Dictionary<string, List<string>>();
            foreach (var (kind, symbols) in overview)
            {
                fileCompact[kind] = symbols
                    .Select(s => s.TryGetValue("name_path", out var np) ? np?.ToString() ?? "?" : "?")
                    .ToList();
            }
            compact[file] = fileCompact;
        }

        string compactResult = JsonSerializer.Serialize(compact, s_jsonOptions);
        if (compactResult.Length <= maxChars)
        {
            return compactResult;
        }

        return TruncateWithMessage(compactResult, maxChars);
    }

    /// <summary>
    /// Formats a list of symbols with progressive truncation.
    /// </summary>
    public static string FormatSymbols(
        IReadOnlyList<Dictionary<string, object?>> symbolDicts,
        int maxChars = -1)
    {
        if (maxChars < 0)
        {
            maxChars = DefaultMaxChars;
        }

        string full = JsonSerializer.Serialize(symbolDicts, s_jsonOptions);
        if (full.Length <= maxChars)
        {
            return full;
        }

        // Try without body/children for each symbol
        var shortened = symbolDicts
            .Select(d => d
                .Where(kvp => kvp.Key is not "body" and not "children" and not "info")
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .ToList();

        string shortenedResult = JsonSerializer.Serialize(shortened, s_jsonOptions);
        if (shortenedResult.Length <= maxChars)
        {
            return shortenedResult;
        }

        // Try with fewer items
        return FormatCollection<Dictionary<string, object?>>(shortened, maxChars);
    }

    /// <summary>
    /// Formats reference results with truncation.
    /// </summary>

    /// <summary>
    /// Formats a grouped symbol dictionary (produced by <see cref="Editor.SymbolDictGrouper"/>)
    /// with progressive truncation.
    /// </summary>
    public static string FormatGroupedSymbols(
        Dictionary<string, object?> grouped,
        int maxChars = -1)
    {
        if (maxChars < 0)
        {
            maxChars = DefaultMaxChars;
        }

        string full = JsonSerializer.Serialize(grouped, s_jsonOptions);
        if (full.Length <= maxChars)
        {
            return full;
        }

        return TruncateWithMessage(full, maxChars);
    }

    public static string FormatReferences(
        IReadOnlyList<Dictionary<string, object?>> references,
        int maxChars = -1)
    {
        return FormatCollection(references, maxChars);
    }

    /// <summary>
    /// Limits a string to the specified maximum length, appending a truncation message.
    /// </summary>
    public static string LimitLength(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        return TruncateWithMessage(text, maxChars);
    }

    /// <summary>
    /// Tries a chain of progressively shorter factory functions to fit within maxChars.
    /// If no factory result fits, the last factory's output is truncated.
    /// When no factories are supplied, falls back to simple truncation.
    /// </summary>
    public static string LimitWithFactories(string fullResult, int maxChars, params Func<string>[] shortenedFactories)
    {
        if (maxChars <= 0 || fullResult.Length <= maxChars)
        {
            return fullResult;
        }

        if (shortenedFactories.Length == 0)
        {
            return TruncateWithMessage(fullResult, maxChars);
        }

        string note = $"[Note: Result was shortened to fit within {maxChars} characters]\n";
        int budget = maxChars - note.Length;

        string lastResult = fullResult;
        foreach (var factory in shortenedFactories)
        {
            lastResult = factory();
            if (budget > 0 && lastResult.Length <= budget)
            {
                return note + lastResult;
            }
        }

        return TruncateWithMessage(note + lastResult, maxChars);
    }

    private static string TruncateWithMessage(string text, int maxChars)
    {
        string suffix = $"\n\n... (truncated, {text.Length} total chars)";
        int available = maxChars - suffix.Length;

        if (available <= 0)
        {
            return suffix.TrimStart();
        }

        return text[..available] + suffix;
    }

    private static string SerializeItems<T>(
        IReadOnlyList<T> items,
        Func<T, Dictionary<string, object?>>? toDict)
    {
        if (toDict is not null)
        {
            var dicts = items.Select(toDict).ToList();
            return JsonSerializer.Serialize(dicts, s_jsonOptions);
        }
        return JsonSerializer.Serialize(items, s_jsonOptions);
    }
}
