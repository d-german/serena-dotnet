// apply_edits - several find-and-replace edits across one or more files in a
// single call. A refactor that touches a hundred files costs a hundred
// replace_content turns; this tool makes it one turn per batch, applies each
// file's edits atomically, echoes the changed lines back, and reports the
// language server's diagnostics for every written file when it is warm.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serena.Core.Editor;
using Serena.Core.Project;

namespace Serena.Core.Tools;

[CanEdit]
public sealed class ApplyEditsTool : ToolBase
{
    public ApplyEditsTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Applies a batch of find-and-replace edits to one or more files in one call. Each edit has " +
        "relative_path, needle, repl, optional mode ('literal' default or 'regex') and optional " +
        "allow_multiple_occurrences (default false). Edits are applied in order; edits to the same file " +
        "are applied to the running text, so a later needle must match the text as left by earlier edits. " +
        "A file is written only if every edit to it succeeds; a failed edit leaves that file untouched and " +
        "is reported with its reason while other files still proceed. The result shows the changed lines " +
        "with their numbers, so no re-read is needed, and the language server's diagnostics for each written " +
        "file when the server is already warm. Prefer this over repeated replace_content calls.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("edits", "Array of edit objects: {\"relative_path\": \"src/A.cs\", \"needle\": \"old\", \"repl\": \"new\", \"mode\": \"literal\", \"allow_multiple_occurrences\": false}. A JSON string holding the same array is also accepted.", typeof(List<Dictionary<string, object?>>), Required: true),
        new("context_lines", "Lines of context to show around each change. 0 shows only the changed lines.", typeof(int), Required: false, DefaultValue: 2),
        new("report_diagnostics", "Append the language server's diagnostics for each written file when the server is running and Ready or Partial. Never starts a server.", typeof(bool), Required: false, DefaultValue: true),
        new("min_severity", "Lowest diagnostic severity to list: 'error' (default), 'warning', 'information' or 'hint'.", typeof(string), Required: false, DefaultValue: "error"),
    ];

    internal sealed record EditSpec(
        int Ordinal, string RelativePath, string Needle, string Repl, ReplacementMode Mode, bool AllowMultiple);

    private sealed record AppliedEdit(EditSpec Spec, int Count, int Index, int Length);

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        arguments.TryGetValue("edits", out var rawEdits);
        var edits = ParseEdits(rawEdits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("'edits' must contain at least one edit.");
        }

        int contextLines = Math.Max(0, GetOptional(arguments, "context_lines", 2));
        bool reportDiagnostics = GetOptional(arguments, "report_diagnostics", true);
        var minSeverity = DiagnosticsReport.ParseMinSeverity(GetOptional(arguments, "min_severity", "error"));

        var encoding = GetProjectEncoding();
        var byFile = edits.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        var report = new StringBuilder();
        var fileSections = new List<string>();
        var indexUpdates = new List<SymbolCacheUpdate>();
        int applied = 0;
        int filesWritten = 0;

        foreach (var group in byFile)
        {
            string relativePath = group.Key;
            var section = new StringBuilder();
            string absolutePath;
            try
            {
                absolutePath = ResolvePath(relativePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                section.Append("== ").Append(relativePath).Append(": NOT written (").Append(ex.Message).Append(')');
                fileSections.Add(section.ToString());
                continue;
            }

            // The lease covers read, replace and write, so two calls on one file apply in sequence.
            await using var lease = await FileWriteGate.LockAsync(absolutePath, ct);
            string content;
            try
            {
                content = await File.ReadAllTextAsync(absolutePath, encoding, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                section.Append("== ").Append(relativePath).Append(": NOT written (").Append(ex.Message).Append(')');
                fileSections.Add(section.ToString());
                continue;
            }

            var appliedHere = new List<AppliedEdit>();
            string? failure = null;
            foreach (var spec in group)
            {
                try
                {
                    var result = ContentReplacement.Apply(content, spec.Needle, spec.Repl, spec.Mode, spec.AllowMultiple, relativePath);
                    content = result.NewContent;
                    appliedHere.Add(new AppliedEdit(spec, result.Count, result.FirstIndex, result.FirstLength));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or RegexMatchTimeoutException)
                {
                    failure = $"edit [{spec.Ordinal}] failed: {ex.Message}";
                    break;
                }
            }

            if (failure is not null)
            {
                section.Append("== ").Append(relativePath).Append(": NOT written (").Append(failure).Append(')');
                fileSections.Add(section.ToString());
                continue;
            }

            await lease.WriteAllTextAsync(content, encoding, ct);
            if (await TryNotifyLspAsync(absolutePath, content, ct) is { } indexUpdate)
            {
                indexUpdates.Add(indexUpdate);
            }
            filesWritten++;
            applied += appliedHere.Count;

            section.Append("== ").Append(relativePath).Append(": ").Append(appliedHere.Count).Append(" edit(s) applied");
            foreach (var edit in appliedHere)
            {
                int line = ContentReplacement.LineOf(content, edit.Index);
                section.Append('\n').Append("  [").Append(edit.Spec.Ordinal).Append("] replaced ")
                       .Append(edit.Count).Append(" occurrence(s), first at line ").Append(line);
                string context = ContentReplacement.FormatContext(content, edit.Index, edit.Length, contextLines);
                foreach (string contextLine in context.Split('\n'))
                {
                    section.Append('\n').Append("    ").Append(contextLine);
                }
            }

            if (reportDiagnostics)
            {
                string? diagnostics = await DiagnosticsReport.TryBuildAfterEditAsync(Context, absolutePath, relativePath, minSeverity, ct);
                if (diagnostics is not null)
                {
                    section.Append('\n').Append("  diagnostics: ").Append(diagnostics.Replace("\n", "\n  "));
                }
            }

            fileSections.Add(section.ToString());
        }

        report.Append("Applied ").Append(applied).Append(" of ").Append(edits.Count)
              .Append(" edit(s); ").Append(filesWritten).Append(" of ").Append(byFile.Count).Append(" file(s) written.");
        foreach (string section in fileSections)
        {
            report.Append("\n\n").Append(section);
        }

        string? indexNote = DescribeIndexUpdates(indexUpdates);
        if (indexNote is not null)
        {
            report.Append("\n\n").Append(indexNote);
        }

        return report.ToString();
    }

    /// <summary>
    /// One line about the index when a written file's outline has to wait for a
    /// language server, so the agent knows that find_symbol on it will start or
    /// wait for the server while find_occurrences is already current. Silent
    /// when a usable server is rebuilding every outline in the background.
    /// </summary>
    internal static string? DescribeIndexUpdates(IReadOnlyList<SymbolCacheUpdate> updates)
    {
        int deferred = updates.Count(u => u.Outline == OutlineRefresh.Deferred);
        if (deferred == 0)
        {
            return null;
        }
        var state = updates.First(u => u.Outline == OutlineRefresh.Deferred).ServerState;
        return $"Index: occurrence rows are current for all {updates.Count} written file(s); the outline of {deferred} waits for " +
               $"the language server (now {state}) and is rebuilt in the background once it is Ready or Partial. " +
               "find_occurrences works now; find_symbol on those files starts or waits for the server.";
    }

    /// <summary>
    /// Accepts the edit list as delivered by the MCP bridge (a list whose
    /// elements are dictionaries or raw JSON object strings) or as one JSON
    /// string holding the whole array.
    /// </summary>
    internal static List<EditSpec> ParseEdits(object? raw)
    {
        var items = new List<IReadOnlyDictionary<string, object?>>();

        switch (raw)
        {
            case null:
                throw new ArgumentException("Required parameter 'edits' is missing.");
            case string json:
                items.AddRange(ParseJsonArray(json));
                break;
            case IEnumerable<object?> list:
                foreach (var element in list)
                {
                    switch (element)
                    {
                        case IReadOnlyDictionary<string, object?> dict:
                            items.Add(dict);
                            break;
                        case IDictionary<string, object?> dict:
                            items.Add(new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase));
                            break;
                        case string json:
                            items.AddRange(ParseJsonObjectOrArray(json));
                            break;
                        case JsonElement element2:
                            items.AddRange(FromJsonElement(element2));
                            break;
                        default:
                            throw new ArgumentException("Each edit must be an object with relative_path, needle and repl.");
                    }
                }
                break;
            case JsonElement element:
                items.AddRange(FromJsonElement(element));
                break;
            default:
                throw new ArgumentException("'edits' must be an array of edit objects.");
        }

        var specs = new List<EditSpec>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int ordinal = i + 1;
            string relativePath = RequireString(item, "relative_path", ordinal);
            string needle = RequireString(item, "needle", ordinal);
            string repl = item.TryGetValue("repl", out var replValue) && replValue is not null
                ? Convert.ToString(replValue) ?? string.Empty
                : throw new ArgumentException($"Edit [{ordinal}] is missing 'repl'.");
            var mode = ContentReplacement.ParseMode(item.TryGetValue("mode", out var modeValue) ? Convert.ToString(modeValue) : null);
            bool allowMultiple = item.TryGetValue("allow_multiple_occurrences", out var multiValue) && multiValue is not null
                && Convert.ToBoolean(multiValue);
            specs.Add(new EditSpec(ordinal, relativePath, needle, repl, mode, allowMultiple));
        }

        return specs;
    }

    private static string RequireString(IReadOnlyDictionary<string, object?> item, string key, int ordinal)
    {
        if (!item.TryGetValue(key, out var value) || value is null || Convert.ToString(value) is not { Length: > 0 } text)
        {
            throw new ArgumentException($"Edit [{ordinal}] is missing '{key}'.");
        }
        return text;
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ParseJsonArray(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("'edits' must be a JSON array of edit objects.");
        }
        return doc.RootElement.EnumerateArray().Select(ToDictionary).ToList();
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> ParseJsonObjectOrArray(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJsonElement(doc.RootElement).ToList();
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => [ToDictionary(element)],
            JsonValueKind.Array => element.EnumerateArray().Select(ToDictionary).ToList(),
            _ => throw new ArgumentException("Each edit must be a JSON object."),
        };
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Each edit must be a JSON object.");
        }

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.Value.GetDouble(),
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }
        return dict;
    }
}
