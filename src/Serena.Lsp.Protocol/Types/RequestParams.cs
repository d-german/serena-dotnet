// LSP Request/Response Parameter types - Ported from solidlsp/lsp_protocol_handler/lsp_types.py
// These are the param types for LSP methods used by Serena.

using System.Text.Json.Serialization;

namespace Serena.Lsp.Protocol.Types;

// ---- Base parameter types ----

public record TextDocumentPositionParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("position")]
    public required Position Position { get; init; }
}

public record WorkDoneProgressParams
{
    [JsonPropertyName("workDoneToken")]
    public object? WorkDoneToken { get; init; }
}

// ---- Initialize ----

public sealed record InitializeParams
{
    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    [JsonPropertyName("rootUri")]
    public string? RootUri { get; init; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; init; }

    [JsonPropertyName("capabilities")]
    public required object Capabilities { get; init; }

    [JsonPropertyName("trace")]
    public string? Trace { get; init; }

    [JsonPropertyName("workspaceFolders")]
    public IReadOnlyList<WorkspaceFolder>? WorkspaceFolders { get; init; }

    [JsonPropertyName("initializationOptions")]
    public object? InitializationOptions { get; init; }

    [JsonPropertyName("clientInfo")]
    public ClientInfo? ClientInfo { get; init; }
}

public sealed record ClientInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version);

public sealed record WorkspaceFolder(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

public sealed record InitializeResult
{
    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }

    [JsonPropertyName("serverInfo")]
    public ServerInfo? ServerInfo { get; init; }
}

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version);

/// <summary>
/// Server capabilities returned by the initialize response.
/// This is a simplified version covering the capabilities Serena actually uses.
/// </summary>
public sealed record ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public object? TextDocumentSync { get; init; }

    [JsonPropertyName("completionProvider")]
    public object? CompletionProvider { get; init; }

    [JsonPropertyName("hoverProvider")]
    public object? HoverProvider { get; init; }

    [JsonPropertyName("definitionProvider")]
    public object? DefinitionProvider { get; init; }

    [JsonPropertyName("typeDefinitionProvider")]
    public object? TypeDefinitionProvider { get; init; }

    [JsonPropertyName("implementationProvider")]
    public object? ImplementationProvider { get; init; }

    [JsonPropertyName("referencesProvider")]
    public object? ReferencesProvider { get; init; }

    [JsonPropertyName("documentSymbolProvider")]
    public object? DocumentSymbolProvider { get; init; }

    [JsonPropertyName("workspaceSymbolProvider")]
    public object? WorkspaceSymbolProvider { get; init; }

    [JsonPropertyName("documentHighlightProvider")]
    public object? DocumentHighlightProvider { get; init; }

    [JsonPropertyName("codeActionProvider")]
    public object? CodeActionProvider { get; init; }

    [JsonPropertyName("codeLensProvider")]
    public object? CodeLensProvider { get; init; }

    [JsonPropertyName("documentFormattingProvider")]
    public object? DocumentFormattingProvider { get; init; }

    [JsonPropertyName("renameProvider")]
    public object? RenameProvider { get; init; }

    [JsonPropertyName("foldingRangeProvider")]
    public object? FoldingRangeProvider { get; init; }

    [JsonPropertyName("executeCommandProvider")]
    public object? ExecuteCommandProvider { get; init; }

    [JsonPropertyName("callHierarchyProvider")]
    public object? CallHierarchyProvider { get; init; }

    [JsonPropertyName("typeHierarchyProvider")]
    public object? TypeHierarchyProvider { get; init; }

    [JsonPropertyName("semanticTokensProvider")]
    public object? SemanticTokensProvider { get; init; }

    [JsonPropertyName("inlayHintProvider")]
    public object? InlayHintProvider { get; init; }

    [JsonPropertyName("diagnosticProvider")]
    public object? DiagnosticProvider { get; init; }
}

// ---- Document Sync ----

public sealed record DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentItem TextDocument { get; init; }
}

public sealed record DidChangeTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required VersionedTextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("contentChanges")]
    public required IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges { get; init; }
}

public sealed record TextDocumentContentChangeEvent
{
    [JsonPropertyName("range")]
    public Range? Range { get; init; }

    [JsonPropertyName("rangeLength")]
    public int? RangeLength { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }
}

public sealed record DidSaveTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

// ---- Definition / Implementation / TypeDefinition ----

public sealed record DefinitionParams : TextDocumentPositionParams;

public sealed record ImplementationParams : TextDocumentPositionParams;

public sealed record TypeDefinitionParams : TextDocumentPositionParams;

// ---- References ----

public sealed record ReferenceParams : TextDocumentPositionParams
{
    [JsonPropertyName("context")]
    public required ReferenceContext Context { get; init; }
}

