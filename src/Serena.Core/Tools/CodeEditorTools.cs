// Code Editor & Symbol Retriever - Phase 6E
// Tools for symbol editing: replace_symbol_body, insert_before/after_symbol, replace_content, rename_symbol

namespace Serena.Core.Tools;

[SymbolicEdit]
public sealed class ReplaceSymbolBodyTool : ToolBase
{
    public ReplaceSymbolBodyTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Replaces the body of the symbol with the given name_path.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path", "Name path of the symbol to replace.", typeof(string), Required: true),
        new("relative_path", "The relative path to the file containing the symbol.", typeof(string), Required: true),
        new("body", "The new symbol body including the signature line.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
        string body = GetRequired<string>(arguments, "body");

        var editor = await RequireCodeEditorAsync(relativePath, ct);
        return await editor.ReplaceSymbolBodyAsync(namePath, relativePath, body, ct);
    }
}

[SymbolicEdit]
public sealed class InsertBeforeSymbolTool : ToolBase
{
    public InsertBeforeSymbolTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Inserts the given content before the beginning of the definition of the given symbol.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path", "Name path of the symbol before which to insert content.", typeof(string), Required: true),
        new("relative_path", "The relative path to the file containing the symbol.", typeof(string), Required: true),
        new("body", "The content to be inserted.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
        string body = GetRequired<string>(arguments, "body");

        var editor = await RequireCodeEditorAsync(relativePath, ct);
        return await editor.InsertBeforeSymbolAsync(namePath, relativePath, body, ct);
    }
}

[SymbolicEdit]
public sealed class InsertAfterSymbolTool : ToolBase
{
    public InsertAfterSymbolTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Use this to insert code after a class/method/function definition.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path", "Name path of the symbol after which to insert content.", typeof(string), Required: true),
        new("relative_path", "The relative path to the file containing the symbol.", typeof(string), Required: true),
        new("body", "The content to be inserted.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
        string body = GetRequired<string>(arguments, "body");

        var editor = await RequireCodeEditorAsync(relativePath, ct);
        return await editor.InsertAfterSymbolAsync(namePath, relativePath, body, ct);
    }
}

[CanEdit]
public sealed class ReplaceContentTool : ToolBase
{
    public ReplaceContentTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Replaces one or more occurrences of a given pattern in a file with new content.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("needle", "The string or regex pattern to search for.", typeof(string), Required: true),
        new("repl", "The replacement string.", typeof(string), Required: true),
        new("mode", "Either 'literal' or 'regex'.", typeof(string), Required: true),
        new("allow_multiple_occurrences", "Whether to allow replacing multiple occurrences.", typeof(bool), Required: false, DefaultValue: false),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        string needle = GetRequired<string>(arguments, "needle");
        string repl = GetRequired<string>(arguments, "repl");
        string mode = GetOptional(arguments, "mode", "literal");
        bool allowMultiple = GetOptional(arguments, "allow_multiple_occurrences", false);

        string absolutePath = ResolvePath(relativePath);
        string content = await File.ReadAllTextAsync(absolutePath, ct);

        string newContent;
        int count;

        if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            // Convert $!N backreference syntax to standard $N
            string normalizedRepl = System.Text.RegularExpressions.Regex.Replace(repl, @"\$!(\d+)", @"$$$1");
            var regex = new System.Text.RegularExpressions.Regex(
                needle, System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);
            count = regex.Matches(content).Count;

            if (count == 0)
            {
                throw new InvalidOperationException($"Pattern not found in {relativePath}: {needle}");
            }
            if (count > 1 && !allowMultiple)
            {
                throw new InvalidOperationException(
                    $"Pattern matches {count} occurrences in {relativePath}, but allow_multiple_occurrences is false.");
            }

            newContent = regex.Replace(content, normalizedRepl);
        }
        else
        {
            count = CountOccurrences(content, needle);

            if (count == 0)
            {
                throw new InvalidOperationException($"Text not found in {relativePath}");
            }
            if (count > 1 && !allowMultiple)
            {
                throw new InvalidOperationException(
                    $"Text matches {count} occurrences in {relativePath}, but allow_multiple_occurrences is false.");
            }

            if (allowMultiple)
            {
                newContent = content.Replace(needle, repl);
            }
            else
            {
                int index = content.IndexOf(needle, StringComparison.Ordinal);
                newContent = content[..index] + repl + content[(index + needle.Length)..];
            }
        }

        await File.WriteAllTextAsync(absolutePath, newContent, ct);

        // Try to notify LSP if available
        try
        {
            var lsp = await Context.Agent.GetLanguageServerForFileAsync(absolutePath, ct);
            if (lsp is not null)
            {
                await lsp.NotifyFileChangedAsync(absolutePath, newContent);
            }
        }
        catch
        {
            // LSP notification is best-effort
        }

        return $"Replaced {count} occurrence(s) in {relativePath}";
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
}

[SymbolicEdit]
public sealed class RenameSymbolTool : ToolBase
{
    public RenameSymbolTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Renames the symbol with the given name_path to new_name throughout the entire codebase.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path", "Name path of the symbol to rename.", typeof(string), Required: true),
        new("relative_path", "The relative path to the file containing the symbol.", typeof(string), Required: true),
        new("new_name", "The new name for the symbol.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
        string newName = GetRequired<string>(arguments, "new_name");

        var editor = await RequireCodeEditorAsync(relativePath, ct);
        return await editor.RenameSymbolAsync(namePath, relativePath, newName, ct);
    }
}

[SymbolicEdit]
public sealed class SafeDeleteSymbolTool : ToolBase
{
    public SafeDeleteSymbolTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Deletes the symbol if it is safe to do so (no references) or returns a list of references.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path_pattern", "Name path of the symbol to delete.", typeof(string), Required: true),
        new("relative_path", "The relative path to the file containing the symbol.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePathPattern = GetRequired<string>(arguments, "name_path_pattern");
        string relativePath = GetRequired<string>(arguments, "relative_path");

        var editor = await RequireCodeEditorAsync(relativePath, ct);
        return await editor.SafeDeleteSymbolAsync(namePathPattern, relativePath, ct);
    }
}
