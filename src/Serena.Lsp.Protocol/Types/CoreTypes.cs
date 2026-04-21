// LSP Protocol Types - Ported from solidlsp/lsp_protocol_handler/lsp_types.py (LSP v3.17.0)
// Core structural types used throughout the Serena LSP client.

using System.Text.Json.Serialization;

namespace Serena.Lsp.Protocol.Types;

// Type aliases
// URI = string, DocumentUri = string, Uint = uint, RegExp = string

/// <summary>
/// Position in a text document expressed as zero-based line and character offset.
/// </summary>
public sealed record Position(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character) : IComparable<Position>
{
    public static Position Zero => new(0, 0);

    public int CompareTo(Position? other)
    {
        if (other is null)
        {
            return 1;
        }
        int lineCmp = Line.CompareTo(other.Line);
        return lineCmp != 0 ? lineCmp : Character.CompareTo(other.Character);
    }

    public static bool operator <(Position left, Position right) => left.CompareTo(right) < 0;
    public static bool operator >(Position left, Position right) => left.CompareTo(right) > 0;
    public static bool operator <=(Position left, Position right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Position left, Position right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// A range in a text document expressed as (zero-based) start and end positions.
/// </summary>
public sealed record Range(
    [property: JsonPropertyName("start")] Position Start,
    [property: JsonPropertyName("end")] Position End)
{
    public bool Contains(Position position) =>
        position >= Start && position <= End;

    public bool Overlaps(Range other) =>
        Start <= other.End && End >= other.Start;
}

/// <summary>
/// Represents a location inside a resource, such as a line inside a text file.
/// </summary>
public sealed record Location(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range);

/// <summary>
/// Represents a link between a source and a target location.
/// </summary>
public sealed record LocationLink(
    [property: JsonPropertyName("originSelectionRange")] Range? OriginSelectionRange,
    [property: JsonPropertyName("targetUri")] string TargetUri,
    [property: JsonPropertyName("targetRange")] Range TargetRange,
    [property: JsonPropertyName("targetSelectionRange")] Range TargetSelectionRange);

/// <summary>
/// A literal to identify a text document in the client.
/// </summary>
public sealed record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

/// <summary>
/// A text document identifier to denote a specific version of a text document.
/// </summary>
public sealed record VersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version);

/// <summary>
/// An identifier to optionally denote a specific version of a text document.
/// </summary>
public sealed record OptionalVersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int? Version);

/// <summary>
/// An item to transfer a text document from the client to the server.
/// </summary>
public sealed record TextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// A text edit applicable to a text document.
/// </summary>
public sealed record TextEdit(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("newText")] string NewText);

/// <summary>
/// Represents a diagnostic, such as a compiler error or warning.
/// </summary>
public sealed record Diagnostic
{
    [JsonPropertyName("range")]
    public required Range Range { get; init; }

    [JsonPropertyName("severity")]
    public DiagnosticSeverity? Severity { get; init; }

    [JsonPropertyName("code")]
    public object? Code { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("relatedInformation")]
    public IReadOnlyList<DiagnosticRelatedInformation>? RelatedInformation { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<DiagnosticTag>? Tags { get; init; }
}

/// <summary>
/// Represents a related message and source code location for a diagnostic.
/// </summary>
public sealed record DiagnosticRelatedInformation(
    [property: JsonPropertyName("location")] Location Location,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Represents programming constructs like variables, classes, interfaces etc.
/// Document symbols can be hierarchical.
/// </summary>
public sealed record DocumentSymbol
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("kind")]
    public required SymbolKind Kind { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<SymbolTag>? Tags { get; init; }

    [JsonPropertyName("deprecated")]
    public bool? Deprecated { get; init; }

    [JsonPropertyName("range")]
    public required Range Range { get; init; }

    [JsonPropertyName("selectionRange")]
    public required Range SelectionRange { get; init; }

    [JsonPropertyName("children")]
    public IReadOnlyList<DocumentSymbol>? Children { get; init; }
}

/// <summary>
/// Represents information about programming constructs like variables, classes, interfaces etc.
/// Legacy type - prefer DocumentSymbol.
/// </summary>
public sealed record SymbolInformation
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required SymbolKind Kind { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<SymbolTag>? Tags { get; init; }

    [JsonPropertyName("deprecated")]
    public bool? Deprecated { get; init; }

    [JsonPropertyName("location")]
    public required Location Location { get; init; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; init; }
}

/// <summary>
/// The content of a Hover or markup response.
/// </summary>
public sealed record MarkupContent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// The result of a hover request.
/// </summary>
public sealed record Hover
{
    [JsonPropertyName("contents")]
    public required object Contents { get; init; }

    [JsonPropertyName("range")]
    public Range? Range { get; init; }
}

/// <summary>
/// A workspace edit represents changes to many resources managed in the workspace.
/// </summary>
public sealed record WorkspaceEdit
{
    [JsonPropertyName("changes")]
    public Dictionary<string, IReadOnlyList<TextEdit>>? Changes { get; init; }

    [JsonPropertyName("documentChanges")]
    public IReadOnlyList<object>? DocumentChanges { get; init; }
}

/// <summary>
/// A text document edit describes changes to an existing text document.
/// </summary>
public sealed record TextDocumentEdit(
    [property: JsonPropertyName("textDocument")] OptionalVersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("edits")] IReadOnlyList<TextEdit> Edits);

/// <summary>
/// Represents a command that can be executed by the language server.
/// </summary>
public sealed record Command(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("command")] string CommandIdentifier,
    [property: JsonPropertyName("arguments")] IReadOnlyList<object>? Arguments);
