// CodeEditor - Phase D2
// Provides file editing operations with LSP synchronization.
// Ported from serena/code_editor.py LanguageServerCodeEditor.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serena.Core.Editor;
using Serena.Core.Tools;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;

namespace Serena.Core.Editor;

/// <summary>
/// Interface for code editing operations that coordinate with the LSP.
/// </summary>
public interface ICodeEditor
{
    /// <summary>
    /// Replaces the body of a symbol at the given name path.
    /// </summary>
    Task<string> ReplaceSymbolBodyAsync(
        string namePath, string relativePath, string newBody, CancellationToken ct = default);

    /// <summary>
    /// Inserts content before the definition of a symbol.
    /// </summary>
    Task<string> InsertBeforeSymbolAsync(
        string namePath, string relativePath, string body, CancellationToken ct = default);

    /// <summary>
    /// Inserts content after a symbol definition.
    /// </summary>
    Task<string> InsertAfterSymbolAsync(
        string namePath, string relativePath, string body, CancellationToken ct = default);

    /// <summary>
    /// Replaces content in a file using literal or regex matching.
    /// </summary>
    Task<string> ReplaceContentAsync(
        string relativePath, string needle, string replacement, string mode,
        bool allowMultipleOccurrences = false, CancellationToken ct = default);

    /// <summary>
    /// Renames a symbol across the codebase using LSP rename.
    /// </summary>
    Task<string> RenameSymbolAsync(
        string namePath, string relativePath, string newName, CancellationToken ct = default);

    /// <summary>
    /// Deletes a symbol if no references exist, otherwise returns the list of references.
    /// </summary>
    Task<string> SafeDeleteSymbolAsync(
        string namePath, string relativePath, CancellationToken ct = default);
}

/// <summary>
/// Production implementation using LSP client and SymbolRetriever.
/// Edits are made to the file on disk and synchronized to the language server.
/// </summary>
public sealed class LanguageServerCodeEditor : ICodeEditor
{
    private readonly ISymbolRetriever _symbolRetriever;
    private readonly LspClient _lsp;
    private readonly string _projectRoot;
    private readonly ILogger _logger;

    public LanguageServerCodeEditor(
        ISymbolRetriever symbolRetriever,
        LspClient lsp,
        string projectRoot,
        ILogger logger)
    {
        _symbolRetriever = symbolRetriever;
        _lsp = lsp;
        _projectRoot = Path.GetFullPath(projectRoot);
        _logger = logger;
    }

    public async Task<string> ReplaceSymbolBodyAsync(
        string namePath, string relativePath, string newBody, CancellationToken ct)
    {
        var symbol = await FindUniqueSymbolAsync(namePath, relativePath, ct);

        if (symbol.BodyLocation is null)
        {
            throw new InvalidOperationException(
                $"Symbol '{namePath}' does not have a body location for replacement.");
        }

        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);
        string[] lines = content.Split('\n');

        var loc = symbol.BodyLocation;

        // Calculate character offsets from line/column positions
        int startOffset = GetOffset(lines, loc.StartLine, loc.StartColumn);
        int endOffset = GetOffset(lines, loc.EndLine, loc.EndColumn);

