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
        var sb = new StringBuilder();

        string? scopeWarning = TryBuildScopeWarning();
        if (scopeWarning is not null)
        {
            sb.AppendLine(scopeWarning);
            sb.AppendLine();
        }

        sb.Append(InstructionsManual);

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

    /// <summary>
    /// Builds a warning when the active project contains multiple .sln/.slnx files
    /// and no C# scope is set. Returns null otherwise.
    /// </summary>
    private string? TryBuildScopeWarning()
    {
        var project = Context.ActiveProject;
        if (project is null)
        {
            return null;
        }

        if (!project.GetCSharpScope().IsEmpty)
        {
            return null;
        }

        if (!HasMultipleTopLevelSolutions(project.Root))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("⚠️ Multiple top-level C# solutions detected. Serena will not open an unbounded multi-solution Roslyn workspace; select the solution relevant to the task.");
        sb.AppendLine();
        sb.AppendLine("If the user named a solution, call set_active_solution with it directly. Otherwise:");
        sb.AppendLine("  1. Use cached find_symbol when the question names a declaration; use search_for_pattern for arbitrary text or a cache miss");
        sb.AppendLine("  2. Note the directories of relevant hits");
        sb.AppendLine("  3. find_file(\"*.sln*\", relative_path=\"<near the hits>\")");
        sb.AppendLine("  4. set_active_solution(solution_path=\"<that path>\") before Roslyn-bound operations");
        sb.AppendLine();
        sb.AppendLine("After scoping, call warm_language_server. While it loads, cached find_symbol/get_symbols_overview remain available and search_for_pattern handles non-symbol text. Poll get_language_server_status. Ready is complete; Partial permits read-only semantic queries but negative cross-project results are not guaranteed. Rename and safe delete require Ready.");
        sb.AppendLine();
        sb.AppendLine("To clear scope later: clear_active_solution. To stop a runaway language server: kill_language_server.");
        return sb.ToString().TrimEnd();
    }

    private static bool HasMultipleTopLevelSolutions(string projectRoot)
    {
        return new[] { ".sln", ".slnx" }
            .SelectMany(ext => Directory.EnumerateFiles(projectRoot, $"*{ext}", SearchOption.TopDirectoryOnly))
            .Take(2)
            .Count() > 1;
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

        ### Symbol Navigation
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

        ## Working with the C# Language Server (Roslyn)

        For C# projects, symbol-graph tools are split into two categories with very different
        cost and readiness requirements. Knowing the difference avoids the most common failure
        mode (hung calls, empty results, timeouts).

        ### Tools that never require Roslyn
        - `search_for_pattern` — text/regex search
        - `find_file` — filename glob
        - `read_file`
        - `list_dir`

        ### Cache-first symbol tools
        - `get_symbols_overview` — returns cached document symbols without Roslyn; a true cache
          miss starts the language server
        - `find_symbol` — searches a populated symbol cache without Roslyn, including when
          `relative_path` is supplied; a true cache miss may start the language server

        ### Tools that require Roslyn
        - `find_referencing_symbols` — needs the resolved symbol graph; it may run in `Partial`
          state, but its result explicitly says completeness is not guaranteed
        - `rename_symbol` and `safe_delete_symbol` — require `Ready`; Serena blocks them in
          `Partial` because an incomplete graph cannot prove all usages are covered

        ### Recommended workflow for any C# task
        1. **Use the symbol cache first.** `find_symbol` is for declarations and
           `get_symbols_overview` is for file structure. Use `search_for_pattern` for arbitrary
           text, configuration, UI strings, or when the cache has no relevant declaration.
        2. **Scope before Roslyn.** If multiple solutions exist, call `set_active_solution` with
           the narrowest relevant solution. Roslyn loads the solution's complete project graph,
           including dependencies outside the application's directory.
        3. **Kick off warmup when semantic relationships are needed.** Call
           `warm_language_server` once. Cached symbol tools remain available while it loads.
        4. **Poll, don't retry.** Call `get_language_server_status` every 60–120 seconds to
           check progress. On large solutions warmup takes 10–30 minutes.
        5. **Interpret readiness honestly.** `Ready` means initialization completed without
           captured warnings. `Partial` allows read-only reference queries but their completeness
           is not guaranteed. Mutating graph operations require `Ready`.

        ### Critical rules
        - **Never retry a Roslyn-bound symbol call immediately after a `language_server_warming`
          response.** Poll `get_language_server_status` first. The warming response is
          structured JSON, not a transient error.
        - **A `find_referencing_symbols` empty result during warmup is meaningless.** In `Partial`,
          inspect warnings and do not treat a negative result as proof that there are no callers.
        - **A cache-backed `find_symbol` result can be used while Roslyn is stopped or loading.**
          On a true cache miss, wait for the language server instead of treating an empty or
          warming result as a definitive answer.

        ### When to choose text search vs. symbol tools
        - **Cache-backed symbols win:** declaration lookup, qualified name paths, class/member
          structure, exact body ranges, and avoiding comment/string false positives.
        - **Text search wins:** arbitrary strings, configuration keys, generated markup, and
          discovering a term when no declaration name is known.
        - **Roslyn wins:** resolved callers across overloads, implementations, rename, and impact
          analysis across the selected solution's project graph.

        ## Best Practices

        1. **Start with overview**: Use `get_symbols_overview` before `find_symbol`
        2. **Use symbolic tools**: Prefer `find_symbol` + `replace_symbol_body` over text-based editing
        3. **Check references**: Use `find_referencing_symbols` before renaming or deleting
        4. **Use memories**: Write important project knowledge to memories for persistence
        5. **Validate changes**: After edits, use `read_file` to verify the result
        6. **Organize memories**: Use "/" in memory names for topic organization
        """;
}
