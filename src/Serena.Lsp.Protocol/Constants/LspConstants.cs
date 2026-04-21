// LSP Constants - Ported from solidlsp/lsp_protocol_handler/lsp_constants.py

namespace Serena.Lsp.Protocol.Constants;

/// <summary>
/// Constants used in the LSP protocol for dictionary keys.
/// </summary>
public static class LspConstants
{
    public const string Uri = "uri";
    public const string Range = "range";
    public const string OriginSelectionRange = "originSelectionRange";
    public const string TargetUri = "targetUri";
    public const string TargetRange = "targetRange";
    public const string TargetSelectionRange = "targetSelectionRange";
    public const string TextDocument = "textDocument";
    public const string LanguageId = "languageId";
    public const string Version = "version";
    public const string Text = "text";
    public const string Position = "position";
    public const string Line = "line";
    public const string Character = "character";
    public const string ContentChanges = "contentChanges";
    public const string Name = "name";
    public const string Kind = "kind";
    public const string Children = "children";
    public const string Location = "location";
    public const string Severity = "severity";
    public const string Message = "message";
}

/// <summary>
/// LSP method names for requests and notifications.
/// </summary>
public static class LspMethods
{
    // Lifecycle
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    // Text Document Sync
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    public const string TextDocumentDidChange = "textDocument/didChange";
    public const string TextDocumentDidClose = "textDocument/didClose";
    public const string TextDocumentDidSave = "textDocument/didSave";
    public const string TextDocumentWillSave = "textDocument/willSave";

    // Language Features
    public const string TextDocumentCompletion = "textDocument/completion";
    public const string CompletionItemResolve = "completionItem/resolve";
    public const string TextDocumentHover = "textDocument/hover";
    public const string TextDocumentSignatureHelp = "textDocument/signatureHelp";
    public const string TextDocumentDefinition = "textDocument/definition";
    public const string TextDocumentTypeDefinition = "textDocument/typeDefinition";
    public const string TextDocumentImplementation = "textDocument/implementation";
    public const string TextDocumentReferences = "textDocument/references";
    public const string TextDocumentDocumentHighlight = "textDocument/documentHighlight";
    public const string TextDocumentDocumentSymbol = "textDocument/documentSymbol";
    public const string TextDocumentCodeAction = "textDocument/codeAction";
    public const string TextDocumentCodeLens = "textDocument/codeLens";
    public const string TextDocumentFormatting = "textDocument/formatting";
    public const string TextDocumentRangeFormatting = "textDocument/rangeFormatting";
    public const string TextDocumentOnTypeFormatting = "textDocument/onTypeFormatting";
    public const string TextDocumentRename = "textDocument/rename";
    public const string TextDocumentPrepareRename = "textDocument/prepareRename";
    public const string TextDocumentFoldingRange = "textDocument/foldingRange";
    public const string TextDocumentSelectionRange = "textDocument/selectionRange";
    public const string TextDocumentLinkedEditingRange = "textDocument/linkedEditingRange";
    public const string TextDocumentSemanticTokensFull = "textDocument/semanticTokens/full";
    public const string TextDocumentSemanticTokensDelta = "textDocument/semanticTokens/full/delta";
    public const string TextDocumentSemanticTokensRange = "textDocument/semanticTokens/range";
    public const string TextDocumentInlayHint = "textDocument/inlayHint";
    public const string TextDocumentDiagnostic = "textDocument/diagnostic";
    public const string TextDocumentDocumentLink = "textDocument/documentLink";
    public const string TextDocumentDocumentColor = "textDocument/documentColor";
    public const string TextDocumentColorPresentation = "textDocument/colorPresentation";

    // Call Hierarchy
    public const string TextDocumentPrepareCallHierarchy = "textDocument/prepareCallHierarchy";
    public const string CallHierarchyIncomingCalls = "callHierarchy/incomingCalls";
    public const string CallHierarchyOutgoingCalls = "callHierarchy/outgoingCalls";

    // Type Hierarchy
    public const string TextDocumentPrepareTypeHierarchy = "textDocument/prepareTypeHierarchy";
    public const string TypeHierarchySupertypes = "typeHierarchy/supertypes";
    public const string TypeHierarchySubtypes = "typeHierarchy/subtypes";

    // Workspace
    public const string WorkspaceSymbol = "workspace/symbol";
    public const string WorkspaceExecuteCommand = "workspace/executeCommand";
    public const string WorkspaceDidChangeConfiguration = "workspace/didChangeConfiguration";
    public const string WorkspaceDidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
    public const string WorkspaceApplyEdit = "workspace/applyEdit";

    // Window
    public const string WindowShowMessage = "window/showMessage";
    public const string WindowShowMessageRequest = "window/showMessageRequest";
    public const string WindowLogMessage = "window/logMessage";
    public const string WindowWorkDoneProgressCreate = "window/workDoneProgress/create";

    // Diagnostics (push model)
    public const string TextDocumentPublishDiagnostics = "textDocument/publishDiagnostics";
}
