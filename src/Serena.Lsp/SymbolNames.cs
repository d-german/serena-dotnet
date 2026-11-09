namespace Serena.Lsp;

/// <summary>
/// Name forms shared by the index, the name path matcher and the CLI.
/// </summary>
public static class SymbolNames
{
    /// <summary>
    /// "ToResult&lt;T&gt;" becomes "ToResult"; a name without a type parameter list is returned as is.
    /// </summary>
    public static string StripTypeParameters(string name)
    {
        int lt = name.IndexOf('<');
        return lt > 0 && name.EndsWith('>') ? name[..lt] : name;
    }

    /// <summary>True for a declaration name that carries a type parameter list.</summary>
    public static bool HasTypeParameters(string name) =>
        name.IndexOf('<') > 0 && name.EndsWith('>');
}
