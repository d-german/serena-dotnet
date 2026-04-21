// System Prompt Factory - Generates system prompts for LLM context.
// Uses simple string interpolation (no template engine dependency).

using System.Text;
using Serena.Core.Agent;

namespace Serena.Core.Templates;

/// <summary>
/// Static factory for building system prompts that describe the active Serena context
/// (project, tools, memories, mode) for LLM consumption.
/// </summary>
public static class SystemPromptFactory
{
    /// <summary>
    /// Creates a system prompt for the given agent, including project info,
    /// available tools, memory listings, and mode-specific instructions.
    /// Returns a minimal prompt when no project is active.
    /// </summary>
    public static string CreateSystemPrompt(SerenaAgent agent)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are Serena, an AI-powered coding assistant with deep code intelligence.");
        sb.AppendLine();

        AppendProjectSection(sb, agent);
        AppendToolsSection(sb, agent);
        AppendMemoriesSection(sb, agent);
        AppendModeSection(sb, agent);

        return sb.ToString().TrimEnd();
    }

    private static void AppendProjectSection(StringBuilder sb, SerenaAgent agent)
    {
        var project = agent.ActiveProject;
        if (project is null)
        {
            sb.AppendLine("## Project");
            sb.AppendLine("No project is currently active. Use the activate_project tool to activate one.");

            var registered = agent.RegisteredProjects;
            if (registered.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Registered projects:");
                foreach (string name in registered)
                {
                    sb.AppendLine($"  - {name}");
                }
            }

            sb.AppendLine();
            return;
        }

        sb.AppendLine("## Project");
        sb.AppendLine($"- **Name**: {project.Name}");
        sb.AppendLine($"- **Path**: {project.ProjectRoot}");
        sb.AppendLine();
    }

    private static void AppendToolsSection(StringBuilder sb, SerenaAgent agent)
    {
        IReadOnlyList<ToolDescription> tools;
        try
        {
            tools = agent.GetToolDescriptions();
        }
        catch (InvalidOperationException)
        {
            // Tool registry not yet set.
            return;
        }

        if (tools.Count == 0)
        {
            return;
        }

        sb.AppendLine("## Available Tools");
        foreach (var tool in tools)
        {
            sb.AppendLine($"- **{tool.Name}**: {tool.Description}");
        }

        sb.AppendLine();
    }

    private static void AppendMemoriesSection(StringBuilder sb, SerenaAgent agent)
    {
        var project = agent.ActiveProject;
        if (project is null)
        {
            return;
        }

        var memories = project.MemoriesManager.ListMemories();
        if (memories.Count == 0)
        {
            return;
        }

        sb.AppendLine("## Memories");
        sb.AppendLine("The following memories are available. Read them if relevant to your task:");
        foreach (string memory in memories)
        {
            sb.AppendLine($"  - {memory}");
        }

        sb.AppendLine();
    }

    private static void AppendModeSection(StringBuilder sb, SerenaAgent agent)
    {
        var mode = agent.ActiveMode;
        if (mode is null)
        {
            return;
        }

        sb.AppendLine("## Active Mode");
        sb.AppendLine($"- **Mode**: {mode.Name}");

        if (mode.HasPrompt)
        {
            sb.AppendLine();
            sb.AppendLine(mode.Prompt);
        }

        sb.AppendLine();
    }
}
