// Serena Configuration System - Ported from serena/config/serena_config.py
// Phase 5A: SerenaPaths, SerenaConfig, ProjectConfig, RegisteredProject

using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;

namespace Serena.Core.Config;

/// <summary>
/// Provides paths to various Serena-related directories and files.
/// Ported from serena_config.py SerenaPaths singleton.
/// </summary>
public sealed class SerenaPaths
{
    private static readonly Lazy<SerenaPaths> _lazy = new(() => new SerenaPaths());

    public string SerenaUserHomeDir { get; }
    public string UserPromptTemplatesDir { get; }
    public string UserContextsDir { get; }
    public string UserModesDir { get; }
    public string NewsReadItemsFile { get; }

    private SerenaPaths()
    {
        string? homeDir = Environment.GetEnvironmentVariable("SERENA_HOME");
        if (string.IsNullOrWhiteSpace(homeDir))
        {
            homeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".serena");
        }
        SerenaUserHomeDir = homeDir;
        UserPromptTemplatesDir = Path.Combine(SerenaUserHomeDir, "prompt_templates");
        UserContextsDir = Path.Combine(SerenaUserHomeDir, "contexts");
        UserModesDir = Path.Combine(SerenaUserHomeDir, "modes");
        NewsReadItemsFile = Path.Combine(SerenaUserHomeDir, "news_read.json");
    }

    public static SerenaPaths Instance => _lazy.Value;

    /// <summary>
    /// Ensures that all required directories exist.
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SerenaUserHomeDir);
        Directory.CreateDirectory(UserPromptTemplatesDir);
        Directory.CreateDirectory(UserContextsDir);
        Directory.CreateDirectory(UserModesDir);
    }
}

/// <summary>
/// A registered project that Serena knows about.
/// Ported from serena_config.py RegisteredProject.
/// </summary>
public sealed record RegisteredProject
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? Description { get; init; }
    public bool AutoDetected { get; init; }
}

/// <summary>
/// Configuration for a specific project.
/// Corresponds to the project.yml file in a project's .serena directory.
/// </summary>
public sealed record ProjectConfig
{
    /// <summary>
    /// The main programming language of the project.
    /// </summary>
    public string? MainLanguage { get; init; }

    /// <summary>
    /// Additional languages used in the project.
    /// </summary>
    public IReadOnlyList<string>? AdditionalLanguages { get; init; }

    /// <summary>
    /// Glob patterns for files to exclude from analysis.
    /// </summary>
    public IReadOnlyList<string>? ExcludePatterns { get; init; }

    /// <summary>
    /// Language-server-specific settings overrides.
    /// </summary>
    public IReadOnlyDictionary<string, object>? LsSpecificSettings { get; init; }

    /// <summary>
    /// The source file encoding to use.
    /// </summary>
    public string Encoding { get; init; } = "utf-8";

    /// <summary>
    /// Max characters for a single tool response.
    /// </summary>
    public int MaxAnswerChars { get; init; } = 40_000;

    /// <summary>
    /// Timeout in seconds for tool execution.
    /// </summary>
    public double ToolTimeout { get; init; } = 240;

    /// <summary>
    /// The initial mode to set when the project is activated.
    /// </summary>
    public string? InitialMode { get; init; }

    /// <summary>
    /// The initial context to set when the project is activated.
    /// </summary>
    public string? InitialContext { get; init; }

    /// <summary>
    /// Whether to include the onboarding tool.
    /// </summary>
    public bool IncludeOnboarding { get; init; } = true;

    /// <summary>
    /// Shell commands that are suggested to the agent for common tasks.
    /// </summary>

    /// <summary>
    /// Regex patterns for shell commands that should be blocked.
    /// When a command matches any of these patterns, execution is denied.
    /// Example patterns: "rm\s+-rf\s+/", "format\s+[a-z]:", "mkfs\."
    /// </summary>
    public IReadOnlyList<string>? ShellCommandDenyPatterns { get; init; }

    public IReadOnlyDictionary<string, string>? SuggestedCommands { get; init; }
}

/// <summary>
/// The global Serena configuration, which manages registered projects and global settings.
/// Ported from serena_config.py SerenaConfig.
/// </summary>
public sealed class SerenaConfig
{
    private readonly ILogger<SerenaConfig> _logger;
    private readonly Dictionary<string, RegisteredProject> _registeredProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerenaAgentContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerenaAgentMode> _modes = new(StringComparer.OrdinalIgnoreCase);

    public SerenaConfig(ILogger<SerenaConfig> logger)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<string, RegisteredProject> RegisteredProjects => _registeredProjects;

    /// <summary>
    /// Loaded agent contexts, keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, SerenaAgentContext> Contexts => _contexts;

    /// <summary>
    /// Loaded agent modes, keyed by name.
    /// </summary>
    public IReadOnlyDictionary<string, SerenaAgentMode> Modes => _modes;

    /// <summary>
    /// The default project to activate on startup, or null.
    /// </summary>
    public string? DefaultProject { get; private set; }

    /// <summary>
    /// Tool execution timeout in seconds.
    /// </summary>
    public double ToolTimeout { get; private set; } = 240;

    /// <summary>
    /// Default max characters for tool responses.
    /// </summary>
    public int DefaultMaxToolAnswerChars { get; private set; } = 150_000;

