// File Tools - Phase 6B
// Tools for file operations: read_file, create_text_file, list_dir, find_file, search_for_pattern

using System.Text;
using System.Text.RegularExpressions;
using Serena.Core.Editor;

namespace Serena.Core.Tools;

public sealed class ReadFileTool : ToolBase
{
    public ReadFileTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Reads the given file or a chunk of it. Returns the full text of the file at the given relative path.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file to read.", typeof(string), Required: true),
        new("start_line", "The 0-based index of the first line to be retrieved.", typeof(int), Required: false, DefaultValue: 0),
        new("end_line", "The 0-based index of the last line to be retrieved (inclusive). -1 for end of file.", typeof(int), Required: false, DefaultValue: -1),
        new("max_answer_chars", "Max characters for the result. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int startLine = GetOptional(arguments, "start_line", 0);
        int endLine = GetOptional(arguments, "end_line", -1);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);

        string fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult($"Error: File not found: {relativePath}");
        }

        string[] lines = File.ReadAllLines(fullPath);
        if (startLine > 0 || endLine >= 0)
        {
            int end = endLine >= 0 ? Math.Min(endLine + 1, lines.Length) : lines.Length;
            int start = Math.Min(startLine, lines.Length);
            lines = lines[start..end];
        }

        string result = string.Join('\n', lines);
        if (maxChars > 0 && result.Length > maxChars)
        {
            return Task.FromResult(
                $"Error: File content ({result.Length} chars) exceeds max_answer_chars ({maxChars}). " +
                "Use start_line/end_line to read specific sections.");
        }

        return Task.FromResult(result);
    }
}

[CanEdit]
public sealed class CreateTextFileTool : ToolBase
{
    public CreateTextFileTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Write a new file or overwrite an existing file. Returns a message indicating success or failure.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file to create.", typeof(string), Required: true),
        new("content", "The content to write to the file.", typeof(string), Required: true),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        string content = GetRequired<string>(arguments, "content");

        string fullPath = ResolvePath(relativePath);
        bool willOverwrite = File.Exists(fullPath);

        string? dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, Encoding.UTF8);

        string answer = $"File created: {relativePath}.";
        if (willOverwrite)
        {
            answer += " Overwrote existing file.";
        }
        return Task.FromResult(answer);
    }
}

public sealed class ListDirTool : ToolBase
{
    public ListDirTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Lists files and directories in the given directory (optionally with recursion).";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the directory to list; pass '.' for project root.", typeof(string), Required: true),
        new("recursive", "Whether to scan subdirectories recursively.", typeof(bool), Required: true),
        new("skip_ignored_files", "Whether to skip files and directories that are ignored.", typeof(bool), Required: false, DefaultValue: false),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        bool recursive = GetOptional(arguments, "recursive", false);
        bool skipIgnored = GetOptional(arguments, "skip_ignored_files", false);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);

        string fullPath = ResolvePath(relativePath);
        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult(ToJson(new
            {
                error = $"Directory not found: {relativePath}",
                hint = "Check if the path is correct relative to the project root"
            }));
        }

        string projectRoot = RequireProjectRoot();
        var (dirs, files) = FileSystemHelpers.ScanDirectory(
            fullPath, projectRoot, recursive,
            skipIgnored ? IsPathIgnored : null);

        string result = ToJson(new { dirs, files });
        return Task.FromResult(LimitLength(result, maxChars));
    }
}

public sealed class FindFileTool : ToolBase
{
    public FindFileTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Finds non-gitignored files matching the given file mask within the given relative path.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("file_mask", "The filename or file mask (using * or ?) to search for.", typeof(string), Required: true),
        new("relative_path", "The relative path to the directory to search in; pass '.' for project root.", typeof(string), Required: true),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string fileMask = GetRequired<string>(arguments, "file_mask");
        string relativePath = GetOptional(arguments, "relative_path", ".");

        string fullPath = ResolvePath(relativePath);
        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult(ToJson(new { error = $"Directory not found: {relativePath}" }));
        }

        string projectRoot = RequireProjectRoot();
        var (_, allFiles) = FileSystemHelpers.ScanDirectory(
            fullPath, projectRoot, recursive: true,
            isIgnored: IsPathIgnored);

        var matchingFiles = allFiles
            .Where(f => FileSystemHelpers.MatchesGlob(Path.GetFileName(f), fileMask))
            .ToList();

        return Task.FromResult(ToJson(new { files = matchingFiles }));
    }
}

