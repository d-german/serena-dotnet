// CodeEditor - Phase D2
// Provides file editing operations with LSP synchronization.
// Ported from serena/code_editor.py LanguageServerCodeEditor.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serena.Core.Editor;
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
        string[] lines = await File.ReadAllLinesAsync(absolutePath, ct);

        var loc = symbol.BodyLocation;
        string content = string.Join(Environment.NewLine, lines);

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
        string[] lines = await File.ReadAllLinesAsync(absolutePath, ct);

        int insertLine = symbol.BodyLocation.StartLine;

        // Ensure body ends with newline and has appropriate trailing spacing
        body = body.TrimEnd() + Environment.NewLine;

        // Add a blank line separator for class/method definitions
        if (IsDefinitionSymbol(symbol))
        {
            body += Environment.NewLine;
        }

        var lineList = new List<string>(lines);
        lineList.Insert(insertLine, body.TrimEnd('\r', '\n'));

        string newContent = string.Join(Environment.NewLine, lineList);
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
        string[] lines = await File.ReadAllLinesAsync(absolutePath, ct);

        int insertAfterLine = symbol.BodyLocation.EndLine;

        // Ensure body ends with newline
        if (!body.EndsWith('\n'))
        {
            body += Environment.NewLine;
        }

        // Add leading blank line for definition symbols
        if (IsDefinitionSymbol(symbol))
        {
            body = Environment.NewLine + body;
        }

        var lineList = new List<string>(lines);
        if (insertAfterLine + 1 <= lineList.Count)
        {
            lineList.Insert(insertAfterLine + 1, body.TrimEnd('\r', '\n'));
        }
        else
        {
            lineList.Add(body.TrimEnd('\r', '\n'));
        }

        string newContent = string.Join(Environment.NewLine, lineList);
        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Inserted content after '{namePath}' in {relativePath}";
    }

    public async Task<string> ReplaceContentAsync(
        string relativePath, string needle, string replacement, string mode,
        bool allowMultipleOccurrences, CancellationToken ct)
    {
        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);

        string newContent;
        int count;

        if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            // Use $!N backreference syntax → convert to standard $N
            string normalizedReplacement = Regex.Replace(replacement, @"\$!(\d+)", @"$$$1");

            var regex = new Regex(needle, RegexOptions.Singleline | RegexOptions.Multiline);
            var matches = regex.Matches(content);
            count = matches.Count;

            if (count == 0)
            {
                throw new InvalidOperationException(
                    $"Pattern not found in {relativePath}: {needle}");
            }

            if (count > 1 && !allowMultipleOccurrences)
            {
                throw new InvalidOperationException(
                    $"Pattern matches {count} occurrences in {relativePath}, " +
                    "but allow_multiple_occurrences is false.");
            }

            newContent = regex.Replace(content, normalizedReplacement);
        }
        else
        {
            // Literal mode
            count = CountOccurrences(content, needle);

            if (count == 0)
            {
                throw new InvalidOperationException(
                    $"Text not found in {relativePath}: {Truncate(needle, 100)}");
            }

            if (count > 1 && !allowMultipleOccurrences)
            {
                throw new InvalidOperationException(
                    $"Text matches {count} occurrences in {relativePath}, " +
                    "but allow_multiple_occurrences is false.");
            }

            if (allowMultipleOccurrences)
            {
                newContent = content.Replace(needle, replacement);
            }
            else
            {
                int index = content.IndexOf(needle, StringComparison.Ordinal);
                newContent = content[..index] + replacement + content[(index + needle.Length)..];
            }
        }

        await WriteAndSyncAsync(absolutePath, newContent, ct);

        return $"Replaced {count} occurrence(s) in {relativePath}";
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
        string[] lines = await File.ReadAllLinesAsync(absolutePath, ct);

        int startLine = symbol.BodyLocation.StartLine;
        int endLine = symbol.BodyLocation.EndLine;

        var lineList = new List<string>(lines);
        int deleteCount = Math.Min(endLine - startLine + 1, lineList.Count - startLine);
        lineList.RemoveRange(startLine, deleteCount);

        string newContent = string.Join(Environment.NewLine, lineList);
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
            _logger.LogWarning(ex, "Failed to notify LSP of file change: {Path}", absolutePath);
        }
    }

    private async Task<int> ApplyWorkspaceEditAsync(WorkspaceEdit edit, CancellationToken ct)
    {
        int changeCount = 0;

        if (edit.Changes is { } changes)
        {
            foreach (var (uri, edits) in changes)
            {
                string filePath = LspClient.UriToPath(uri);
                await ApplyTextEditsAsync(filePath, edits, ct);
                changeCount++;
            }
        }

        if (edit.DocumentChanges is { } docChanges)
        {
            foreach (var docChange in docChanges)
            {
                if (docChange is TextDocumentEdit textDocEdit)
                {
                    string filePath = LspClient.UriToPath(textDocEdit.TextDocument.Uri);
                    await ApplyTextEditsAsync(filePath, textDocEdit.Edits, ct);
                    changeCount++;
                }
                else if (docChange is JObject jObj)
                {
                    var parsed = jObj.ToObject<TextDocumentEdit>();
                    if (parsed?.TextDocument?.Uri is not null && parsed.Edits is not null)
                    {
                        string filePath = LspClient.UriToPath(parsed.TextDocument.Uri);
                        await ApplyTextEditsAsync(filePath, parsed.Edits, ct);
                        changeCount++;
                    }
                }
            }
        }

        return changeCount;
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

    private static int GetOffset(string[] lines, int line, int character)
    {
        int offset = 0;
        for (int i = 0; i < line && i < lines.Length; i++)
        {
            offset += lines[i].Length + 1; // +1 for newline
        }
        if (line < lines.Length)
        {
            offset += Math.Min(character, lines[line].Length);
        }
        return offset;
    }

    private static bool IsDefinitionSymbol(LanguageServerSymbol symbol)
    {
        return symbol.Kind is SymbolKind.Class or SymbolKind.Method or SymbolKind.Function
            or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum
            or SymbolKind.Module or SymbolKind.Namespace;
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
