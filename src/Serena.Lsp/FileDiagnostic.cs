// Diagnostics as reported by a language server for one document, in the
// shape the tool layer needs: zero-based positions, a severity, a code and a
// message. Parsed from either a pull-model textDocument/diagnostic report or a
// push-model textDocument/publishDiagnostics notification.

using Newtonsoft.Json.Linq;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp;

/// <summary>
/// One diagnostic for a document. Positions are zero-based, as on the wire.
/// </summary>
public sealed record FileDiagnostic(
    int Line,
    int Character,
    int EndLine,
    int EndCharacter,
    DiagnosticSeverity Severity,
    string? Code,
    string Message,
    string? Source);

/// <summary>
/// Converts raw JSON diagnostics into <see cref="FileDiagnostic"/> values.
/// </summary>
public static class DiagnosticParser
{
    /// <summary>
    /// Parses the result of a <c>textDocument/diagnostic</c> request. Returns
    /// the items of a full report, or null when the report is missing,
    /// malformed or of kind <c>unchanged</c> (the caller then falls back to the
    /// last published diagnostics).
    /// </summary>
    public static IReadOnlyList<FileDiagnostic>? ParsePullReport(JToken? report)
    {
        if (report is not JObject obj)
        {
            return null;
        }

        string? kind = obj.Value<string>("kind");
        if (!string.Equals(kind, "full", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ParseItems(obj["items"]);
    }

    /// <summary>
    /// Parses the <c>diagnostics</c> array of a publishDiagnostics notification,
    /// or any array of LSP Diagnostic objects.
    /// </summary>
    public static IReadOnlyList<FileDiagnostic> ParseItems(JToken? items)
    {
        if (items is not JArray array)
        {
            return [];
        }

        var result = new List<FileDiagnostic>(array.Count);
        foreach (var item in array)
        {
            var parsed = ParseOne(item);
            if (parsed is not null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses one LSP Diagnostic object; null when it has no range or message.
    /// </summary>
    public static FileDiagnostic? ParseOne(JToken? item)
    {
        if (item is not JObject obj)
        {
            return null;
        }

        string? message = obj.Value<string>("message");
        if (obj["range"] is not JObject range || message is null)
        {
            return null;
        }

        var start = range["start"] as JObject;
        var end = range["end"] as JObject;
        int line = start?.Value<int?>("line") ?? 0;
        int character = start?.Value<int?>("character") ?? 0;
        int endLine = end?.Value<int?>("line") ?? line;
        int endCharacter = end?.Value<int?>("character") ?? character;

        int severityValue = obj.Value<int?>("severity") ?? (int)DiagnosticSeverity.Error;
        var severity = Enum.IsDefined(typeof(DiagnosticSeverity), severityValue)
            ? (DiagnosticSeverity)severityValue
            : DiagnosticSeverity.Error;

        var codeToken = obj["code"];
        string? code = codeToken is null || codeToken.Type == JTokenType.Null
            ? null
            : codeToken.Type == JTokenType.Object
                ? codeToken.Value<string>("value")
                : codeToken.ToString();

        return new FileDiagnostic(
            line, character, endLine, endCharacter,
            severity, code, message, obj.Value<string>("source"));
    }
}
