// Diagnostics - compiler and analyzer diagnostics for one file, straight from the
// language server, so an agent can check an edit in under a second instead of
// running a build to find out that a brace is unbalanced or a using is missing.

using System.Text;
using Microsoft.Extensions.Logging;
using Serena.Core.Editor;
using Serena.Lsp;
using Serena.Lsp.Protocol.Types;

namespace Serena.Core.Tools;

/// <summary>
/// Formats <see cref="FileDiagnostic"/> lists for tool output, and builds the
/// optional post-edit report that edit tools append when a language server is
/// already warm.
/// </summary>
public static class DiagnosticsReport
{
    private static readonly TimeSpan AfterEditTimeout = TimeSpan.FromSeconds(15);

    public static DiagnosticSeverity ParseMinSeverity(string? text) =>
        (text ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "error" or "errors" => DiagnosticSeverity.Error,
            "warning" or "warnings" => DiagnosticSeverity.Warning,
            "info" or "information" => DiagnosticSeverity.Information,
            "hint" or "hints" or "all" => DiagnosticSeverity.Hint,
            _ => DiagnosticSeverity.Warning,
        };

    /// <summary>
    /// Renders diagnostics at or above <paramref name="minSeverity"/> as one
    /// summary line followed by one line per diagnostic, ordered by position.
    /// Lines and columns are one-based, matching compiler output.
    /// </summary>
    public static string Format(string relativePath, IReadOnlyList<FileDiagnostic> diagnostics, DiagnosticSeverity minSeverity)
    {
        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        var shown = diagnostics
            .Where(d => d.Severity <= minSeverity)
            .OrderBy(d => d.Line).ThenBy(d => d.Character)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(relativePath).Append(": ")
          .Append(errors).Append(" error(s), ")
          .Append(warnings).Append(" warning(s)");
        if (shown.Count < diagnostics.Count)
        {
            sb.Append(" (showing ").Append(shown.Count).Append(" at or above ")
              .Append(minSeverity.ToString().ToLowerInvariant()).Append(')');
        }

        foreach (var d in shown)
        {
            sb.Append('\n').Append("  ")
              .Append(d.Severity.ToString().ToLowerInvariant());
            if (!string.IsNullOrEmpty(d.Code))
            {
                sb.Append(' ').Append(d.Code);
            }
            sb.Append(" (").Append(d.Line + 1).Append(',').Append(d.Character + 1).Append("): ")
              .Append(d.Message.ReplaceLineEndings(" "));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Diagnostics for a file the caller has just written, when the file's
    /// language server is already running and Ready or Partial. Returns null
    /// when there is no warm server (a write must never cold-start one), and a
    /// one-line note on timeout or failure, so the edit result is never lost
    /// to a diagnostics problem.
    /// </summary>
    public static async Task<string?> TryBuildAfterEditAsync(
        IToolContext context, string absolutePath, string relativePath, DiagnosticSeverity minSeverity, CancellationToken ct)
    {
        var client = context.Agent.TryGetRunningLanguageServer(absolutePath);
        if (client is null)
        {
            return null;
        }

        var state = context.Agent.GetLanguageServerReadyState(client.Language).State;
        if (state is not (WorkspaceReadyState.Ready or WorkspaceReadyState.Partial))
        {
            return $"{relativePath}: diagnostics unavailable (language server {state})";
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AfterEditTimeout);
            var diagnostics = await client.RequestDocumentDiagnosticsAsync(absolutePath, timeout.Token).ConfigureAwait(false);
            return Format(relativePath, diagnostics, minSeverity);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"{relativePath}: diagnostics timed out after {AfterEditTimeout.TotalSeconds:0}s";
        }
        catch (Exception ex)
        {
            context.LoggerFactory.CreateLogger(typeof(DiagnosticsReport))
                .LogDebug(ex, "Post-edit diagnostics failed for {Path}", absolutePath);
            return $"{relativePath}: diagnostics unavailable ({ex.GetType().Name})";
        }
    }
}

/// <summary>
/// get_file_diagnostics: the language server's current diagnostics for one
/// file. Starts the server if needed (like the symbol tools) and returns the
/// structured warming response while the workspace is still loading.
/// </summary>
public sealed class GetFileDiagnosticsTool : ToolBase
{
    public GetFileDiagnosticsTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Returns the compiler and analyzer diagnostics the language server reports for one file, " +
        "as 'severity CODE (line,col): message' lines with one-based positions. Use it after editing a file " +
        "and before building: it reports unbalanced braces, missing usings, unresolved types and callers " +
        "broken by a signature change in well under a second. Requires the language server to be Ready " +
        "or Partial; while it is loading, a language_server_warming response is returned instead.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("min_severity", "Lowest severity to list: 'error', 'warning' (default), 'information' or 'hint'. The summary line always counts errors and warnings.", typeof(string), Required: false, DefaultValue: "warning"),
        new("max_answer_chars", "Max characters for the result. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        var minSeverity = DiagnosticsReport.ParseMinSeverity(GetOptional(arguments, "min_severity", "warning"));
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);

        string absolutePath = ResolvePath(relativePath);
        if (!File.Exists(absolutePath))
        {
            return $"Path not found: {relativePath}";
        }

        var client = await Context.Agent.GetLanguageServerForFileAsync(absolutePath, ct)
            ?? throw new InvalidOperationException(
                $"No language server available for '{relativePath}'. Ensure a language server is configured.");

        try
        {
            var language = client.Language;
            var diagnostics = await LspTimeout.WithTimeoutAsync(
                token => client.RequestDocumentDiagnosticsAsync(absolutePath, token),
                () => Context.Agent.GetLanguageServerReadyState(language),
                language,
                Logger,
                ct);

            return LimitLength(DiagnosticsReport.Format(relativePath, diagnostics, minSeverity), maxChars);
        }
        catch (LanguageServerWarmingException ex)
        {
            return SymbolToolWarmingResponse.Format(ex);
        }
    }
}
