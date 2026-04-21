// LSP Enums - Ported from solidlsp/lsp_protocol_handler/lsp_types.py (LSP v3.17.0)

namespace Serena.Lsp.Protocol.Types;

/// <summary>
/// A symbol kind.
/// </summary>
public enum SymbolKind
{
    Unknown = 0,
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

/// <summary>
/// Symbol tags are extra annotations that tweak the rendering of a symbol.
/// </summary>
public enum SymbolTag
{
    Deprecated = 1,
}

/// <summary>
/// Severity of a diagnostic.
/// </summary>
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>
/// The diagnostic tags.
/// </summary>
public enum DiagnosticTag
{
    Unnecessary = 1,
    Deprecated = 2,
}

/// <summary>
/// Predefined error codes.
/// </summary>
public enum ErrorCodes
{
    ParseError = -32700,
    InvalidRequest = -32600,
    MethodNotFound = -32601,
    InvalidParams = -32602,
    InternalError = -32603,
    ServerNotInitialized = -32002,
    UnknownErrorCode = -32001,
}

/// <summary>
/// LSP-specific error codes.
/// </summary>
public enum LspErrorCodes
{
    RequestFailed = -32803,
    ServerCancelled = -32802,
    ContentModified = -32801,
    RequestCancelled = -32800,
}

/// <summary>
/// Defines how the host (editor) should sync document changes to the language server.
/// </summary>
public enum TextDocumentSyncKind
{
    None = 0,
    Full = 1,
    Incremental = 2,
}

/// <summary>
/// The message type.
/// </summary>
public enum MessageType
{
    Error = 1,
    Warning = 2,
    Info = 3,
    Log = 4,
}

/// <summary>
/// A document highlight kind.
/// </summary>
public enum DocumentHighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3,
}

/// <summary>
/// The kind of a completion entry.
/// </summary>
public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

/// <summary>
/// A set of predefined range kinds for folding.
/// </summary>
public enum FoldingRangeKind
{
    Comment,
    Imports,
    Region,
}

/// <summary>
/// Defines whether the insert text in a completion item should be interpreted as plain text or a snippet.
/// </summary>
public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2,
}

/// <summary>
/// How whitespace and indentation is handled during completion item insertion.
/// </summary>
public enum InsertTextMode
{
    AsIs = 1,
    AdjustIndentation = 2,
}

/// <summary>
/// A set of predefined token types for semantic tokens.
/// </summary>
public static class SemanticTokenTypes
{
    public const string Namespace = "namespace";
    public const string Type = "type";
    public const string Class = "class";
    public const string Enum = "enum";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string TypeParameter = "typeParameter";
    public const string Parameter = "parameter";
    public const string Variable = "variable";
    public const string Property = "property";
    public const string EnumMember = "enumMember";
    public const string Event = "event";
    public const string Function = "function";
    public const string Method = "method";
    public const string Macro = "macro";
    public const string Keyword = "keyword";
    public const string Modifier = "modifier";
    public const string Comment = "comment";
    public const string String = "string";
    public const string Number = "number";
    public const string Regexp = "regexp";
    public const string Operator = "operator";
    public const string Decorator = "decorator";
}

/// <summary>
/// A set of predefined token modifiers for semantic tokens.
/// </summary>
public static class SemanticTokenModifiers
{
    public const string Declaration = "declaration";
    public const string Definition = "definition";
    public const string Readonly = "readonly";
    public const string Static = "static";
    public const string Deprecated = "deprecated";
    public const string Abstract = "abstract";
    public const string Async = "async";
    public const string Modification = "modification";
    public const string Documentation = "documentation";
    public const string DefaultLibrary = "defaultLibrary";
}

/// <summary>
/// The kind of a markup content value.
/// </summary>
public static class MarkupKind
{
    public const string PlainText = "plaintext";
    public const string Markdown = "markdown";
}

/// <summary>
/// A set of predefined code action kinds.
/// </summary>
public static class CodeActionKind
{
    public const string Empty = "";
    public const string QuickFix = "quickfix";
    public const string Refactor = "refactor";
    public const string RefactorExtract = "refactor.extract";
    public const string RefactorInline = "refactor.inline";
    public const string RefactorRewrite = "refactor.rewrite";
    public const string Source = "source";
    public const string SourceOrganizeImports = "source.organizeImports";
    public const string SourceFixAll = "source.fixAll";
}

/// <summary>
/// Inlay hint kinds.
/// </summary>
public enum InlayHintKind
{
    Type = 1,
    Parameter = 2,
}

/// <summary>
/// Represents reasons why a text document is saved.
/// </summary>
public enum TextDocumentSaveReason
{
    Manual = 1,
    AfterDelay = 2,
    FocusOut = 3,
}

/// <summary>
/// The file event type.
/// </summary>
public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

/// <summary>
/// Trace values for the trace notification.
/// </summary>
public static class TraceValues
{
    public const string Off = "off";
    public const string Messages = "messages";
    public const string Verbose = "verbose";
}