public sealed class SearchForPatternTool : ToolBase
{
    public SearchForPatternTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Searches for arbitrary patterns in the codebase using regex.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("substring_pattern", "Regular expression pattern to search for.", typeof(string), Required: true),
        new("relative_path", "Restrict search to this file or directory.", typeof(string), Required: false, DefaultValue: ""),
        new("context_lines_before", "Number of lines of context before each match.", typeof(int), Required: false, DefaultValue: 0),
        new("context_lines_after", "Number of lines of context after each match.", typeof(int), Required: false, DefaultValue: 0),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
        new("paths_include_glob", "Only search files whose name matches this glob pattern (e.g. '*.cs').", typeof(string), Required: false, DefaultValue: ""),
        new("paths_exclude_glob", "Exclude files whose name matches this glob pattern (e.g. '*.min.js').", typeof(string), Required: false, DefaultValue: ""),
        new("restrict_search_to_code_files", "When true, only search files that the language server can analyze.", typeof(bool), Required: false, DefaultValue: false),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string pattern = GetRequired<string>(arguments, "substring_pattern");
        string relativePath = GetOptional(arguments, "relative_path", "");
        int contextBefore = GetOptional(arguments, "context_lines_before", 0);
        int contextAfter = GetOptional(arguments, "context_lines_after", 0);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);
        string includeGlob = GetOptional(arguments, "paths_include_glob", "");
        string excludeGlob = GetOptional(arguments, "paths_exclude_glob", "");
        bool codeFilesOnly = GetOptional(arguments, "restrict_search_to_code_files", false);

        string projectRoot = RequireProjectRoot();
        string searchRoot = string.IsNullOrEmpty(relativePath)
            ? projectRoot
            : ResolvePath(relativePath);

        var regex = new Regex(pattern, RegexOptions.Singleline | RegexOptions.Multiline);
        var results = new Dictionary<string, List<string>>();

        IEnumerable<string> filesToSearch;
        if (File.Exists(searchRoot))
        {
            filesToSearch = [searchRoot];
        }
        else
        {
            filesToSearch = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Where(f => !IsPathIgnored(
                    Path.GetRelativePath(projectRoot, f)));
        }

        if (!string.IsNullOrEmpty(includeGlob))
        {
            filesToSearch = filesToSearch.Where(f => FileSystemHelpers.MatchesGlob(Path.GetFileName(f), includeGlob));
        }

        if (!string.IsNullOrEmpty(excludeGlob))
        {
            filesToSearch = filesToSearch.Where(f => !FileSystemHelpers.MatchesGlob(Path.GetFileName(f), excludeGlob));
        }

        if (codeFilesOnly)
        {
            filesToSearch = filesToSearch.Where(LanguageServerSymbolRetriever.CanAnalyzeFile);
        }

        foreach (string filePath in filesToSearch)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                string content = File.ReadAllText(filePath);
                var matches = regex.Matches(content);
                if (matches.Count == 0)
                {
                    continue;
                }

                string[] lines = content.Split('\n');
                var fileMatches = new List<string>();

                foreach (Match match in matches)
                {
                    int matchLine = content[..match.Index].Count(c => c == '\n');
                    int matchEndLine = matchLine + match.Value.Count(c => c == '\n');

                    int startLine = Math.Max(0, matchLine - contextBefore);
                    int endLine = Math.Min(lines.Length - 1, matchEndLine + contextAfter);

                    var sb = new StringBuilder();
                    for (int i = startLine; i <= endLine; i++)
                    {
                        string prefix = (i >= matchLine && i <= matchEndLine) ? "  > " : "    ";
                        sb.AppendLine($"{prefix}{i + 1,4}:{lines[i]}");
                    }
                    fileMatches.Add(sb.ToString().TrimEnd());
                }

                string rel = Path.GetRelativePath(projectRoot, filePath).Replace('\\', '/');
                results[rel] = fileMatches;
            }
            catch (Exception)
            {
                // Skip binary files or files that can't be read
            }
        }

        string result = ToJson(results);
        return Task.FromResult(LimitLength(result, maxChars));
    }
}

/// <summary>
/// Shared file system utilities used by file tools.
/// </summary>
internal static class FileSystemHelpers
{
    private static readonly string[] IgnoredPrefixes =
    [
        ".git/", ".git\\",
        "node_modules/", "node_modules\\",
        ".serena/", ".serena\\",
        "bin/", "bin\\",
        "obj/", "obj\\",
        "__pycache__/", "__pycache__\\",
    ];

    public static bool IsDefaultIgnored(string relativePath)
    {
        return IgnoredPrefixes.Any(prefix =>
            relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || relativePath is ".git" or "node_modules" or ".serena" or "bin" or "obj" or "__pycache__";
    }

    public static (List<string> Dirs, List<string> Files) ScanDirectory(
        string path, string relativeTo, bool recursive, Func<string, bool>? isIgnored = null)
    {
        var dirs = new List<string>();
        var files = new List<string>();

        if (!Directory.Exists(path))
        {
            return (dirs, files);
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            string relative = Path.GetRelativePath(relativeTo, entry).Replace('\\', '/');

            if (isIgnored?.Invoke(relative) == true)
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                dirs.Add(relative);
                if (recursive)
                {
                    var (subDirs, subFiles) = ScanDirectory(entry, relativeTo, true, isIgnored);
                    dirs.AddRange(subDirs);
                    files.AddRange(subFiles);
                }
            }
            else
            {
                files.Add(relative);
            }
        }

        return (dirs, files);
    }

    public static bool MatchesGlob(string fileName, string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}
