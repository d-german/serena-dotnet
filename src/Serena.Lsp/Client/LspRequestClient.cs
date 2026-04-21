// LSP Request Client - Ported from solidlsp/ls_request.py
// Phase 2B: Typed LSP request methods wrapping StreamJsonRpc
// StreamJsonRpc handles request IDs, correlation, and JSON-RPC framing natively.

using Newtonsoft.Json.Linq;
using Serena.Lsp.Process;
using Serena.Lsp.Protocol;
using Serena.Lsp.Protocol.Constants;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Client;

/// <summary>
/// Provides strongly-typed async methods for all LSP requests.
/// Wraps LanguageServerProcess.SendRequestAsync with proper param/result types.
/// Ported from solidlsp/ls_request.py LanguageServerRequest.
/// </summary>
public sealed class LspRequestClient
{
    private readonly LanguageServerProcess _process;

    public LspRequestClient(LanguageServerProcess process)
    {
        _process = process;
    }

    public Task<InitializeResult> InitializeAsync(InitializeParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<InitializeResult>(LspMethods.Initialize, @params, ct);

    public Task ShutdownAsync(CancellationToken ct = default)
        => _process.SendRequestAsync("shutdown", cancellationToken: ct);

    public Task<JToken?> DefinitionAsync(DefinitionParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentDefinition, @params, ct);

    public Task<JToken?> TypeDefinitionAsync(TypeDefinitionParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentTypeDefinition, @params, ct);

    public Task<JToken?> ImplementationAsync(ImplementationParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentImplementation, @params, ct);

    public Task<Location[]?> ReferencesAsync(ReferenceParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<Location[]?>(LspMethods.TextDocumentReferences, @params, ct);

    public Task<JToken?> DocumentSymbolAsync(DocumentSymbolParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentDocumentSymbol, @params, ct);

    public Task<SymbolInformation[]?> WorkspaceSymbolAsync(WorkspaceSymbolParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<SymbolInformation[]?>(LspMethods.WorkspaceSymbol, @params, ct);

    public Task<Hover?> HoverAsync(HoverParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<Hover?>(LspMethods.TextDocumentHover, @params, ct);

    public Task<JToken?> CompletionAsync(CompletionParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentCompletion, @params, ct);

    public Task<WorkspaceEdit?> RenameAsync(RenameParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<WorkspaceEdit?>(LspMethods.TextDocumentRename, @params, ct);

    public Task<JToken?> PrepareRenameAsync(PrepareRenameParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentPrepareRename, @params, ct);

    public Task<JToken?> CodeActionAsync(CodeActionParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentCodeAction, @params, ct);

    public Task<TextEdit[]?> FormattingAsync(DocumentFormattingParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<TextEdit[]?>(LspMethods.TextDocumentFormatting, @params, ct);

    public Task<JToken?> PrepareCallHierarchyAsync(CallHierarchyPrepareParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentPrepareCallHierarchy, @params, ct);

    public Task<JToken?> IncomingCallsAsync(CallHierarchyIncomingCallsParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.CallHierarchyIncomingCalls, @params, ct);

    public Task<JToken?> OutgoingCallsAsync(CallHierarchyOutgoingCallsParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.CallHierarchyOutgoingCalls, @params, ct);

    public Task<JToken?> PrepareTypeHierarchyAsync(TypeHierarchyPrepareParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TextDocumentPrepareTypeHierarchy, @params, ct);

    public Task<JToken?> SupertypesAsync(TypeHierarchySupertypesParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TypeHierarchySupertypes, @params, ct);

    public Task<JToken?> SubtypesAsync(TypeHierarchySubtypesParams @params, CancellationToken ct = default)
        => _process.SendRequestAsync<JToken?>(LspMethods.TypeHierarchySubtypes, @params, ct);
}

/// <summary>
/// Provides methods for sending LSP notifications (no response expected).
/// Ported from solidlsp/lsp_protocol_handler/lsp_requests.py LspNotification.
/// </summary>
public sealed class LspNotificationClient
{
    private readonly LanguageServerProcess _process;

    public LspNotificationClient(LanguageServerProcess process)
    {
        _process = process;
    }

    public Task DidOpenTextDocumentAsync(DidOpenTextDocumentParams @params)
        => _process.SendNotificationAsync(LspMethods.TextDocumentDidOpen, @params);

    public Task DidChangeTextDocumentAsync(DidChangeTextDocumentParams @params)
        => _process.SendNotificationAsync(LspMethods.TextDocumentDidChange, @params);

    public Task DidCloseTextDocumentAsync(DidCloseTextDocumentParams @params)
        => _process.SendNotificationAsync(LspMethods.TextDocumentDidClose, @params);

    public Task DidSaveTextDocumentAsync(DidSaveTextDocumentParams @params)
        => _process.SendNotificationAsync(LspMethods.TextDocumentDidSave, @params);

    public Task DidChangeConfigurationAsync(DidChangeConfigurationParams @params)
        => _process.SendNotificationAsync(LspMethods.WorkspaceDidChangeConfiguration, @params);

    public Task ExitAsync()
        => _process.SendNotificationAsync("exit");
}
