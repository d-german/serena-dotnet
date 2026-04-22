// SymbolDictGrouper - W2
// Groups symbol dictionaries by specified keys for compact JSON output.

namespace Serena.Core.Editor;

/// <summary>
/// Groups flat lists of symbol dictionaries into hierarchical structures keyed by
/// specified properties (e.g. relative_path, kind). This produces more compact
/// JSON output by hoisting repeated values into group headers.
/// </summary>
public static class SymbolDictGrouper
{
    /// <summary>
    /// Groups items by a single key. The key is removed from each item dict and
    /// becomes a group header in the result.
    /// When <paramref name="collapseSingleton"/> is true, groups containing exactly
    /// one item are collapsed to the item itself instead of a single-element list.
    /// </summary>
    public static Dictionary<string, object?> GroupBy(
        IReadOnlyList<Dictionary<string, object?>> items,
        string groupKey,
        bool collapseSingleton = true)
    {
        var grouped = new Dictionary<string, object?>();

        foreach (var item in items)
        {
            string key = item.TryGetValue(groupKey, out var v) ? v?.ToString() ?? "unknown" : "unknown";
            var copy = new Dictionary<string, object?>(item);
            copy.Remove(groupKey);

            if (!grouped.TryGetValue(key, out var existing))
            {
                grouped[key] = new List<Dictionary<string, object?>> { copy };
            }
            else
            {
                ((List<Dictionary<string, object?>>)existing!).Add(copy);
            }
        }

        if (collapseSingleton)
        {
            CollapseSingletons(grouped);
        }

        return grouped;
    }

    /// <summary>
    /// Groups items hierarchically by multiple keys. The first key produces the
    /// top-level grouping, the second key groups within each top-level group, etc.
    /// </summary>
    public static Dictionary<string, object?> GroupByMultiple(
        IReadOnlyList<Dictionary<string, object?>> items,
        string[] groupKeys,
        bool collapseSingleton = true)
    {
        if (groupKeys.Length == 0)
        {
            return new Dictionary<string, object?> { ["items"] = items };
        }

        string firstKey = groupKeys[0];
        var buckets = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var item in items)
        {
            string key = item.TryGetValue(firstKey, out var v) ? v?.ToString() ?? "unknown" : "unknown";
            var copy = new Dictionary<string, object?>(item);
            copy.Remove(firstKey);

            if (!buckets.TryGetValue(key, out var list))
            {
                list = [];
                buckets[key] = list;
            }

            list.Add(copy);
        }

        var result = new Dictionary<string, object?>();
        string[] remainingKeys = groupKeys[1..];

        foreach (var (key, bucket) in buckets)
        {
            result[key] = ResolveBucket(bucket, remainingKeys, collapseSingleton);
        }

        return result;
    }

    private static object ResolveBucket(
        List<Dictionary<string, object?>> bucket, string[] remainingKeys, bool collapseSingleton)
    {
        if (remainingKeys.Length > 0)
        {
            return GroupByMultiple(bucket, remainingKeys, collapseSingleton);
        }

        if (collapseSingleton && bucket.Count == 1)
        {
            return bucket[0];
        }

        return bucket;
    }

    /// <summary>
    /// Groups child-level symbol dicts by the specified keys.
    /// Same logic as <see cref="GroupByMultiple"/> but named for clarity at call sites
    /// that operate on children of a parent symbol.
    /// </summary>
    public static Dictionary<string, object?> GroupChildren(
        IReadOnlyList<Dictionary<string, object?>> children,
        string[] groupByKeys,
        bool collapseSingleton = true)
    {
        return GroupByMultiple(children, groupByKeys, collapseSingleton);
    }

    private static void CollapseSingletons(Dictionary<string, object?> grouped)
    {
        foreach (var key in grouped.Keys.ToList())
        {
            if (grouped[key] is List<Dictionary<string, object?>> { Count: 1 } list)
            {
                grouped[key] = list[0];
            }
        }
    }
}
