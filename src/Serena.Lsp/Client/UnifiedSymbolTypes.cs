// Unified Symbol Types - Ported from solidlsp/ls_types.py
// Phase 3A: UnifiedSymbolInformation and related types

using System.Text.Json.Serialization;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Lsp.Client;

/// <summary>
/// Unified symbol information combining DocumentSymbol and SymbolInformation.
/// Adds Serena-specific fields like parent references and overload indices.
/// Ported from solidlsp/ls_types.py UnifiedSymbolInformation.
/// </summary>
public sealed class UnifiedSymbolInformation
{
    public required string Name { get; set; }
    public required SymbolKind Kind { get; set; }

    [JsonPropertyName("location")]
    public Location? Location { get; set; }

    [JsonPropertyName("range")]
    public LspRange? BodyRange { get; set; }

    [JsonPropertyName("selectionRange")]
    public LspRange? SelectionRange { get; set; }

    public string? Detail { get; set; }
    public string? ContainerName { get; set; }
    public bool Deprecated { get; set; }

    /// <summary>
    /// Children symbols (e.g., methods within a class).
    /// </summary>
    public List<UnifiedSymbolInformation> Children { get; set; } = [];

    /// <summary>
    /// Parent symbol reference (Serena extension, not part of LSP).
    /// </summary>
    [JsonIgnore]
    public UnifiedSymbolInformation? Parent { get; set; }

    /// <summary>
    /// Overload index for methods with the same name and parent (0-based).
    /// Null if no overloads exist.
    /// </summary>
    public int? OverloadIndex { get; set; }

    /// <summary>
    /// The body of the symbol (lazy-loaded from file buffer).
    /// </summary>
    [JsonIgnore]
    public SymbolBody? Body { get; set; }

    /// <summary>
    /// The name path (e.g., "MyClass/myMethod") for unique identification.
    /// </summary>
    [JsonIgnore]
    public string NamePath
    {
        get
        {
            string name = OverloadIndex.HasValue ? $"{Name}[{OverloadIndex.Value}]" : Name;
            return Parent is not null ? $"{Parent.NamePath}/{name}" : name;
        }
    }

    /// <summary>
    /// Start line of the symbol body (0-based).
    /// </summary>
    public int? StartLine => BodyRange?.Start.Line ?? Location?.Range.Start.Line;

    /// <summary>
    /// End line of the symbol body (0-based).
    /// </summary>
    public int? EndLine => BodyRange?.End.Line ?? Location?.Range.End.Line;

    /// <summary>
    /// Normalizes a symbol name that may contain signature decoration (e.g. from Roslyn).
    /// Returns the bare name and any extracted detail.
    /// </summary>
    internal static (string BaseName, string? Detail) NormalizeSymbolName(string name)
    {
        // Method pattern: name contains '(' → baseName is everything before first '('
        int parenIdx = name.IndexOf('(');
        if (parenIdx >= 0)
        {
            return (name[..parenIdx], name[parenIdx..]);
        }

        // Property pattern: name contains ' : ' but no '(' → baseName is everything before first ' : '
        int colonIdx = name.IndexOf(" : ", StringComparison.Ordinal);
        if (colonIdx >= 0)
        {
            return (name[..colonIdx], name[colonIdx..]);
        }

        return (name, null);
    }

    /// <summary>
    /// Creates a UnifiedSymbolInformation from a DocumentSymbol.
    /// </summary>
    public static UnifiedSymbolInformation FromDocumentSymbol(
        DocumentSymbol ds, string uri, string? relativePath)
    {
        var (baseName, extractedDetail) = NormalizeSymbolName(ds.Name);

        var symbol = new UnifiedSymbolInformation
        {
            Name = baseName,
            Kind = ds.Kind,
            Detail = ds.Detail ?? extractedDetail,
            Deprecated = ds.Deprecated ?? false,
            BodyRange = ds.Range,
            SelectionRange = ds.SelectionRange,
            Location = new Location(uri, ds.Range),
        };

        if (ds.Children is not null)
        {
            foreach (var child in ds.Children)
            {
                var childSymbol = FromDocumentSymbol(child, uri, relativePath);
                childSymbol.Parent = symbol;
                symbol.Children.Add(childSymbol);
            }
        }

        AssignOverloadIndices(symbol.Children);
        return symbol;
    }

    /// <summary>
    /// Creates a UnifiedSymbolInformation from a SymbolInformation.
    /// </summary>
    public static UnifiedSymbolInformation FromSymbolInformation(SymbolInformation si)
    {
        var (baseName, detail) = NormalizeSymbolName(si.Name);
        return new UnifiedSymbolInformation
        {
            Name = baseName,
            Detail = detail,
            Kind = si.Kind,
            ContainerName = si.ContainerName,
            Deprecated = si.Deprecated ?? false,
            Location = si.Location,
            BodyRange = si.Location.Range,
            SelectionRange = si.Location.Range,
        };
    }

    /// <summary>
    /// Assigns overload indices to children that share the same name.
    /// </summary>
    private static void AssignOverloadIndices(List<UnifiedSymbolInformation> symbols)
    {
        var nameGroups = symbols.GroupBy(s => s.Name).Where(g => g.Count() > 1);
        foreach (var group in nameGroups)
        {
            int idx = 0;
            foreach (var symbol in group)
            {
                symbol.OverloadIndex = idx++;
            }
        }
    }
}

/// <summary>
/// A reference found within a symbol context.
/// </summary>
public sealed record ReferenceInSymbol
{
    public required UnifiedSymbolInformation Symbol { get; init; }
    public required int Line { get; init; }
    public required int Character { get; init; }
}