public sealed record ReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);

// ---- Document Symbols ----

public sealed record DocumentSymbolParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }
}

// ---- Workspace Symbols ----

public sealed record WorkspaceSymbolParams
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }
}

// ---- Hover ----

public sealed record HoverParams : TextDocumentPositionParams;

// ---- Signature Help ----

public sealed record SignatureHelpParams : TextDocumentPositionParams;

// ---- Completion ----

public sealed record CompletionParams : TextDocumentPositionParams
{
    [JsonPropertyName("context")]
    public CompletionContext? Context { get; init; }
}

public sealed record CompletionContext
{
    [JsonPropertyName("triggerKind")]
    public required int TriggerKind { get; init; }

    [JsonPropertyName("triggerCharacter")]
    public string? TriggerCharacter { get; init; }
}

// ---- Rename ----

public sealed record RenameParams : TextDocumentPositionParams
{
    [JsonPropertyName("newName")]
    public required string NewName { get; init; }
}

public sealed record PrepareRenameParams : TextDocumentPositionParams;

// ---- Code Action ----

public sealed record CodeActionParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("range")]
    public required Range Range { get; init; }

    [JsonPropertyName("context")]
    public required CodeActionContext Context { get; init; }
}

public sealed record CodeActionContext
{
    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    [JsonPropertyName("only")]
    public IReadOnlyList<string>? Only { get; init; }
}

// ---- Execute Command ----

public sealed record ExecuteCommandParams
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("arguments")]
    public IReadOnlyList<object>? Arguments { get; init; }
}

// ---- Formatting ----

public sealed record DocumentFormattingParams
{
    [JsonPropertyName("textDocument")]
    public required TextDocumentIdentifier TextDocument { get; init; }

    [JsonPropertyName("options")]
    public required FormattingOptions Options { get; init; }
}

public sealed record FormattingOptions
{
    [JsonPropertyName("tabSize")]
    public required int TabSize { get; init; }

    [JsonPropertyName("insertSpaces")]
    public required bool InsertSpaces { get; init; }

    [JsonPropertyName("trimTrailingWhitespace")]
    public bool? TrimTrailingWhitespace { get; init; }

    [JsonPropertyName("insertFinalNewline")]
    public bool? InsertFinalNewline { get; init; }

    [JsonPropertyName("trimFinalNewlines")]
    public bool? TrimFinalNewlines { get; init; }
}

// ---- Diagnostics ----

public sealed record PublishDiagnosticsParams
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("version")]
    public int? Version { get; init; }

    [JsonPropertyName("diagnostics")]
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
}

// ---- Configuration ----

public sealed record DidChangeConfigurationParams
{
    [JsonPropertyName("settings")]
    public required object Settings { get; init; }
}

// ---- Call Hierarchy ----

public sealed record CallHierarchyPrepareParams : TextDocumentPositionParams;

public sealed record CallHierarchyItem
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required SymbolKind Kind { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("range")]
    public required Range Range { get; init; }

    [JsonPropertyName("selectionRange")]
    public required Range SelectionRange { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<SymbolTag>? Tags { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public sealed record CallHierarchyIncomingCallsParams(
    [property: JsonPropertyName("item")] CallHierarchyItem Item);

public sealed record CallHierarchyOutgoingCallsParams(
    [property: JsonPropertyName("item")] CallHierarchyItem Item);

public sealed record CallHierarchyIncomingCall(
    [property: JsonPropertyName("from")] CallHierarchyItem From,
    [property: JsonPropertyName("fromRanges")] IReadOnlyList<Range> FromRanges);

public sealed record CallHierarchyOutgoingCall(
    [property: JsonPropertyName("to")] CallHierarchyItem To,
    [property: JsonPropertyName("fromRanges")] IReadOnlyList<Range> FromRanges);

// ---- Type Hierarchy ----

public sealed record TypeHierarchyPrepareParams : TextDocumentPositionParams;

public sealed record TypeHierarchyItem
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required SymbolKind Kind { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("range")]
    public required Range Range { get; init; }

    [JsonPropertyName("selectionRange")]
    public required Range SelectionRange { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<SymbolTag>? Tags { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

public sealed record TypeHierarchySupertypesParams(
    [property: JsonPropertyName("item")] TypeHierarchyItem Item);

public sealed record TypeHierarchySubtypesParams(
    [property: JsonPropertyName("item")] TypeHierarchyItem Item);

// ---- File Operations ----

public sealed record FileEvent(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("type")] FileChangeType Type);

public sealed record DidChangeWatchedFilesParams(
    [property: JsonPropertyName("changes")] IReadOnlyList<FileEvent> Changes);
