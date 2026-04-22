// Line Edit Tools - Line-level file editing
// Tools for: delete_lines, insert_at_line, replace_lines

namespace Serena.Core.Tools;

[CanEdit]
public sealed class DeleteLinesTool : ToolBase
{
    public DeleteLinesTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Deletes a range of lines (1-based, inclusive) from a file.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("start_line", "The 1-based line number of the first line to delete.", typeof(int), Required: true),
        new("end_line", "The 1-based line number of the last line to delete (inclusive).", typeof(int), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int startLine = GetRequired<int>(arguments, "start_line");
        int endLine = GetRequired<int>(arguments, "end_line");

        if (startLine < 1)
        {
            throw new ArgumentException("start_line must be >= 1.");
        }

        if (endLine < startLine)
        {
            throw new ArgumentException("end_line must be >= start_line.");
        }

        string absolutePath = ResolvePath(relativePath);
        var lines = new List<string>(await File.ReadAllLinesAsync(absolutePath, ct));

        int clampedStart = Math.Min(startLine - 1, lines.Count);
        int clampedEnd = Math.Min(endLine, lines.Count);
        int deleteCount = clampedEnd - clampedStart;

        if (deleteCount <= 0)
        {
            return $"No lines deleted in {relativePath} (file has {lines.Count} lines).";
        }

        lines.RemoveRange(clampedStart, deleteCount);
        string newContent = string.Join(Environment.NewLine, lines);
        if (lines.Count > 0)
        {
            newContent += Environment.NewLine;
        }

        await File.WriteAllTextAsync(absolutePath, newContent, ct);
        await TryNotifyLspAsync(absolutePath, newContent, ct);

        return $"Deleted {deleteCount} line(s) ({startLine}-{startLine + deleteCount - 1}) from {relativePath}.";
    }

    private async Task TryNotifyLspAsync(string absolutePath, string content, CancellationToken ct)
    {
        try
        {
            var lsp = await Context.Agent.GetLanguageServerForFileAsync(absolutePath, ct);
            if (lsp is not null)
            {
                await lsp.NotifyFileChangedAsync(absolutePath, content);
            }
        }
        catch
        {
            // LSP notification is best-effort
        }
    }
}

[CanEdit]
public sealed class InsertAtLineTool : ToolBase
{
    public InsertAtLineTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Inserts content before the specified line number (1-based). Line 1 inserts at the beginning.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("line_number", "The 1-based line number before which to insert content.", typeof(int), Required: true),
        new("content", "The content to insert (may contain multiple lines).", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int lineNumber = GetRequired<int>(arguments, "line_number");
        string content = GetRequired<string>(arguments, "content");

        if (lineNumber < 1)
        {
            throw new ArgumentException("line_number must be >= 1.");
        }

        string absolutePath = ResolvePath(relativePath);
        var lines = new List<string>(await File.ReadAllLinesAsync(absolutePath, ct));

        string[] newLines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int insertIndex = Math.Min(lineNumber - 1, lines.Count);
        lines.InsertRange(insertIndex, newLines);

        string newContent = string.Join(Environment.NewLine, lines);
        if (lines.Count > 0)
        {
            newContent += Environment.NewLine;
        }

        await File.WriteAllTextAsync(absolutePath, newContent, ct);
        await TryNotifyLspAsync(absolutePath, newContent, ct);

        return $"Inserted {newLines.Length} line(s) at line {insertIndex + 1} in {relativePath}.";
    }

    private async Task TryNotifyLspAsync(string absolutePath, string content, CancellationToken ct)
    {
        try
        {
            var lsp = await Context.Agent.GetLanguageServerForFileAsync(absolutePath, ct);
            if (lsp is not null)
            {
                await lsp.NotifyFileChangedAsync(absolutePath, content);
            }
        }
        catch
        {
            // LSP notification is best-effort
        }
    }
}

[CanEdit]
public sealed class ReplaceLinesTool : ToolBase
{
    public ReplaceLinesTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Replaces a range of lines (1-based, inclusive) in a file with new content.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("start_line", "The 1-based line number of the first line to replace.", typeof(int), Required: true),
        new("end_line", "The 1-based line number of the last line to replace (inclusive).", typeof(int), Required: true),
        new("new_content", "The replacement content (may contain multiple lines).", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int startLine = GetRequired<int>(arguments, "start_line");
        int endLine = GetRequired<int>(arguments, "end_line");
        string newContent = GetRequired<string>(arguments, "new_content");

        if (startLine < 1)
        {
            throw new ArgumentException("start_line must be >= 1.");
        }

        if (endLine < startLine)
        {
            throw new ArgumentException("end_line must be >= start_line.");
        }

        string absolutePath = ResolvePath(relativePath);
        var lines = new List<string>(await File.ReadAllLinesAsync(absolutePath, ct));

        int clampedStart = Math.Min(startLine - 1, lines.Count);
        int clampedEnd = Math.Min(endLine, lines.Count);
        int deleteCount = clampedEnd - clampedStart;

        if (deleteCount > 0)
        {
            lines.RemoveRange(clampedStart, deleteCount);
        }

        string[] replacementLines = newContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines.InsertRange(clampedStart, replacementLines);

        string fileContent = string.Join(Environment.NewLine, lines);
        if (lines.Count > 0)
        {
            fileContent += Environment.NewLine;
        }

        await File.WriteAllTextAsync(absolutePath, fileContent, ct);
        await TryNotifyLspAsync(absolutePath, fileContent, ct);

        return $"Replaced {deleteCount} line(s) with {replacementLines.Length} line(s) at lines {startLine}-{startLine + deleteCount - 1} in {relativePath}.";
    }

    private async Task TryNotifyLspAsync(string absolutePath, string content, CancellationToken ct)
    {
        try
        {
            var lsp = await Context.Agent.GetLanguageServerForFileAsync(absolutePath, ct);
            if (lsp is not null)
            {
                await lsp.NotifyFileChangedAsync(absolutePath, content);
            }
        }
        catch
        {
            // LSP notification is best-effort
        }
    }
}
