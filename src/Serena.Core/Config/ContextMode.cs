// Context and Mode System - Ported from serena/config/context_mode.py
// Phase 5B: SerenaAgentMode, SerenaAgentContext, ToolInclusionDefinition

using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Serena.Core.Config;

/// <summary>
/// Defines which tools to include/exclude in Serena's operation.
/// Ported from serena_config.py ToolInclusionDefinition.
/// </summary>
public record ToolInclusionDefinition
{
    /// <summary>
    /// Tool names to exclude from use (incremental mode).
    /// </summary>
    [YamlMember(Alias = "excluded_tools")]
    public IReadOnlyList<string> ExcludedTools { get; init; } = [];

    /// <summary>
    /// Optional tool names to include (incremental mode).
    /// </summary>
    [YamlMember(Alias = "included_optional_tools")]
    public IReadOnlyList<string> IncludedOptionalTools { get; init; } = [];

    /// <summary>
    /// Fixed set of tools to use (fixed mode).
    /// </summary>
    [YamlMember(Alias = "fixed_tools")]
    public IReadOnlyList<string> FixedTools { get; init; } = [];

    public bool IsFixedToolSet
    {
        get
        {
            int numFixed = FixedTools.Count;
            int numIncremental = ExcludedTools.Count + IncludedOptionalTools.Count;
            if (numFixed > 0 && numIncremental > 0)
            {
                throw new InvalidOperationException(
                    "Cannot use both fixed_tools and excluded_tools/included_optional_tools at the same time.");
            }
            return numFixed > 0;
        }
    }
}

/// <summary>
/// Represents a mode of operation for the agent, loaded from YAML.
/// An agent can be in multiple modes simultaneously.
/// Ported from context_mode.py SerenaAgentMode.
/// </summary>
public sealed record SerenaAgentMode : ToolInclusionDefinition
{
    [YamlMember(Alias = "name")]
    public required string Name { get; init; }

    [YamlMember(Alias = "prompt")]
    public string Prompt { get; init; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Additional system prompt text injected when this mode is active.
    /// </summary>
    [YamlMember(Alias = "system_prompt_addition")]
    public string SystemPromptAddition { get; init; } = string.Empty;

    /// <summary>
    /// Tool names that are explicitly allowed when this mode is active.
    /// An empty list means no inclusion filtering is applied by this mode.
    /// </summary>
    [YamlMember(Alias = "tool_inclusions")]
    public IReadOnlyList<string> ToolInclusions { get; init; } = [];

    /// <summary>
    /// Tool names that are explicitly disallowed when this mode is active.
    /// </summary>
    [YamlMember(Alias = "tool_exclusions")]
    public IReadOnlyList<string> ToolExclusions { get; init; } = [];

    /// <summary>
    /// Whether this mode defines a prompt.
    /// </summary>
    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt) || !string.IsNullOrWhiteSpace(SystemPromptAddition);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load a mode from a YAML file.
    /// </summary>
    public static SerenaAgentMode FromYaml(string yamlPath)
    {
        string content = File.ReadAllText(yamlPath);
        var mode = Deserializer.Deserialize<SerenaAgentMode>(content);
        if (string.IsNullOrEmpty(mode.Name))
        {
            return mode with { Name = Path.GetFileNameWithoutExtension(yamlPath) };
        }
        return mode;
    }

    /// <summary>
    /// Load a mode from a YAML file using <see cref="YamlConfigLoader"/>.
    /// Returns null if the file does not exist or is empty.
    /// </summary>
    public static SerenaAgentMode? LoadFromYaml(string yamlPath)
    {
        var mode = YamlConfigLoader.TryLoad<SerenaAgentMode>(yamlPath);
        if (mode is null)
        {
            return null;
        }
        if (string.IsNullOrEmpty(mode.Name))
        {
            return mode with { Name = Path.GetFileNameWithoutExtension(yamlPath) };
        }
        return mode;
    }

    /// <summary>
    /// List all registered mode names from a modes directory.
    /// </summary>
    public static IReadOnlyList<string> ListModeNames(string modesDirectory)
    {
        if (!Directory.Exists(modesDirectory))
        {
            return [];
        }
        return Directory.GetFiles(modesDirectory, "*.yml")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(n => n != "mode.template")
            .Order()
            .ToList();
    }
}

/// <summary>
/// Maintains a set of currently active modes and provides combined tool filtering
/// and system prompt aggregation across all active modes.
/// </summary>
public sealed class ActiveModes
{
    private readonly Dictionary<string, SerenaAgentMode> _modes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a mode to the active set. Replaces any existing mode with the same name.
    /// </summary>
    public void AddMode(SerenaAgentMode mode)
    {
        _modes[mode.Name] = mode;
    }

