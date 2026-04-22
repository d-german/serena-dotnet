// Tools for: delete_lines, insert_at_line, replace_lines

using Microsoft.Extensions.Logging;

namespace Serena.Core.Tools;

[CanEdit]
[OptionalTool]
public sealed class DeleteLinesTool : ToolBase
{
    public DeleteLinesTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Deletes a range of lines (1-based, inclusive) from a file.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("start_line", "The 1-based line number of the first line to delete. Line 1 is the first line.", typeof(int), Required: true),
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
        var encoding = GetProjectEncoding();
        string rawContent = await File.ReadAllTextAsync(absolutePath, encoding, ct);
        var (lines, lineEnding, hasTrailingNewline) = LineEditHelpers.ParseFileContent(rawContent);

        Logger.LogDebug("DeleteLines: {Path} - {LineCount} lines, deleting {Start}-{End}",
            relativePath, lines.Count, startLine, endLine);

        int clampedStart = Math.Min(startLine - 1, lines.Count);
        int clampedEnd = Math.Min(endLine, lines.Count);
        int deleteCount = clampedEnd - clampedStart;

        if (deleteCount <= 0)
        {
            return $"No lines deleted in {relativePath} (file has {lines.Count} lines).";
        }

        lines.RemoveRange(clampedStart, deleteCount);
        string newContent = string.Join(lineEnding, lines);
        if (hasTrailingNewline)
        {
            newContent += lineEnding;
        }

        Logger.LogDebug("DeleteLines: {Path} - After: {LineCount} lines (deleted {DeleteCount})",
            relativePath, lines.Count, deleteCount);

        await File.WriteAllTextAsync(absolutePath, newContent, encoding, ct);
        await TryNotifyLspAsync(absolutePath, newContent, ct);

        return $"Deleted {deleteCount} line(s) ({startLine}-{startLine + deleteCount - 1}) from {relativePath}.";
    }
}

[CanEdit]
[OptionalTool]
public sealed class InsertAtLineTool : ToolBase
{
    public InsertAtLineTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Inserts content before the specified line number (1-based). Line 1 inserts at the beginning.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("line_number", "The 1-based line number before which to insert content. Line 1 inserts at the very beginning.", typeof(int), Required: true),
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
        var encoding = GetProjectEncoding();
        string rawContent = await File.ReadAllTextAsync(absolutePath, encoding, ct);
        var (lines, lineEnding, hasTrailingNewline) = LineEditHelpers.ParseFileContent(rawContent);

        string[] newLines = LineEditHelpers.SplitContentIntoLines(content);
        int insertIndex = Math.Min(lineNumber - 1, lines.Count);

        Logger.LogDebug("InsertAtLine: {Path} - {LineCount} lines, inserting {InsertCount} lines at {Line}",
            relativePath, lines.Count, newLines.Length, insertIndex + 1);

        lines.InsertRange(insertIndex, newLines);

        string newContent = string.Join(lineEnding, lines);
        if (hasTrailingNewline)
        {
            newContent += lineEnding;
        }

        Logger.LogDebug("InsertAtLine: {Path} - After: {LineCount} lines",
            relativePath, lines.Count);

        await File.WriteAllTextAsync(absolutePath, newContent, encoding, ct);
        await TryNotifyLspAsync(absolutePath, newContent, ct);

        return $"Inserted {newLines.Length} line(s) at line {insertIndex + 1} in {relativePath}.";
    }
}

[CanEdit]
[OptionalTool]
public sealed class ReplaceLinesTool : ToolBase
{
    public ReplaceLinesTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Replaces a range of lines (1-based, inclusive) in a file with new content.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file.", typeof(string), Required: true),
        new("start_line", "The 1-based line number of the first line to replace. Line 1 is the first line.", typeof(int), Required: true),
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
        var encoding = GetProjectEncoding();
        string rawContent = await File.ReadAllTextAsync(absolutePath, encoding, ct);
        var (lines, lineEnding, hasTrailingNewline) = LineEditHelpers.ParseFileContent(rawContent);

        int clampedStart = Math.Min(startLine - 1, lines.Count);
        int clampedEnd = Math.Min(endLine, lines.Count);
        int deleteCount = clampedEnd - clampedStart;

        string[] replacementLines = LineEditHelpers.SplitContentIntoLines(newContent);

        Logger.LogDebug("ReplaceLines: {Path} - {LineCount} lines, replacing {Start}-{End} ({DeleteCount} lines) with {InsertCount} new lines",
            relativePath, lines.Count, startLine, endLine, deleteCount, replacementLines.Length);

        if (deleteCount > 0)
        {
            lines.RemoveRange(clampedStart, deleteCount);
        }

        lines.InsertRange(clampedStart, replacementLines);

        string fileContent = string.Join(lineEnding, lines);
        if (hasTrailingNewline)
        {
            fileContent += lineEnding;
        }

        Logger.LogDebug("ReplaceLines: {Path} - After: {LineCount} lines (delta: {Delta})",
            relativePath, lines.Count, replacementLines.Length - deleteCount);

        await File.WriteAllTextAsync(absolutePath, fileContent, encoding, ct);
        await TryNotifyLspAsync(absolutePath, fileContent, ct);

        return $"Replaced {deleteCount} line(s) with {replacementLines.Length} line(s) at lines {startLine}-{startLine + deleteCount - 1} in {relativePath}.";
    }
}

file static class LineEditHelpers
{
    /// <summary>
    /// Parses file content, detecting line ending style and trailing newline.
    /// </summary>
    public static (List<string> Lines, string LineEnding, bool HasTrailingNewline) ParseFileContent(string content)
    {
        if (content.Length == 0)
        {
            return ([], "\n", false);
        }

        string lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";
        bool hasTrailingNewline = content.EndsWith('\n');

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Remove the empty trailing element that Split creates when content ends with \n
        if (lines.Count > 0 && lines[^1] == "" && hasTrailingNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return (lines, lineEnding, hasTrailingNewline);
    }

    /// <summary>
    /// Splits user-provided content into lines, stripping a trailing empty element
    /// that <c>Split('\n')</c> produces when content ends with <c>\n</c>.
    /// </summary>
    public static string[] SplitContentIntoLines(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Strip phantom trailing empty element: "abc\n".Split('\n') → ["abc", ""]
        if (lines.Length > 1 && lines[^1] == "")
        {
            return lines[..^1];
        }

        return lines;
    }
}
