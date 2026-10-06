// FrameworkConditionalSymbols - outlines C# code that compiles only for .NET Framework

using System.Text.RegularExpressions;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Lsp.Client;

/// <summary>
/// Roslyn outlines files outside a loaded workspace as modern .NET, so a declaration inside
/// <c>#if NETFRAMEWORK</c>, <c>#if NET48</c>, or the <c>#else</c> of <c>#if NET</c> is inactive
/// code and never reaches the outline. In a codebase that multi-targets .NET Framework that
/// silently hides real types. For C# files whose conditionals test target-framework symbols,
/// this requests a second outline with the .NET Framework 4.8 symbols in effect and merges
/// the two, so the outline holds every declaration that exists in either build.
/// </summary>
public static class FrameworkConditionalSymbols
{
    private static readonly Regex FrameworkConditional = new(
        @"^[ \t]*#[ \t]*(?:if|elif)\b[^\r\n]*\bNET(?:FRAMEWORK|COREAPP|STANDARD|\d|\b)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // #undef also removes symbols supplied by the compilation, which is what lets a file
    // opened under Roslyn's modern-.NET miscellaneous project be read as a net48 build.
    private static readonly string[] Header = [.. BuildHeader()];

    private static readonly string HeaderText = string.Concat(Header.Select(line => line + "\n"));

    /// <summary>Lines prepended to the document for the .NET Framework outline.</summary>
    internal static int HeaderLineCount => Header.Length;

    /// <summary>
    /// Requests the outline of an open document and, for a C# file with target-framework
    /// conditionals, merges in its .NET Framework outline. The document's contents are
    /// restored in the language server before returning.
    /// </summary>
    public static async Task<IReadOnlyList<UnifiedSymbolInformation>> RequestAsync(
        LspClient client, string absolutePath, CancellationToken ct = default)
    {
        var buffer = client.Language == Language.CSharp
            ? client.FileBuffers.GetBuffer(LspClient.PathToUri(absolutePath))
            : null;
        if (buffer is null || !HasFrameworkConditional(buffer.ServerText ?? buffer.Contents))
        {
            return await client.RequestDocumentSymbolsAsync(absolutePath, ct).ConfigureAwait(false);
        }

        // Both outlines and the temporary edit happen under the document's lock: a second
        // caller must neither outline the header text nor read it as the text to restore.
        using var _ = await client.LockDocumentAsync(absolutePath, ct).ConfigureAwait(false);
        string original = buffer.ServerText ?? buffer.Contents;
        var symbols = await client.RequestDocumentSymbolsAsync(absolutePath, ct).ConfigureAwait(false);

        IReadOnlyList<UnifiedSymbolInformation> framework;
        await client.NotifyFileChangedAsync(absolutePath, HeaderText + original).ConfigureAwait(false);
        try
        {
            framework = await client.RequestDocumentSymbolsAsync(absolutePath, ct).ConfigureAwait(false);
        }
        finally
        {
            await client.NotifyFileChangedAsync(absolutePath, original).ConfigureAwait(false);
        }

        return Merge(symbols, ShiftLines(framework, -HeaderLineCount));
    }

    internal static bool HasFrameworkConditional(string source) => FrameworkConditional.IsMatch(source);

    /// <summary>
    /// Returns <paramref name="primary"/> plus every symbol of <paramref name="secondary"/>
    /// that it lacks, matched by name, kind and declaration position, recursively and in
    /// document order. The same declaration seen by both builds appears once; a type that
    /// only one build compiles, or a member with a different declaration per build, is kept.
    /// </summary>
    internal static List<UnifiedSymbolInformation> Merge(
        IReadOnlyList<UnifiedSymbolInformation> primary,
        IReadOnlyList<UnifiedSymbolInformation> secondary,
        UnifiedSymbolInformation? parent = null)
    {
        var merged = primary.ToList();
        foreach (var candidate in secondary)
        {
            var match = merged.FirstOrDefault(existing => SameDeclaration(existing, candidate));
            if (match is null)
            {
                candidate.Parent = parent;
                merged.Add(candidate);
            }
            else
            {
                match.Children = Merge(match.Children, candidate.Children, match);
            }
        }

        merged = [.. merged.OrderBy(s => s.StartLine ?? int.MaxValue)
                           .ThenBy(s => s.SelectionRange?.Start.Character ?? 0)];
        if (parent is not null)
        {
            UnifiedSymbolInformation.AssignOverloadIndices(merged);
        }
        return merged;
    }

    /// <summary>Copies <paramref name="symbols"/> with every range moved by <paramref name="lines"/>.</summary>
    internal static List<UnifiedSymbolInformation> ShiftLines(
        IReadOnlyList<UnifiedSymbolInformation> symbols, int lines, UnifiedSymbolInformation? parent = null)
    {
        var shifted = new List<UnifiedSymbolInformation>(symbols.Count);
        foreach (var symbol in symbols)
        {
            var copy = new UnifiedSymbolInformation
            {
                Name = symbol.Name,
                Kind = symbol.Kind,
                Detail = symbol.Detail,
                ContainerName = symbol.ContainerName,
                Deprecated = symbol.Deprecated,
                OverloadIndex = symbol.OverloadIndex,
                BodyRange = Shift(symbol.BodyRange, lines),
                SelectionRange = Shift(symbol.SelectionRange, lines),
                Location = symbol.Location is null
                    ? null
                    : symbol.Location with { Range = Shift(symbol.Location.Range, lines)! },
                Parent = parent,
            };
            copy.Children = ShiftLines(symbol.Children, lines, copy);
            shifted.Add(copy);
        }
        return shifted;
    }

    private static bool SameDeclaration(UnifiedSymbolInformation a, UnifiedSymbolInformation b) =>
        a.Name == b.Name
        && a.Kind == b.Kind
        && a.SelectionRange?.Start == b.SelectionRange?.Start;

    private static LspRange? Shift(LspRange? range, int lines) =>
        range is null
            ? null
            : new LspRange(
                range.Start with { Line = range.Start.Line + lines },
                range.End with { Line = range.End.Line + lines });

    private static IEnumerable<string> BuildHeader()
    {
        yield return "#undef NET";
        yield return "#undef NETCOREAPP";
        foreach (string version in new[] { "1_0", "1_1", "2_0", "2_1", "2_2", "3_0", "3_1" })
        {
            yield return $"#undef NETCOREAPP{version}_OR_GREATER";
        }
        for (int major = 5; major <= 12; major++)
        {
            yield return $"#undef NET{major}_0";
            yield return $"#undef NET{major}_0_OR_GREATER";
        }

        yield return "#define NETFRAMEWORK";
        yield return "#define NET48";
        foreach (string version in new[]
                 { "20", "30", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48" })
        {
            yield return $"#define NET{version}_OR_GREATER";
        }
    }
}
