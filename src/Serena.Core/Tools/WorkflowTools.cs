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

        var solutions = DiscoverSolutionFiles(project.Root);
        if (solutions.Count < 2)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"⚠️ Multiple C# solutions detected ({solutions.Count} found). Without an active solution scope, semantic operations (find_referencing_symbols, rename_symbol) will load ALL projects into Roslyn — this can take 15-30+ minutes on large repos and may time out.");
        sb.AppendLine();
        sb.AppendLine("DO NOT enumerate or pick a solution blindly. Use this discovery workflow:");
        sb.AppendLine("  1. search_for_pattern(\"<term from the user's question>\")  — pure ripgrep, zero Roslyn cost");
        sb.AppendLine("  2. Note the directories of the hits");
        sb.AppendLine("  3. find_file(\"*.sln*\", relative_path=\"<directory of hits>\")  — finds the nearest enclosing solution");
        sb.AppendLine("  4. set_active_solution(solution_path=\"<that path>\")");
        sb.AppendLine("  5. Only NOW use find_symbol / find_referencing_symbols / rename_symbol.");
        sb.AppendLine();
        sb.AppendLine("If the user already named a specific component (e.g. \"Forms\"), skip steps 1-3 and call set_active_solution directly.");
        sb.AppendLine();
        sb.AppendLine("After set_active_solution the C# language server requires workspace warmup (10-30 min on large solutions). RECOMMENDED FLOW: (1) call warm_language_server immediately to begin background warmup; (2) while it loads, use search_for_pattern, find_file, get_symbols_overview (file-scoped), and find_symbol WITH a relative_path — these all work without Roslyn; (3) poll get_language_server_status every 60-120s; (4) once state == Ready, use solution-wide find_symbol (no relative_path), find_referencing_symbols, and rename_symbol. If a symbol call returns a warming status, do NOT retry immediately — poll get_language_server_status first.");
        sb.AppendLine();
        sb.AppendLine("To clear scope later: clear_active_solution. To stop a runaway language server: kill_language_server.");
        return sb.ToString().TrimEnd();
    }

    private static List<string> DiscoverSolutionFiles(string projectRoot)
    {
        return new[] { ".sln", ".slnx" }
            .SelectMany(ext => Directory.EnumerateFiles(projectRoot, $"*{ext}", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(projectRoot, p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        ## Working with the C# Language Server (Roslyn)

        For C# projects, symbol-graph tools are split into two categories with very different
        cost and readiness requirements. Knowing the difference avoids the most common failure
        mode (hung calls, empty results, timeouts).

        ### Tools that DO NOT require Roslyn — work immediately, even during warmup
        - `search_for_pattern` — text/regex search
        - `find_file` — filename glob
        - `read_file`
        - `list_dir`
        - `get_symbols_overview` — file-scoped lightweight parser
        - `find_symbol` **WHEN you pass `relative_path`** — file-scoped lightweight parser

        ### Tools that REQUIRE Roslyn (state == Ready)
        - `find_symbol` **WITHOUT `relative_path`** — solution-wide
        - `find_referencing_symbols` — needs the full symbol graph
        - `rename_symbol` — needs the full symbol graph
        - `replace_symbol_body`, `insert_before_symbol`, `insert_after_symbol`,
          `safe_delete_symbol` — symbol-targeting edits

        ### Recommended workflow for any C# task
        1. **Scope first.** If multiple solutions exist, call `set_active_solution` with the
           narrowest solution that contains the code you'll touch. Roslyn only loads projects
           in scope, so a tight scope means a fast warmup.
        2. **Kick off warmup immediately.** Call `warm_language_server` once. It returns in
           seconds and starts Roslyn loading in the background. Do NOT wait for it.
        3. **Work while it loads.** Use the no-Roslyn tools above for exploration:
           `search_for_pattern` to find candidates, `read_file` and `get_symbols_overview` to
           read them, `find_symbol` with `relative_path` for file-scoped symbol lookups.
        4. **Poll, don't retry.** Call `get_language_server_status` every 60–120 seconds to
           check progress. On large solutions warmup takes 10–30 minutes.
        5. **Use Roslyn-only tools once Ready.** When `state == Ready`, switch to solution-wide
           `find_symbol`, `find_referencing_symbols`, and `rename_symbol` for the questions
           that actually need the full symbol graph (find every caller, resolve overloads,
           rename across the solution).

        ### Critical rules
        - **Never retry a Roslyn-bound symbol call immediately after a `language_server_warming`
          response.** Poll `get_language_server_status` first. The warming response is
          structured JSON, not a transient error.
        - **A `find_referencing_symbols` empty result during warmup is meaningless.** It does
          not mean "no callers" — it means "Roslyn isn't ready." Wait for Ready, then re-ask.
        - **File-scoped `find_symbol` (with `relative_path`) returning empty IS meaningful** —
          that path uses the lightweight parser and does not depend on Roslyn.

        ### When to choose grep vs. Roslyn
        - **Grep wins (use the no-Roslyn tools):** forward pipeline traces, finding files,
          named-symbol lookups in known files, architectural exploration.
        - **Roslyn wins (wait for Ready):** "find every caller of method X" across overloads,
          finding all implementations of an interface, renaming a symbol, impact analysis.

        ## Best Practices

        1. **Start with overview**: Use `get_symbols_overview` before `find_symbol`
        2. **Use symbolic tools**: Prefer `find_symbol` + `replace_symbol_body` over text-based editing
        3. **Check references**: Use `find_referencing_symbols` before renaming or deleting
        4. **Use memories**: Write important project knowledge to memories for persistence
        5. **Validate changes**: After edits, use `read_file` to verify the result
        6. **Organize memories**: Use "/" in memory names for topic organization
        """;
}
