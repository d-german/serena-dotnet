// v1.0.26: Cold-start UX — wrap a single LSP-touching call so it returns a
// typed warming exception instead of blocking past the MCP client's own
// timeout when Roslyn is still loading the workspace.

using Microsoft.Extensions.Logging;
using Serena.Lsp;

namespace Serena.Core.Editor;

/// <summary>
/// Per-LSP-request timeout wrapper. Keeps callers below the MCP client's own
/// 5–10 minute timeout window so the agent always gets control back, and
/// translates the inner timeout into a structured
/// <see cref="LanguageServerWarmingException"/> carrying the current
/// readiness snapshot so the tool layer can render actionable guidance.
/// </summary>
public static class LspTimeout
{
    private const int DefaultTimeoutSeconds = 60;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 1800;
    private const string EnvVar = "SERENA_LSP_REQUEST_TIMEOUT_SECONDS";

    /// <summary>
    /// Per-request timeout. Reads <c>SERENA_LSP_REQUEST_TIMEOUT_SECONDS</c>
    /// (clamped to 5..1800), defaulting to 60s. Resolved once per process to
    /// avoid hot-path env lookups.
    /// </summary>
    public static TimeSpan RequestTimeout { get; } = ResolveTimeout();

    /// <summary>
    /// Wraps an LSP-touching async operation with the configured timeout.
    /// On timeout, throws <see cref="LanguageServerWarmingException"/> populated
    /// from <paramref name="snapshotFactory"/>. Outer cancellation (the caller's
    /// <paramref name="ct"/>) propagates as a normal <see cref="OperationCanceledException"/>
    /// — only the inner timeout becomes a warming exception.
    /// </summary>
    public static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> op,
        Func<ReadyStateSnapshot> snapshotFactory,
        Language language,
        ILogger? logger,
        CancellationToken ct)
    {
        // v1.0.27: pre-flight readiness gate. If the workspace isn't Ready,
        // throw the warming exception immediately without ever invoking the
        // op delegate. This protects the agent from interpreting empty
        // mid-load LSP responses as 'symbol not found' and avoids burning
        // the full RequestTimeout window per call while warmup is in flight.
        var preflight = snapshotFactory();
        if (preflight.State != WorkspaceReadyState.Ready)
        {
            string preflightAdvice = preflight.State switch
            {
                WorkspaceReadyState.NotStarted =>
                    "Call warm_language_server to start the language server, then poll get_language_server_status. " +
                    "Use search_for_pattern for exploration in the meantime.",
                WorkspaceReadyState.Loading =>
                    "The language server is still loading the workspace. Use search_for_pattern for exploration; " +
                    "poll get_language_server_status until state == Ready, then retry this request.",
                WorkspaceReadyState.Failed =>
                    "Language server warmup failed. Check serena-dotnet logs; consider restart_language_server.",
                _ => "Wait until the language server is Ready before retrying."
            };
            logger?.LogInformation(
                "Pre-flight: skipping LSP request for {Language} because state is {State}",
                language, preflight.State);
            throw new LanguageServerWarmingException(language, preflight, preflightAdvice);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(RequestTimeout);
        try
        {
            return await op(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Inner timeout fired — convert to a warming signal the tool layer can act on.
            var snapshot = snapshotFactory();
            string advice = "Use search_for_pattern for exploration. Call get_language_server_status " +
                            "to check readiness, then retry this request.";
            logger?.LogInformation(
                "LSP request for {Language} timed out after {Timeout}s while server state is {State}",
                language, RequestTimeout.TotalSeconds, snapshot.State);
            throw new LanguageServerWarmingException(language, snapshot, advice);
        }
    }

    private static TimeSpan ResolveTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable(EnvVar);
        if (int.TryParse(raw, out int value))
        {
            return TimeSpan.FromSeconds(Math.Clamp(value, MinTimeoutSeconds, MaxTimeoutSeconds));
        }
        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }
}
