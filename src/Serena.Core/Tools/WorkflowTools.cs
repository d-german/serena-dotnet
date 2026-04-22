// Workflow Tools - Onboarding and initial instructions
// Tools for: check_onboarding_performed, onboarding, initial_instructions

using System.Text;
using Serena.Core.Templates;

namespace Serena.Core.Tools;

[NoActiveProjectRequired]
public sealed class CheckOnboardingPerformedTool : ToolBase
{
    public CheckOnboardingPerformedTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Checks whether project onboarding was already performed. Call this after activating a project.";

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var mm = CreateMemoriesManager();
        var projectMemories = mm.ListProjectMemories();

        if (projectMemories.Count == 0)
        {
            return Task.FromResult(
                "Onboarding has NOT been performed for this project.\n" +
                "No project memories found. Call the 'onboarding' tool to start learning about the project.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("Onboarding has been performed. Project memories found:");
        foreach (string memory in projectMemories)
        {
            sb.AppendLine($"  - {memory}");
        }
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

public sealed class OnboardingTool : ToolBase
{
    public OnboardingTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Call this tool if onboarding was not performed yet. Returns instructions on how to explore the project.";

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Add project context if available
        var project = Context.ActiveProject;
        if (project is not null)
        {
            sb.AppendLine($"## Active Project: {project.Name}");
            sb.AppendLine($"- **Path**: {project.ProjectRoot}");
            sb.AppendLine();
        }

        sb.Append(OnboardingPrompt);
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private static readonly string OnboardingPrompt = """
        ## Project Onboarding Instructions

        You are onboarding onto a new project. Follow these steps to learn about it:

        ### 1. Explore Project Structure
        - Use `list_dir` with `relative_path: "."` and `recursive: false` to see the top-level structure
        - Identify the main source directories, configuration files, and build files

        ### 2. Identify Language & Framework
        - Look for common configuration files: package.json, *.csproj, pyproject.toml, Cargo.toml, etc.
        - Determine the primary programming language and framework

        ### 3. Understand Build & Test
        - Find and document build commands (e.g., `dotnet build`, `npm run build`, `make`)
        - Find and document test commands (e.g., `dotnet test`, `npm test`, `pytest`)
        - Try running them with `execute_shell_command` to verify they work

        ### 4. Explore Key Code
        - Use `find_file` and `get_symbols_overview` to understand the main modules
        - Use `find_symbol` to explore important classes and functions
        - Look at entry points (Main, index, app files)

        ### 5. Create Memories
        Write what you learned using `write_memory`. Create memories for:
        - **architecture**: High-level project structure and design patterns
        - **build**: How to build, test, and run the project
        - **conventions**: Coding conventions, naming patterns, and style guidelines
        - **dependencies**: Key dependencies and their purpose

        Use descriptive names with "/" for organization (e.g., "architecture/overview", "build/commands").

        ### 6. Verify Onboarding
        After creating memories, call `check_onboarding_performed` to confirm completion.
        """;
}

[NoActiveProjectRequired]
public sealed class InitialInstructionsTool : ToolBase
{
    public InitialInstructionsTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Provides the 'Serena Instructions Manual' with essential information on how to use the Serena toolbox.";

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var sb = new StringBuilder(InstructionsManual);

        // Append dynamic runtime context
        string dynamicContext = SystemPromptFactory.CreateSystemPrompt(Context.Agent);
        if (!string.IsNullOrWhiteSpace(dynamicContext))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## Current Runtime Context");
            sb.AppendLine();
            sb.Append(dynamicContext);
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private static readonly string InstructionsManual = """
        # Serena Instructions Manual

        ## Overview
        Serena is a code-aware assistant toolbox. It provides tools for navigating, understanding,
        and editing code using language server protocol (LSP) integration.

        ## Key Concepts

        ### Name Paths
        A name path is a hierarchical path to a symbol within a source file.
        Example: `MyClass/my_method` refers to `my_method` defined in `MyClass`.
        - Simple name: `method` — matches any symbol with that name
        - Relative path: `class/method` — matches any symbol with that suffix
        - Absolute path: `/class/method` — requires exact match within the file

        ### Symbol Kinds
        Symbols include classes, methods, functions, properties, fields, enums, interfaces, etc.
        Use `get_symbols_overview` to discover what a file contains before diving deeper.

        ## Tool Categories

        ### File Operations
        - `read_file` — Read file content (with optional line range)
        - `create_text_file` — Create or overwrite a file
        - `list_dir` — List directory contents
        - `find_file` — Find files by glob pattern
        - `search_for_pattern` — Regex search across codebase

        ### Symbol Navigation (requires LSP)
        - `find_symbol` — Find symbols by name path pattern
        - `get_symbols_overview` — High-level view of symbols in a file
        - `find_referencing_symbols` — Find all references to a symbol

        ### Code Editing (requires LSP)
        - `replace_symbol_body` — Replace a symbol's implementation
        - `insert_before_symbol` — Insert code before a symbol
        - `insert_after_symbol` — Insert code after a symbol
        - `replace_content` — Find-and-replace with literal or regex
        - `rename_symbol` — Rename a symbol across the codebase
        - `safe_delete_symbol` — Delete a symbol if no references exist

        ### Memory System
        - `read_memory` — Read a stored memory
        - `write_memory` — Save information for future sessions
        - `list_memories` — List available memories
        - `delete_memory` — Delete a memory
        - `rename_memory` — Rename or move a memory
        - `edit_memory` — In-place find-and-replace in a memory

        ### Project & Config
        - `activate_project` — Switch to a different project
        - `get_current_config` — View current agent configuration
        - `execute_shell_command` — Run shell commands

        ### Workflow
        - `check_onboarding_performed` — Check if project has been explored
        - `onboarding` — Get instructions for project exploration
        - `initial_instructions` — This manual

        ## Best Practices

        1. **Start with overview**: Use `get_symbols_overview` before `find_symbol`
        2. **Use symbolic tools**: Prefer `find_symbol` + `replace_symbol_body` over text-based editing
        3. **Check references**: Use `find_referencing_symbols` before renaming or deleting
        4. **Use memories**: Write important project knowledge to memories for persistence
        5. **Validate changes**: After edits, use `read_file` to verify the result
        6. **Organize memories**: Use "/" in memory names for topic organization
        """;
}
