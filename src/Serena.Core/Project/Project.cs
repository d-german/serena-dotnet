// Project Class & File Operations - Ported from serena/project.py
// Phase 5C: Project, MemoriesManager

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp;

namespace Serena.Core.Project;

/// <summary>
/// Manages persistent memory notes (markdown files) scoped to a project or globally.
/// Ported from serena/project.py MemoriesManager.
/// </summary>
public sealed class MemoriesManager
{
    public const string GlobalTopic = "global";
    private readonly string _globalMemoryDir;
    private readonly string? _projectMemoryDir;
    private readonly Regex[] _readOnlyPatterns;
    private readonly Regex[] _ignoredPatterns;

    public MemoriesManager(
        string? serenaDataFolder,
        IEnumerable<string>? readOnlyMemoryPatterns = null,
        IEnumerable<string>? ignoredMemoryPatterns = null)
    {
        _globalMemoryDir = Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "memories");
        Directory.CreateDirectory(_globalMemoryDir);

        if (serenaDataFolder is not null)
        {
            _projectMemoryDir = Path.Combine(serenaDataFolder, "memories");
            Directory.CreateDirectory(_projectMemoryDir);
        }

        _readOnlyPatterns = (readOnlyMemoryPatterns ?? []).Select(p => new Regex(p)).ToArray();
        _ignoredPatterns = (ignoredMemoryPatterns ?? []).Select(p => new Regex(p)).ToArray();
    }

    private static bool IsGlobal(string name) =>
        name == GlobalTopic || name.StartsWith(GlobalTopic + "/", StringComparison.Ordinal);

    public string GetMemoryFilePath(string name)
    {
        name = name.Replace(".md", "").Replace('\\', '/');
        string[] parts = name.Split('/');
        if (parts.Contains(".."))
        {
            throw new ArgumentException($"Memory name cannot contain '..' segments. Got: {name}");
        }

        if (IsGlobal(name))
        {
            if (name == GlobalTopic)
            {
                throw new ArgumentException(
                    $"Bare \"{GlobalTopic}\" is not a valid memory name. Use \"{GlobalTopic}/<name>\".");
            }
            string subName = name[(GlobalTopic.Length + 1)..];
            string[] subParts = subName.Split('/');
            string filename = $"{subParts[^1]}.md";
            if (subParts.Length > 1)
            {
                string subdir = Path.Combine(_globalMemoryDir, string.Join(Path.DirectorySeparatorChar.ToString(), subParts[..^1]));
                Directory.CreateDirectory(subdir);
                return Path.Combine(subdir, filename);
            }
            return Path.Combine(_globalMemoryDir, filename);
        }

        if (_projectMemoryDir is null)
        {
            throw new InvalidOperationException("No project directory configured for project-local memories.");
        }

        string fname = $"{parts[^1]}.md";
        if (parts.Length > 1)
        {
            string subdir = Path.Combine(_projectMemoryDir, string.Join(Path.DirectorySeparatorChar.ToString(), parts[..^1]));
            Directory.CreateDirectory(subdir);
            return Path.Combine(subdir, fname);
        }
        return Path.Combine(_projectMemoryDir, fname);
    }

    public string LoadMemory(string name)
    {
        CheckNotIgnored(name);
        string path = GetMemoryFilePath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Memory '{name}' not found at {path}");
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }

    public void SaveMemory(string name, string content)
    {
        CheckNotIgnored(name);
        CheckWriteAccess(name);
        string path = GetMemoryFilePath(name);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    public void DeleteMemory(string name)
    {
        CheckNotIgnored(name);
        CheckWriteAccess(name);
        string path = GetMemoryFilePath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<string> ListMemories(string? topic = null)
    {
        var memories = new List<string>();
        if (_projectMemoryDir is not null)
        {
            memories.AddRange(ListMemoriesInDir(_projectMemoryDir, ""));
        }
        memories.AddRange(ListMemoriesInDir(_globalMemoryDir, GlobalTopic));

        if (topic is not null)
        {
            memories = memories.Where(m => m.StartsWith(topic, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return memories.Where(m => !IsIgnored(m)).Order().ToList();
    }

    private static IEnumerable<string> ListMemoriesInDir(string dir, string prefix)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        foreach (string file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            string name = relative[..^3]; // Remove .md extension
            yield return string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
        }
    }

    /// <summary>
    /// Renames/moves a memory file. Supports cross-scope moves (project ↔ global).
    /// </summary>
    public void MoveMemory(string oldName, string newName)
    {
        CheckNotIgnored(oldName);
        CheckNotIgnored(newName);
        CheckWriteAccess(newName);

        string oldPath = GetMemoryFilePath(oldName);
        if (!File.Exists(oldPath))
        {
            throw new FileNotFoundException($"Memory '{oldName}' not found at {oldPath}");
        }

        string newPath = GetMemoryFilePath(newName);
        if (File.Exists(newPath))
        {
            throw new InvalidOperationException(
                $"Cannot move memory '{oldName}' to '{newName}': destination already exists.");
        }

        string? newDir = Path.GetDirectoryName(newPath);
        if (newDir is not null)
        {
            Directory.CreateDirectory(newDir);
        }

        File.Move(oldPath, newPath);
    }

    /// <summary>
    /// In-place find-and-replace within a memory file.
    /// </summary>
    public string EditMemory(string name, string needle, string replacement, string mode, bool allowMultiple)
    {
        CheckNotIgnored(name);
        CheckWriteAccess(name);

        string path = GetMemoryFilePath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Memory '{name}' not found at {path}");
        }

        string content = File.ReadAllText(path, Encoding.UTF8);
        string updated = ApplyReplacement(content, needle, replacement, mode, allowMultiple);

        File.WriteAllText(path, updated, Encoding.UTF8);
        return updated;
    }

    /// <summary>
    /// Returns only project-scope memories (excluding global).
    /// </summary>
    public IReadOnlyList<string> ListProjectMemories()
    {
        if (_projectMemoryDir is null)
        {
            return [];
        }

        return ListMemoriesInDir(_projectMemoryDir, "")
            .Where(m => !IsIgnored(m))
            .Order()
            .ToList();
    }

    private static string ApplyReplacement(
        string content, string needle, string replacement, string mode, bool allowMultiple)
    {
        if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
        {
            var regex = TextReplacementHelper.CreateSearchRegex(needle);
            int matchCount = regex.Matches(content).Count;
            if (matchCount == 0)
            {
                throw new InvalidOperationException($"Pattern '{needle}' not found in memory.");
            }
            if (matchCount > 1 && !allowMultiple)
            {
                throw new InvalidOperationException(
                    $"Pattern '{needle}' matched {matchCount} times. Set allowMultiple=true to replace all.");
            }
            return allowMultiple ? regex.Replace(content, replacement) : regex.Replace(content, replacement, 1);
        }

        // Literal mode
        string normalizedNeedle = TextReplacementHelper.NormalizeLineEndings(needle, content);
        string normalizedRepl = TextReplacementHelper.NormalizeLineEndings(replacement, content);

        int occurrences = TextReplacementHelper.CountOccurrences(content, normalizedNeedle);
        if (occurrences == 0)
        {
            throw new InvalidOperationException($"Literal string '{needle}' not found in memory.");
        }
        if (occurrences > 1 && !allowMultiple)
        {
            throw new InvalidOperationException(
                $"Literal string '{needle}' found {occurrences} times. Set allowMultiple=true to replace all.");
        }

        return allowMultiple
            ? content.Replace(normalizedNeedle, normalizedRepl, StringComparison.Ordinal)
            : TextReplacementHelper.ReplaceFirst(content, normalizedNeedle, normalizedRepl);
    }

    private bool IsIgnored(string name) =>
        _ignoredPatterns.Any(p => p.IsMatch(name));

    private void CheckNotIgnored(string name)
    {
        if (IsIgnored(name))
        {
            throw new InvalidOperationException(
                $"Memory '{name}' matches an ignored pattern and cannot be accessed.");
        }
    }

    private void CheckWriteAccess(string name)
    {
        if (_readOnlyPatterns.Any(p => p.IsMatch(name)))
        {
            throw new UnauthorizedAccessException($"Memory '{name}' is read-only.");
        }
    }
}

/// <summary>
/// Represents a Serena project with its configuration, file operations, and language server management.
/// Ported from serena/project.py Project class.
/// </summary>
public sealed class SerenaProject
{
    private readonly ILogger<SerenaProject> _logger;
    private readonly string _projectRoot;
    private ProjectConfig _config;
    private MemoriesManager? _memoriesManager;

    public string ProjectRoot => _projectRoot;
    public ProjectConfig Config => _config;

    /// <summary>
    /// The registration config from config.yml, if this project was registered.
    /// </summary>
    public RegisteredProject? Registration { get; }

    /// <summary>
    /// Root directory of the project.
    /// </summary>
    public string Root => _projectRoot;

    /// <summary>
    /// Display name — prefers registered name, falls back to directory name.
    /// </summary>
    public string Name => Registration?.Name ?? Path.GetFileName(_projectRoot);

    public string ProjectName => Name;

    /// <summary>
    /// Path to the .serena data folder within the project.
    /// </summary>
    public string SerenaDataFolder => Path.Combine(_projectRoot, ".serena");

    /// <summary>
    /// Path to the project.yml configuration file.
    /// </summary>
    public string ProjectYmlPath => Path.Combine(SerenaDataFolder, "project.yml");

    public MemoriesManager MemoriesManager =>
        _memoriesManager ??= new MemoriesManager(SerenaDataFolder);

    public SerenaProject(string projectRoot, ILogger<SerenaProject> logger, RegisteredProject? registration = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _logger = logger;
        Registration = registration;
        _config = LoadConfig();
    }

    private ProjectConfig LoadConfig()
    {
        if (File.Exists(ProjectYmlPath))
        {
            try
            {
                return YamlConfigLoader.Load<ProjectConfig>(ProjectYmlPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load project config from {Path}, using defaults", ProjectYmlPath);
            }
        }
        return new ProjectConfig();
    }

    public void SaveConfig()
    {
        Directory.CreateDirectory(SerenaDataFolder);
        YamlConfigLoader.Save(ProjectYmlPath, _config);
    }

    /// <summary>
    /// Reads a file relative to the project root.
    /// </summary>
    public string ReadFile(string relativePath, int startLine = 0, int? endLine = null)
    {
        string fullPath = Path.Combine(_projectRoot, relativePath);
        ValidatePath(fullPath);
        string[] lines = File.ReadAllLines(fullPath, Encoding.GetEncoding(_config.Encoding));

        if (startLine == 0 && endLine is null)
        {
            return string.Join('\n', lines);
        }

        int end = endLine ?? lines.Length;
        return string.Join('\n', lines.Skip(startLine).Take(end - startLine));
    }

    /// <summary>
    /// Checks if a relative path exists within the project.
    /// </summary>
    public bool RelativePathExists(string relativePath)
    {
        string fullPath = Path.Combine(_projectRoot, relativePath);
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    /// <summary>
    /// Validates that a path is within the project root.
    /// </summary>
    public void ValidatePath(string fullPath)
    {
        string normalizedFull = Path.GetFullPath(fullPath);
        if (!normalizedFull.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Path '{fullPath}' is outside the project root '{_projectRoot}'.");
        }
    }

    /// <summary>
    /// Validates that a relative path is safe and resolves within the project.
    /// </summary>
    public string ValidateRelativePath(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        ValidatePath(fullPath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"Path does not exist: {relativePath}");
        }
        return fullPath;
    }

    /// <summary>
    /// Gathers source files matching the given glob patterns, excluding ignored paths.
    /// </summary>
    public IReadOnlyList<string> GatherSourceFiles(IEnumerable<string>? includePatterns = null)
    {
        var matcher = new Matcher();
        foreach (string pattern in includePatterns ?? ["**/*"])
        {
            matcher.AddInclude(pattern);
        }
        foreach (string excludePattern in _config.ExcludePatterns ?? [])
        {
            matcher.AddExclude(excludePattern);
        }

        var result = matcher.Execute(
            new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                new DirectoryInfo(_projectRoot)));

        return result.Files.Select(f => f.Path).ToList();
    }

    /// <summary>
    /// The gitignore-based filter for this project (lazy-loaded).
    /// </summary>
    private GitIgnoreFilter? _ignoreFilter;
    public GitIgnoreFilter IgnoreFilter =>
        _ignoreFilter ??= GitIgnoreFilter.LoadFromProjectRoot(_projectRoot);

    /// <summary>
    /// Checks if a file path should be ignored based on .gitignore rules.
    /// </summary>
    public bool IsIgnoredPath(string relativePath) =>
        IgnoreFilter.IsIgnored(relativePath);
}