        // Replace the body text
        string stripped = newBody.Trim();
        string newContent = content[..startOffset] + stripped + content[endOffset..];

        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Replaced body of '{namePath}' in {relativePath}";
    }

    public async Task<string> InsertBeforeSymbolAsync(
        string namePath, string relativePath, string body, CancellationToken ct)
    {
        var symbol = await FindUniqueSymbolAsync(namePath, relativePath, ct);

        if (symbol.BodyLocation is null)
        {
            throw new InvalidOperationException(
                $"Symbol '{namePath}' does not have a body location.");
        }

        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);
        string[] lines = content.Split('\n');

        int insertLine = symbol.BodyLocation.StartLine;

        // Count original trailing newlines (minus 1 for the mandatory trailing EOL)
        int originalTrailingNewlines = CountTrailingNewlines(body) - 1;

        // Ensure body ends with exactly one newline
        body = body.TrimEnd() + Environment.NewLine;

        // Add suitable number of trailing blank lines:
        // at least 1 for definition symbols, otherwise as many as the caller provided
        int minTrailingEmptyLines = IsDefinitionSymbol(symbol) ? 1 : 0;
        int numTrailingNewlines = Math.Max(minTrailingEmptyLines, originalTrailingNewlines);
        for (int i = 0; i < numTrailingNewlines; i++)
        {
            body += Environment.NewLine;
        }

        var lineList = new List<string>(lines);
        lineList.Insert(insertLine, body.TrimEnd('\r', '\n'));

        string newContent = string.Join("\n", lineList);
        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Inserted content before '{namePath}' in {relativePath}";
    }

    public async Task<string> InsertAfterSymbolAsync(
        string namePath, string relativePath, string body, CancellationToken ct)
    {
        var symbol = await FindUniqueSymbolAsync(namePath, relativePath, ct);

        if (symbol.BodyLocation is null)
        {
            throw new InvalidOperationException(
                $"Symbol '{namePath}' does not have a body location.");
        }

        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);
        string[] lines = content.Split('\n');

        int insertAfterLine = symbol.BodyLocation.EndLine;

        // Ensure body ends with at least one newline
        if (!body.EndsWith('\n'))
        {
            body += Environment.NewLine;
        }

        // Count and preserve caller's leading newlines, enforcing minimum for definition symbols
        int originalLeadingNewlines = CountLeadingNewlines(body);
        body = body.TrimStart('\r', '\n');
        int minEmptyLines = IsDefinitionSymbol(symbol) ? 1 : 0;
        int numLeadingEmptyLines = Math.Max(minEmptyLines, originalLeadingNewlines);
        if (numLeadingEmptyLines > 0)
        {
            body = new string('\n', numLeadingEmptyLines) + body;
        }

        // Ensure exactly one trailing newline
        body = body.TrimEnd('\r', '\n') + Environment.NewLine;

        var lineList = new List<string>(lines);
        if (insertAfterLine + 1 <= lineList.Count)
        {
            lineList.Insert(insertAfterLine + 1, body.TrimEnd('\r', '\n'));
        }
        else
        {
            lineList.Add(body.TrimEnd('\r', '\n'));
        }

        string newContent = string.Join("\n", lineList);
        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Inserted content after '{namePath}' in {relativePath}";
    }

    public async Task<string> ReplaceContentAsync(
        string relativePath, string needle, string replacement, string mode,
        bool allowMultipleOccurrences, CancellationToken ct)
    {
        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);

        var (newContent, count) = string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase)
            ? ReplaceWithRegex(content, needle, replacement, allowMultipleOccurrences, relativePath)
            : ReplaceWithLiteral(content, needle, replacement, allowMultipleOccurrences, relativePath);

        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Replaced {count} occurrence(s) in {relativePath}";
    }

    private static (string NewContent, int Count) ReplaceWithRegex(
        string content, string needle, string replacement, bool allowMultiple, string relativePath)
    {
        string normalizedReplacement = TextReplacementHelper.NormalizeBackreferences(replacement);
        normalizedReplacement = TextReplacementHelper.NormalizeLineEndings(normalizedReplacement, content);
        var regex = TextReplacementHelper.CreateSearchRegex(needle);
        int count = regex.Matches(content).Count;

        ValidateOccurrenceCount(count, allowMultiple, relativePath,
            $"Pattern not found in {relativePath}: {needle}");

        return (regex.Replace(content, normalizedReplacement), count);
    }

    private static (string NewContent, int Count) ReplaceWithLiteral(
        string content, string needle, string replacement, bool allowMultiple, string relativePath)
    {
        string normalizedNeedle = TextReplacementHelper.NormalizeLineEndings(needle, content);
        string normalizedRepl = TextReplacementHelper.NormalizeLineEndings(replacement, content);

        int count = TextReplacementHelper.CountOccurrences(content, normalizedNeedle);

        ValidateOccurrenceCount(count, allowMultiple, relativePath,
            $"Text not found in {relativePath}: {Truncate(normalizedNeedle, 100)}");

        string newContent = allowMultiple
            ? content.Replace(normalizedNeedle, normalizedRepl)
            : TextReplacementHelper.ReplaceFirst(content, normalizedNeedle, normalizedRepl);

        return (newContent, count);
    }

    private static void ValidateOccurrenceCount(
        int count, bool allowMultiple, string relativePath, string notFoundMessage)
    {
        if (count == 0)
        {
            throw new InvalidOperationException(notFoundMessage);
        }

        if (count > 1 && !allowMultiple)
        {
            throw new InvalidOperationException(
                $"Text matches {count} occurrences in {relativePath}, " +
                "but allow_multiple_occurrences is false.");
        }
    }

    public async Task<string> RenameSymbolAsync(
        string namePath, string relativePath, string newName, CancellationToken ct)
    {
        var symbol = await FindUniqueSymbolAsync(namePath, relativePath, ct);

        if (symbol.BodyLocation is null)
        {
            throw new InvalidOperationException(
                $"Symbol '{namePath}' does not have a valid position for renaming.");
        }

        string absolutePath = ResolvePath(relativePath);

        // Use selection range (name position) if available
        int line = symbol.Raw.SelectionRange?.Start.Line ?? symbol.BodyLocation.StartLine;
        int character = symbol.Raw.SelectionRange?.Start.Character ?? symbol.BodyLocation.StartColumn;

        await _lsp.OpenFileAsync(absolutePath);
        var workspaceEdit = await _lsp.RequestRenameAsync(absolutePath, line, character, newName, ct: ct);

        if (workspaceEdit is null)
        {
            throw new InvalidOperationException(
                $"Language server returned no rename edits for '{namePath}'. " +
                "The symbol might not support renaming.");
        }

        int changeCount = await ApplyWorkspaceEditAsync(workspaceEdit, ct);

        if (changeCount == 0)
        {
            throw new InvalidOperationException(
                $"Renaming '{namePath}' to '{newName}' resulted in no changes.");
        }

        return $"Successfully renamed '{namePath}' to '{newName}' ({changeCount} changes applied)";
    }

    public async Task<string> SafeDeleteSymbolAsync(
        string namePath, string relativePath, CancellationToken ct)
    {
        var symbol = await FindUniqueSymbolAsync(namePath, relativePath, ct);

        // Check for references
        var references = await _symbolRetriever.FindReferencesAsync(symbol, ct: ct);

        if (references.Count > 0)
        {
            var refList = references.Select(r =>
                $"  - {r.RelativePath}:{r.Line}" +
                (r.ContextSnippet is not null ? $" → {r.ContextSnippet}" : ""));
            return $"Cannot delete '{namePath}': {references.Count} reference(s) found:\n" +
                   string.Join("\n", refList);
        }

        if (symbol.BodyLocation is null)
        {
            throw new InvalidOperationException(
                $"Symbol '{namePath}' does not have a body location for deletion.");
        }

        // Delete the symbol
        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);
        string[] lines = content.Split('\n');

        int startLine = symbol.BodyLocation.StartLine;
        int endLine = symbol.BodyLocation.EndLine;

        var lineList = new List<string>(lines);
        int deleteCount = Math.Min(endLine - startLine + 1, lineList.Count - startLine);
        lineList.RemoveRange(startLine, deleteCount);

        string newContent = string.Join("\n", lineList);
        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Deleted symbol '{namePath}' from {relativePath}";
    }

    private async Task<LanguageServerSymbol> FindUniqueSymbolAsync(
        string namePath, string relativePath, CancellationToken ct)
    {
        var symbols = await _symbolRetriever.FindSymbolsByNamePathAsync(
            namePath, relativePath, ct: ct);

        if (symbols.Count == 0)
        {
            throw new InvalidOperationException(
                $"Symbol '{namePath}' not found in {relativePath}");
        }

        if (symbols.Count > 1)
        {
            var names = symbols.Select(s => s.NamePath);
            throw new InvalidOperationException(
                $"Multiple symbols match '{namePath}' in {relativePath}: " +
                string.Join(", ", names));
        }

        return symbols[0];
    }

    private string ResolvePath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
    }

    private async Task WriteAndSyncAsync(string absolutePath, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(absolutePath, content, ct);

        try
        {
            await _lsp.NotifyFileChangedAsync(absolutePath, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify LSP of file change: {Path} — LS may be desynchronized", absolutePath);
        }
    }

    private async Task<int> ApplyWorkspaceEditAsync(WorkspaceEdit edit, CancellationToken ct)
    {
        int changeCount = 0;

        if (edit.Changes is { } changes)
        {
            changeCount += await ApplyChangesAsync(changes, ct);
        }

        if (edit.DocumentChanges is { } docChanges)
        {
            changeCount += await ApplyDocumentChangesAsync(docChanges, ct);
        }

        return changeCount;
    }

    private async Task<int> ApplyChangesAsync(
        IDictionary<string, IReadOnlyList<TextEdit>> changes, CancellationToken ct)
    {
        int count = 0;
        foreach (var (uri, edits) in changes)
        {
            string filePath = LspClient.UriToPath(uri);
            await ApplyTextEditsAsync(filePath, edits, ct);
            count++;
        }
        return count;
    }

    private async Task<int> ApplyDocumentChangesAsync(
        IReadOnlyList<object> docChanges, CancellationToken ct)
    {
        int count = 0;
        foreach (var docChange in docChanges)
        {
            var textDocEdit = ResolveTextDocumentEdit(docChange);
            if (textDocEdit?.TextDocument?.Uri is not null && textDocEdit.Edits is not null)
            {
                string filePath = LspClient.UriToPath(textDocEdit.TextDocument.Uri);
                await ApplyTextEditsAsync(filePath, textDocEdit.Edits, ct);
                count++;
            }
        }
        return count;
    }

    private TextDocumentEdit? ResolveTextDocumentEdit(object docChange)
    {
        return docChange switch
        {
            TextDocumentEdit edit => edit,
            JObject jObj => jObj.ToObject<TextDocumentEdit>(),
            _ => LogUnrecognizedDocChange(docChange)
        };
    }

    private TextDocumentEdit? LogUnrecognizedDocChange(object docChange)
    {
        _logger.LogWarning("ResolveTextDocumentEdit: unrecognized docChange type {Type}: {Value}",
            docChange.GetType().Name, docChange);
        return null;
    }

    private async Task ApplyTextEditsAsync(
        string absolutePath, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        string content = await File.ReadAllTextAsync(absolutePath, ct);
        string[] lines = content.Split('\n');

        // Apply edits in reverse order to preserve positions
        var sortedEdits = edits
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character)
            .ToList();

        foreach (var edit in sortedEdits)
        {
            int startOffset = GetOffset(lines, edit.Range.Start.Line, edit.Range.Start.Character);
            int endOffset = GetOffset(lines, edit.Range.End.Line, edit.Range.End.Character);

            content = content[..startOffset] + edit.NewText + content[endOffset..];
            lines = content.Split('\n');
        }

        await WriteAndSyncAsync(absolutePath, content, ct);
    }

    internal static int GetOffset(string[] lines, int line, int character)
    {
        if (line >= lines.Length)
        {
            // Line is beyond end of file — clamp to total content length
            return lines.Sum(l => l.Length + 1);
        }

        int offset = 0;
        for (int i = 0; i < line; i++)
        {
            offset += lines[i].Length + 1; // +1 for newline
        }
        offset += Math.Min(character, lines[line].Length);
        return offset;
    }

    private static bool IsDefinitionSymbol(LanguageServerSymbol symbol)
    {
        return symbol.Kind is SymbolKind.Class or SymbolKind.Method or SymbolKind.Function
            or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum
            or SymbolKind.Module or SymbolKind.Namespace;
    }

    private static int CountLeadingNewlines(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
            else if (c != '\r')
            {
                break;
            }
        }

        return count;
    }

    private static int CountTrailingNewlines(string text)
    {
        int count = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '\n')
            {
                count++;
            }
            else if (text[i] != '\r')
            {
                break;
            }
        }

        return count;
    }


    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
