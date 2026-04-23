// File Tools - Phase 6B
// Tools for file operations: read_file, create_text_file, list_dir, find_file, search_for_pattern

using System.Collections.Concurrent;
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
        new("start_line", "The 0-based line index to start reading from. Default: 0 (beginning of file).", typeof(int), Required: false, DefaultValue: 0),
        new("end_line", "The 0-based line index to stop reading at (inclusive). -1 means end of file.", typeof(int), Required: false, DefaultValue: -1),
        new("max_answer_chars", "Max characters for the result. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int startLine = GetOptional(arguments, "start_line", 0);
        int endLine = GetOptional(arguments, "end_line", -1);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);

        string fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {relativePath}", relativePath);
        }

        string[] lines = await File.ReadAllLinesAsync(fullPath, GetProjectEncoding(), ct);
        if (startLine > 0 || endLine >= 0)
        {
            int end = endLine >= 0 ? Math.Min(endLine + 1, lines.Length) : lines.Length;
            int start = Math.Min(startLine, lines.Length);
            lines = lines[start..end];
        }

        string result = string.Join('\n', lines);
        return LimitLength(result, maxChars);
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

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
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

        await File.WriteAllTextAsync(fullPath, content, GetProjectEncoding(), ct);

        string answer = $"File created: {relativePath}.";
        if (willOverwrite)
        {
            answer += " Overwrote existing file.";
        }
        return answer;
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
            throw new DirectoryNotFoundException($"Directory not found: {relativePath}");
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
            throw new DirectoryNotFoundException($"Directory not found: {relativePath}");
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

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
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
        var regex = new Regex(pattern, RegexOptions.Singleline | RegexOptions.Multiline, TimeSpan.FromSeconds(10));

        var filesToSearch = EnumerateSearchFiles(searchRoot, projectRoot, includeGlob, excludeGlob, codeFilesOnly);
        var results = await CollectMatchesAsync(filesToSearch, regex, contextBefore, contextAfter, projectRoot, ct);

        string result = ToJson(results);
        return LimitLength(result, maxChars);
    }

    private IEnumerable<string> EnumerateSearchFiles(
        string searchRoot, string projectRoot, string includeGlob, string excludeGlob, bool codeFilesOnly)
    {
        IEnumerable<string> files = File.Exists(searchRoot)
            ? [searchRoot]
            : Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Where(f => !IsPathIgnored(Path.GetRelativePath(projectRoot, f)));

        if (!string.IsNullOrEmpty(includeGlob))
        {
            files = files.Where(f => FileSystemHelpers.MatchesGlob(
                Path.GetRelativePath(projectRoot, f).Replace('\\', '/'), includeGlob));
        }

        if (!string.IsNullOrEmpty(excludeGlob))
        {
            files = files.Where(f => !FileSystemHelpers.MatchesGlob(
                Path.GetRelativePath(projectRoot, f).Replace('\\', '/'), excludeGlob));
        }

        if (codeFilesOnly)
        {
            files = files.Where(LanguageServerSymbolRetriever.CanAnalyzeFile);
        }

        return files;
    }

    private async Task<Dictionary<string, List<string>>> CollectMatchesAsync(
        IEnumerable<string> filesToSearch, Regex regex, int contextBefore, int contextAfter,
        string projectRoot, CancellationToken ct)
    {
        var results = new ConcurrentDictionary<string, List<string>>();
        // Parallel scanning: file I/O + regex are CPU/IO bound; high parallelism
        // is safe here (no LSP involvement). Default to ProcessorCount, override
        // with SERENA_SEARCH_PARALLELISM env var (clamped 1-32).
        int parallelism = GetSearchParallelism();

        await Parallel.ForEachAsync(
            filesToSearch,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (filePath, token) =>
            {
                var fileMatches = await FindMatchesInFileAsync(filePath, regex, contextBefore, contextAfter, token);
                if (fileMatches is not null)
                {
                    string rel = Path.GetRelativePath(projectRoot, filePath).Replace('\\', '/');
                    results[rel] = fileMatches;
                }
            });

        return new Dictionary<string, List<string>>(results);
    }

    private static int GetSearchParallelism()
    {
        string? raw = Environment.GetEnvironmentVariable("SERENA_SEARCH_PARALLELISM");
        if (int.TryParse(raw, out int value))
        {
            return Math.Clamp(value, 1, 32);
        }
        return Math.Min(Environment.ProcessorCount, 16);
    }

    private async Task<List<string>?> FindMatchesInFileAsync(
        string filePath, Regex regex, int contextBefore, int contextAfter, CancellationToken ct)
    {
        try
        {
            string content = await File.ReadAllTextAsync(filePath, GetProjectEncoding(), ct);
            var matches = regex.Matches(content);
            if (matches.Count == 0)
            {
                return null;
            }

            string[] lines = content.Split('\n');
            var fileMatches = new List<string>();

            foreach (Match match in matches)
            {
                fileMatches.Add(FormatMatchContext(content, lines, match, contextBefore, contextAfter));
            }

            return fileMatches;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatMatchContext(
        string content, string[] lines, Match match, int contextBefore, int contextAfter)
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
        return sb.ToString().TrimEnd();
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

            ClassifyEntry(entry, relative, relativeTo, recursive, isIgnored, dirs, files);
        }

        return (dirs, files);
    }

    private static void ClassifyEntry(
        string entry, string relative, string relativeTo, bool recursive,
        Func<string, bool>? isIgnored, List<string> dirs, List<string> files)
    {
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

    public static bool MatchesGlob(string fileName, string pattern)
    {
        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }
}