    /// <summary>
    /// Removes a mode from the active set by name. No-op if not found.
    /// </summary>
    public void RemoveMode(string name)
    {
        _modes.Remove(name);
    }

    /// <summary>
    /// Returns all currently active modes.
    /// </summary>
    public IReadOnlyList<SerenaAgentMode> GetActiveModes() => _modes.Values.ToList();

    /// <summary>
    /// Determines whether a tool is allowed by all active modes.
    /// A tool is disallowed if any active mode lists it in its exclusions,
    /// or if any active mode defines inclusions and the tool is not in any of them.
    /// </summary>
    public bool IsToolAllowed(string toolName)
    {
        if (_modes.Count == 0)
        {
            return true;
        }

        // If any mode explicitly excludes the tool, it is disallowed
        foreach (var mode in _modes.Values)
        {
            if (mode.ToolExclusions.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // If any mode defines inclusions, the tool must appear in at least one
        var modesWithInclusions = _modes.Values
            .Where(m => m.ToolInclusions.Count > 0)
            .ToList();

        if (modesWithInclusions.Count > 0)
        {
            return modesWithInclusions.Any(m =>
                m.ToolInclusions.Contains(toolName, StringComparer.OrdinalIgnoreCase));
        }

        return true;
    }

    /// <summary>
    /// Concatenates the system prompt additions from all active modes, separated by newlines.
    /// </summary>
    public string GetCombinedSystemPromptAdditions()
    {
        var additions = _modes.Values
            .Select(m => m.SystemPromptAddition)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return additions.Count == 0 ? string.Empty : string.Join("\n\n", additions);
    }
}

/// <summary>
/// Represents a context where the agent is operating (an IDE, a chat, etc.).
/// An agent can only be in a single context at a time.
/// Ported from context_mode.py SerenaAgentContext.
/// </summary>
public sealed record SerenaAgentContext : ToolInclusionDefinition
{
    [YamlMember(Alias = "name")]
    public required string Name { get; init; }

    [YamlMember(Alias = "prompt")]
    public string Prompt { get; init; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Mode names to activate by default in this context.
    /// </summary>
    [YamlMember(Alias = "default_modes")]
    public IReadOnlyList<string> DefaultModes { get; init; } = [];

    /// <summary>
    /// Base mode names that are always active in this context.
    /// </summary>
    [YamlMember(Alias = "base_modes")]
    public IReadOnlyList<string> BaseModes { get; init; } = [];

    /// <summary>
    /// Startup behavior instruction (e.g., "onboarding", "activate_default_project").
    /// </summary>
    [YamlMember(Alias = "startup_behavior")]
    public string StartupBehavior { get; init; } = string.Empty;

    /// <summary>
    /// Whether to assume single-project mode.
    /// </summary>
    [YamlMember(Alias = "single_project")]
    public bool SingleProject { get; init; }

    /// <summary>
    /// Custom tool description overrides (tool name → description).
    /// </summary>
    [YamlMember(Alias = "tool_description_overrides")]
    public IReadOnlyDictionary<string, string> ToolDescriptionOverrides { get; init; } =
        new Dictionary<string, string>();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Load a context from a YAML file.
    /// </summary>
    public static SerenaAgentContext FromYaml(string yamlPath)
    {
        string content = File.ReadAllText(yamlPath);
        var context = Deserializer.Deserialize<SerenaAgentContext>(content);
        if (string.IsNullOrEmpty(context.Name))
        {
            return context with { Name = Path.GetFileNameWithoutExtension(yamlPath) };
        }
        return context;
    }

    /// <summary>
    /// Load a context from a YAML file using <see cref="YamlConfigLoader"/>.
    /// </summary>
    public static SerenaAgentContext? LoadFromYaml(string yamlPath)
    {
        var context = YamlConfigLoader.TryLoad<SerenaAgentContext>(yamlPath);
        if (context is null)
        {
            return null;
        }
        if (string.IsNullOrEmpty(context.Name))
        {
            return context with { Name = Path.GetFileNameWithoutExtension(yamlPath) };
        }
        return context;
    }

    /// <summary>
    /// List all registered context names from a contexts directory.
    /// </summary>
    public static IReadOnlyList<string> ListContextNames(string contextsDirectory)
    {
        if (!Directory.Exists(contextsDirectory))
        {
            return [];
        }
        return Directory.GetFiles(contextsDirectory, "*.yml")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Order()
            .ToList();
    }
}