    /// <summary>
    /// The config file path (~/.serena/config.yml).
    /// </summary>
    public string ConfigFilePath => Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "config.yml");

    public void RegisterProject(RegisteredProject project)
    {
        _registeredProjects[project.Name] = project;
        _logger.LogInformation("Registered project: {Name} at {Path}", project.Name, project.Path);
    }

    public RegisteredProject? GetProject(string nameOrPath)
    {
        if (_registeredProjects.TryGetValue(nameOrPath, out var project))
        {
            return project;
        }

        // Try to find by path
        string normalizedPath = Path.GetFullPath(nameOrPath);
        return _registeredProjects.Values.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.Path), normalizedPath, StringComparison.OrdinalIgnoreCase));
    }

    public void UnregisterProject(string name)
    {
        if (_registeredProjects.Remove(name))
        {
            _logger.LogInformation("Unregistered project: {Name}", name);
        }
    }

    /// <summary>
    /// Removes a registered project by name, returning success or failure.
    /// </summary>
    public Result RemoveProject(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return Result.Failure("Project name must not be empty.");
        }

        if (!_registeredProjects.Remove(projectName))
        {
            return Result.Failure($"No project named '{projectName}' is registered.");
        }

        _logger.LogInformation("Removed project: {Name}", projectName);
        return Result.Success();
    }

    /// <summary>
    /// Scans the contexts directory for YAML files and loads each into a
    /// <see cref="SerenaAgentContext"/> stored by name.
    /// </summary>
    public void LoadContexts(string? contextsDirectory = null)
    {
        contextsDirectory ??= SerenaPaths.Instance.UserContextsDir;

        if (!Directory.Exists(contextsDirectory))
        {
            _logger.LogDebug("Contexts directory does not exist: {Dir}", contextsDirectory);
            return;
        }

        foreach (string filePath in Directory.GetFiles(contextsDirectory, "*.yml"))
        {
            TryLoadContext(filePath);
        }
    }

    private void TryLoadContext(string filePath)
    {
        try
        {
            var context = SerenaAgentContext.LoadFromYaml(filePath);
            if (context is null)
            {
                _logger.LogWarning("Context file was empty or unreadable: {Path}", filePath);
                return;
            }

            _contexts[context.Name] = context;
            _logger.LogInformation("Loaded context: {Name} from {Path}", context.Name, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load context from {Path}", filePath);
        }
    }

    /// <summary>
    /// Scans a modes directory for YAML mode definitions and loads them.
    /// </summary>
    public void LoadModes(string? modesDirectory = null)
    {
        modesDirectory ??= SerenaPaths.Instance.UserModesDir;

        if (!Directory.Exists(modesDirectory))
        {
            _logger.LogDebug("Modes directory does not exist: {Dir}", modesDirectory);
            return;
        }

        foreach (string filePath in Directory.GetFiles(modesDirectory, "*.yml"))
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            if (fileName == "mode.template")
            {
                continue;
            }

            try
            {
                var mode = SerenaAgentMode.LoadFromYaml(filePath);
                if (mode is null)
                {
                    _logger.LogWarning("Mode file was empty or unreadable: {Path}", filePath);
                    continue;
                }

                _modes[mode.Name] = mode;
                _logger.LogInformation("Loaded mode: {Name} from {Path}", mode.Name, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load mode from {Path}", filePath);
            }
        }
    }

    /// <summary>
    /// Loads configuration from a YAML file. Returns a default config if the file doesn't exist.
    /// Uses Result&lt;T&gt; to signal parse failures without exceptions.
    /// </summary>
    public static Result<SerenaConfig> LoadFromFile(string filePath, ILogger<SerenaConfig> logger)
    {
        if (!File.Exists(filePath))
        {
            logger.LogInformation("No config file at {Path}, using defaults", filePath);
            return Result.Success(new SerenaConfig(logger));
        }

        try
        {
            var model = YamlConfigLoader.TryLoad<ConfigFileModel>(filePath);
            var config = new SerenaConfig(logger);

            if (model is null)
            {
                // File exists but is empty or contains only comments — valid empty config
                logger.LogInformation("Config file at {Path} is empty, using defaults", filePath);
                return Result.Success(config);
            }

            if (model.Projects is not null)
            {
                foreach (var entry in model.Projects)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Path))
                    {
                        logger.LogWarning("Skipping project entry with missing name or path");
                        continue;
                    }

                    config.RegisterProject(new RegisteredProject
                    {
                        Name = entry.Name,
                        Path = entry.Path,
                        Description = entry.Description
                    });
                }
            }

            config.DefaultProject = model.DefaultProject;

            if (model.ToolTimeout.HasValue)
            {
                config.ToolTimeout = model.ToolTimeout.Value;
            }

            if (model.DefaultMaxToolAnswerChars.HasValue)
            {
                config.DefaultMaxToolAnswerChars = model.DefaultMaxToolAnswerChars.Value;
            }

            logger.LogInformation("Loaded config from {Path} with {Count} project(s)",
                filePath, config.RegisteredProjects.Count);

            return Result.Success(config);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load config from {Path}", filePath);
            return Result.Failure<SerenaConfig>($"Failed to load config: {ex.Message}");
        }
    }
}

/// <summary>
/// YAML-serializable configuration file model.
/// Property names map to snake_case YAML keys via YamlDotNet UnderscoredNamingConvention.
/// </summary>
internal sealed class ConfigFileModel
{
    public List<ProjectEntryModel>? Projects { get; set; }
    public string? DefaultProject { get; set; }
    public double? ToolTimeout { get; set; }
    public int? DefaultMaxToolAnswerChars { get; set; }
}

/// <summary>
/// A single project entry in the config YAML file.
/// </summary>
internal sealed class ProjectEntryModel
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Description { get; set; }
}
