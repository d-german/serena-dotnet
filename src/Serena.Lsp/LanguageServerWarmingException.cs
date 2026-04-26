// v1.0.26: Cold-start UX — typed signal that an LSP request couldn't complete
// because the language server is still loading the workspace. Carries a
// snapshot of readiness so the tool layer can render structured guidance.

using System.Text.Json;

namespace Serena.Lsp;

/// <summary>
/// Lifecycle phases for a language server's workspace load. Set by
/// LanguageServerManager and reported through <see cref="ReadyStateSnapshot"/>.
/// </summary>
public enum WorkspaceReadyState
{
    /// <summary>No server process has been started for this language yet.</summary>
    NotStarted,
    /// <summary>Server process is up; workspace (solutions/projects) is loading.</summary>
    Loading,
    /// <summary>Initial workspace load has completed; semantic queries are viable.</summary>
    Ready,
    /// <summary>Server process or workspace load failed.</summary>
    Failed,
}

/// <summary>
/// Immutable snapshot of a language server's readiness. Returned by the
/// manager and embedded in <see cref="LanguageServerWarmingException"/> so
/// the tool layer can produce a stable, JSON-serializable response without
/// re-querying anything.
/// </summary>
public sealed record ReadyStateSnapshot(
    WorkspaceReadyState State,
    int? ProjectsLoaded = null,
    int? ProjectsTotal = null,
    double ElapsedSeconds = 0,
    string? ScopeDescription = null);

/// <summary>
/// Thrown by LSP-touching call sites when the per-request timeout elapses
/// while the workspace is still loading. The tool layer catches this and
/// returns a structured warming response instead of letting the call hang
/// past the MCP client's own timeout.
/// </summary>
public sealed class LanguageServerWarmingException : LspClientException
{
    public LanguageServerWarmingException(Language language, ReadyStateSnapshot snapshot, string advice)
        : base(BuildMessage(language, snapshot, advice), language)
    {
        Snapshot = snapshot;
        Advice = advice;
        WarmingLanguage = language;
    }

    public Language WarmingLanguage { get; }
    public ReadyStateSnapshot Snapshot { get; }
    public string Advice { get; }

    public string ToJson()
    {
        // v1.0.32: when the workspace IS Ready but the per-request timeout
        // fired (slow request, not warmup), surface a distinct status so the
        // agent doesn't think "warmup is taking forever" — it's the request
        // itself that took too long.
        bool requestTimeoutWhileReady = Snapshot.State == WorkspaceReadyState.Ready;
        var payload = new Dictionary<string, object?>
        {
            ["status"] = requestTimeoutWhileReady
                ? "language_server_request_timeout"
                : "language_server_warming",
            ["language"] = WarmingLanguage.ToString().ToLowerInvariant(),
            ["state"] = Snapshot.State.ToString(),
            ["projects_loaded"] = Snapshot.ProjectsLoaded,
            ["projects_total"] = Snapshot.ProjectsTotal,
            ["elapsed_seconds"] = Math.Round(Snapshot.ElapsedSeconds, 1),
            ["scope"] = Snapshot.ScopeDescription,
            ["advice"] = Advice,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildMessage(Language language, ReadyStateSnapshot snapshot, string advice)
    {
        // v1.0.32: "is still Ready after Xs" is self-contradictory. When State
        // is Ready the workspace finished loading and the per-request timeout
        // is what fired; report that distinctly. Snapshot.ElapsedSeconds is
        // workspace uptime, not request duration — phrase accordingly.
        if (snapshot.State == WorkspaceReadyState.Ready)
        {
            return $"{language} language server request timed out (workspace state: Ready, " +
                   $"uptime {Math.Round(snapshot.ElapsedSeconds, 1)}s). {advice}";
        }

        string progress = snapshot.ProjectsTotal is int total
            ? $" ({snapshot.ProjectsLoaded ?? 0}/{total} projects)"
            : "";
        return $"{language} language server is still {snapshot.State}{progress} after " +
               $"{Math.Round(snapshot.ElapsedSeconds, 1)}s. {advice}";
    }
}
