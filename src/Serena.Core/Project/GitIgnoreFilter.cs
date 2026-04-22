// GitIgnore Integration - Phase B2
// Parses .gitignore files and provides path filtering

using System.Text.RegularExpressions;

namespace Serena.Core.Project;

/// <summary>
/// Interface for filtering paths based on ignore rules.
/// </summary>
public interface IIgnoreFilter
{
    /// <summary>
    /// Returns true if the given relative path should be ignored.
    /// </summary>
    bool IsIgnored(string relativePath);
}

/// <summary>
/// Parses .gitignore files and checks whether relative paths match the ignore patterns.
/// Supports negation (!pattern), directory-only patterns (pattern/), wildcards (*, **),
/// and nested .gitignore files.
/// </summary>
public sealed class GitIgnoreFilter : IIgnoreFilter
{
    private readonly List<GitIgnoreRule> _rules = [];

    /// <summary>
    /// Always-ignored directories that are excluded regardless of .gitignore content.
    /// </summary>
    private static readonly string[] BuiltInIgnored =
    [
        ".git", ".serena",
    ];

    private GitIgnoreFilter() { }

    /// <summary>
    /// Loads .gitignore rules from a project root directory.
    /// Reads the root .gitignore and any nested .gitignore files found by scanning the directory tree.
    /// </summary>
    public static GitIgnoreFilter LoadFromProjectRoot(string projectRoot)
    {
        var filter = new GitIgnoreFilter();

        // Load root .gitignore
        string rootGitignore = Path.Combine(projectRoot, ".gitignore");
        if (File.Exists(rootGitignore))
        {
            filter.AddRulesFromFile(rootGitignore, "");
        }

        // Scan for nested .gitignore files (skip built-in ignored dirs)
        ScanNestedGitignores(projectRoot, projectRoot, filter);

        return filter;
    }

    /// <summary>
    /// Creates a filter from raw .gitignore content (useful for testing).
    /// </summary>
    public static GitIgnoreFilter FromContent(string content, string basePath = "")
    {
        var filter = new GitIgnoreFilter();
        filter.AddRulesFromContent(content, basePath);
        return filter;
    }

    public bool IsIgnored(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        // Normalize to forward slashes
        string normalized = relativePath.Replace('\\', '/').TrimEnd('/');

        // Check built-in ignores
        string topDir = normalized.Split('/')[0];
        if (BuiltInIgnored.Any(d => string.Equals(d, topDir, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Apply rules in order (last match wins, negation can un-ignore)
        bool ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.Matches(normalized))
            {
                ignored = !rule.IsNegation;
            }
        }

        return ignored;
    }

    private void AddRulesFromFile(string filePath, string basePath)
    {
        string content = File.ReadAllText(filePath);
        AddRulesFromContent(content, basePath);
    }

    private void AddRulesFromContent(string content, string basePath)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            _rules.Add(GitIgnoreRule.Parse(line, basePath));
        }
    }

    private static void ScanNestedGitignores(string dir, string projectRoot, GitIgnoreFilter filter)
    {
        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(dir))
            {
                string dirName = Path.GetFileName(subDir);

                // Skip built-in ignored directories
                if (BuiltInIgnored.Any(d => string.Equals(d, dirName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Skip if this directory is already ignored
                string relDir = Path.GetRelativePath(projectRoot, subDir).Replace('\\', '/');
                if (filter.IsIgnored(relDir))
                {
                    continue;
                }

                string nestedGitignore = Path.Combine(subDir, ".gitignore");
                if (File.Exists(nestedGitignore))
                {
                    filter.AddRulesFromFile(nestedGitignore, relDir);
                }

                ScanNestedGitignores(subDir, projectRoot, filter);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Expected for directories we don't have read access to — silently skip
        }
    }
}

/// <summary>
/// A single rule from a .gitignore file.
/// </summary>
internal sealed class GitIgnoreRule
{
    public bool IsNegation { get; }
    public bool DirectoryOnly { get; }
    private readonly Regex _regex;
    private readonly string _basePath;

    private GitIgnoreRule(Regex regex, string basePath, bool isNegation, bool directoryOnly)
    {
        _regex = regex;
        _basePath = basePath;
        IsNegation = isNegation;
        DirectoryOnly = directoryOnly;
    }

    public static GitIgnoreRule Parse(string pattern, string basePath)
    {
        bool isNegation = false;
        bool directoryOnly = false;

        if (pattern.StartsWith('!'))
        {
            isNegation = true;
            pattern = pattern[1..];
        }

        if (pattern.EndsWith('/'))
        {
            directoryOnly = true;
            pattern = pattern.TrimEnd('/');
        }

        // If pattern contains a slash (not at end), it's relative to basePath
        bool isRooted = pattern.Contains('/');

        string regexPattern = ConvertGitIgnorePatternToRegex(pattern, basePath, isRooted);
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        return new GitIgnoreRule(regex, basePath, isNegation, directoryOnly);
    }

    public bool Matches(string relativePath)
    {
        // Check path prefix for nested .gitignore rules
        if (!string.IsNullOrEmpty(_basePath)
            && !relativePath.StartsWith(_basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // For directory-only rules, we match both the directory itself
        // and any path within it (since we can't easily check if a path is a directory)
        return _regex.IsMatch(relativePath);
    }

    private static string ConvertGitIgnorePatternToRegex(string pattern, string basePath, bool isRooted)
    {
        if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
            isRooted = true;
        }

        var sb = new System.Text.StringBuilder("^");
        AppendPathPrefix(sb, basePath, isRooted);
        ConvertGlobChars(sb, pattern);
        sb.Append("(\\/.*)?$");

        return sb.ToString();
    }

    private static void AppendPathPrefix(System.Text.StringBuilder sb, string basePath, bool isRooted)
    {
        if (isRooted && !string.IsNullOrEmpty(basePath))
        {
            sb.Append(Regex.Escape(basePath));
            sb.Append('/');
        }
        else if (!isRooted)
        {
            sb.Append("(.*\\/)?");
        }
    }

    private static void ConvertGlobChars(System.Text.StringBuilder sb, string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '*':
                    i += AppendStarPattern(sb, pattern, i);
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    sb.Append('[');
                    break;
                case ']':
                    sb.Append(']');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
    }

    /// <summary>
    /// Appends the regex for a * or ** glob pattern. Returns the number of extra characters consumed.
    /// </summary>
    private static int AppendStarPattern(System.Text.StringBuilder sb, string pattern, int index)
    {
        if (index + 1 < pattern.Length && pattern[index + 1] == '*')
        {
            if (index + 2 < pattern.Length && pattern[index + 2] == '/')
            {
                sb.Append("(.+\\/)?");
                return 2; // Skip **/
            }

            sb.Append(".*");
            return 1; // Skip second *
        }

        sb.Append("[^/]*");
        return 0;
    }
}
