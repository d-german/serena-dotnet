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
    /// Whether this mode defines a prompt.
    /// </summary>
    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

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
