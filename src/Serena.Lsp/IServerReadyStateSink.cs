// v1.0.26: Cold-start UX — sink interface so MvpLanguageServers (Serena.Lsp)
// can report workspace-load progress back to LanguageServerManager
// (Serena.Core) without creating a circular dependency.

namespace Serena.Lsp;

/// <summary>
/// Receives workspace-load lifecycle signals from a <see cref="LanguageServers.LanguageServerDefinition"/>.
/// Implemented by Serena.Core's LanguageServerManager so symbol tools can ask
/// "is the workspace loaded yet?" without polling the LSP.
/// </summary>
public interface IServerReadyStateSink
{
    /// <summary>
    /// Called once the LSP process is up and we've started issuing
    /// workspace-load notifications (e.g. <c>solution/open</c>). Optionally
    /// carries the count of projects in scope, when known up front.
    /// </summary>
    void MarkLoading(Language language, int? projectsTotal = null, string? scopeDescription = null);

    /// <summary>
    /// Called when the initial workspace load has completed (or stopped
    /// receiving activity). After this, semantic queries should be viable.
    /// </summary>
    void MarkReady(Language language);

    /// <summary>
    /// Called when the workspace load failed unrecoverably.
    /// </summary>
    void MarkFailed(Language language);

    /// <summary>
    /// Returns the current snapshot for the given language, or NotStarted if
    /// no signals have been recorded.
    /// </summary>
    ReadyStateSnapshot GetReadyState(Language language);
}
