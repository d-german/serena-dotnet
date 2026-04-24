// Tools for: read_memory, write_memory, list_memories, execute_shell_command, etc.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serena.Core.Project;
using Serena.Lsp;
using Serena.Lsp.Project;

namespace Serena.Core.Tools;

[NoActiveProjectRequired]
public sealed class ReadMemoryTool : ToolBase
{
    public ReadMemoryTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Reads the contents of a memory. Should only be used if the information is likely relevant.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("memory_name", "The name of the memory to read.", typeof(string), Required: true),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string memoryName = GetRequired<string>(arguments, "memory_name");

        var mm = CreateMemoriesManager();
        string content = mm.LoadMemory(memoryName);
        return Task.FromResult(content);
    }
}

[CanEdit]
public sealed class WriteMemoryTool : ToolBase
{
    public WriteMemoryTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Write information about this project that can be useful for future tasks to a memory.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("memory_name", "The name of the memory to write.", typeof(string), Required: true),
        new("content", "The markdown content to write.", typeof(string), Required: true),
        new("max_chars", "Maximum characters to write. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string memoryName = GetRequired<string>(arguments, "memory_name");
        string content = GetRequired<string>(arguments, "content");
        int maxChars = GetOptional(arguments, "max_chars", -1);

        if (maxChars > 0 && content.Length > maxChars)
        {
            throw new InvalidOperationException(
                $"Content exceeds max_chars ({content.Length} > {maxChars}). " +
                "Please reduce the content length before writing.");
        }

        var mm = CreateMemoriesManager();
        mm.SaveMemory(memoryName, content);
        return Task.FromResult($"Memory '{memoryName}' saved ({content.Length} chars).");
    }
}

[NoActiveProjectRequired]
public sealed class ListMemoriesTool : ToolBase
{
    public ListMemoriesTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Lists available memories, optionally filtered by topic.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("topic", "Optional topic prefix to filter memories.", typeof(string), Required: false, DefaultValue: ""),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string topic = GetOptional(arguments, "topic", "");

        var mm = CreateMemoriesManager();
        var memories = mm.ListMemories(string.IsNullOrEmpty(topic) ? null : topic);
        return Task.FromResult(ToJson(memories));
    }
}

[CanEdit]
public sealed class DeleteMemoryTool : ToolBase
{
    public DeleteMemoryTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Delete a memory, only call if instructed explicitly or permission was granted.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("memory_name", "The name of the memory to delete.", typeof(string), Required: true),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string memoryName = GetRequired<string>(arguments, "memory_name");

        var mm = CreateMemoriesManager();
        mm.DeleteMemory(memoryName);
        return Task.FromResult($"Memory '{memoryName}' deleted.");
    }
}

[CanEdit]
public sealed class ExecuteShellCommandTool : ToolBase
{
    private const int DefaultTimeoutSeconds = 240;

    public ExecuteShellCommandTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Execute a shell command and return its output. Never execute unsafe commands.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("command", "The shell command to execute.", typeof(string), Required: true),
        new("cwd", "Working directory. If null, uses the project root.", typeof(string), Required: false),
        new("capture_stderr", "Whether to capture stderr output.", typeof(bool), Required: false, DefaultValue: true),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
        new("timeout_seconds", "Timeout in seconds for the command. Default is 240.", typeof(int), Required: false, DefaultValue: DefaultTimeoutSeconds),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string command = GetRequired<string>(arguments, "command");
        string? cwd = GetOptional<string?>(arguments, "cwd", null);
        bool captureStderr = GetOptional(arguments, "capture_stderr", true);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);
        int timeoutSeconds = GetOptional(arguments, "timeout_seconds", DefaultTimeoutSeconds);
        string projectRoot = RequireProjectRoot();
        string workingDir = cwd is not null ? ResolvePath(cwd) : projectRoot;

        // Audit log the command at Information level
        Logger.LogInformation("Shell command requested: {Command} (cwd: {Cwd})", command, workingDir);

        EnforceDenyPatterns(command);

        var psi = ConfigureProcess(command, workingDir);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process.");

        return await RunProcessAsync(process, captureStderr, workingDir, maxChars, timeoutSeconds, ct);
    }

    private void EnforceDenyPatterns(string command)
    {
        var denyPatterns = Context.ActiveProject?.Config?.ShellCommandDenyPatterns;
        if (denyPatterns is not { Count: > 0 })
        {
            return;
        }

        foreach (string pattern in denyPatterns)
        {
            if (Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
            {
                Logger.LogWarning("Shell command blocked by deny pattern '{Pattern}': {Command}", pattern, command);
                throw new InvalidOperationException(
                    $"Command blocked by security policy. It matches deny pattern: {pattern}");
            }
        }
    }

    private static ProcessStartInfo ConfigureProcess(string command, string workingDir)
    {
        var (fileName, arguments) = SelectShell(command, OperatingSystem.IsWindows(), IsExecutableOnPath);
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    // v1.0.28: pick the best available shell. On Windows prefer pwsh.exe (modern
    // PowerShell), then powershell.exe, then cmd.exe. Non-Windows always uses
    // /bin/sh. Caller-injectable PATH probe enables unit testing without
    // mutating the host PATH.
    internal static (string FileName, string Arguments) SelectShell(
        string command, bool isWindows, Func<string, bool> isOnPath)
    {
        if (!isWindows)
        {
            return ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
        }

        // PowerShell-quoted command: escape embedded double quotes by doubling.
        string psArgs = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\"\"")}\"";
        if (isOnPath("pwsh.exe"))
        {
            return ("pwsh.exe", psArgs);
        }
        if (isOnPath("powershell.exe"))
        {
            return ("powershell.exe", psArgs);
        }
        return ("cmd.exe", $"/c {command}");
    }

    // Process-lifetime cache so we only walk PATH once per executable name.
    private static readonly ConcurrentDictionary<string, bool> _onPathCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool IsExecutableOnPath(string exeName) =>
        _onPathCache.GetOrAdd(exeName, static name =>
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return false;
            }
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                try
                {
                    if (File.Exists(Path.Combine(dir, name)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Bad PATH entry — skip.
                }
            }
            return false;
        });

    private async Task<string> RunProcessAsync(
        Process process, bool captureStderr, string workingDir, int maxChars, int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = captureStderr
                ? process.StandardError.ReadToEndAsync(timeoutCts.Token)
                : Task.FromResult("");

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            var result = new ShellResult(stdout, captureStderr ? stderr : null, process.ExitCode, workingDir);
            return LimitLength(ToJson(result), maxChars);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Best-effort process kill failed");
            }
            return ToJson(new { error = $"Command timed out after {timeoutSeconds} seconds." });
        }
    }

    private sealed record ShellResult(string Stdout, string? Stderr, int ReturnCode, string Cwd);
}

[CanEdit]
public sealed class RenameMemoryTool : ToolBase
{
    public RenameMemoryTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Rename or move a memory. Use '/' in the name to organize into topics.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("old_name", "The current name of the memory.", typeof(string), Required: true),
        new("new_name", "The new name for the memory.", typeof(string), Required: true),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string oldName = GetRequired<string>(arguments, "old_name");
        string newName = GetRequired<string>(arguments, "new_name");

        var mm = CreateMemoriesManager();
        mm.MoveMemory(oldName, newName);
        return Task.FromResult($"Memory renamed from '{oldName}' to '{newName}'.");
    }
}

[CanEdit]
public sealed class EditMemoryTool : ToolBase
{
    public EditMemoryTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Replaces content matching a pattern in a memory.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("memory_name", "The name of the memory.", typeof(string), Required: true),
        new("needle", "The string or regex pattern to search for.", typeof(string), Required: true),
        new("repl", "The replacement string.", typeof(string), Required: true),
        new("mode", "Either 'literal' or 'regex'.", typeof(string), Required: true),
        new("allow_multiple_occurrences", "Whether to allow replacing multiple matches.", typeof(bool), Required: false, DefaultValue: false),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string memoryName = GetRequired<string>(arguments, "memory_name");
        string needle = GetRequired<string>(arguments, "needle");
        string repl = GetRequired<string>(arguments, "repl");
        string mode = GetRequired<string>(arguments, "mode");
        bool allowMultiple = GetOptional(arguments, "allow_multiple_occurrences", false);

        var mm = CreateMemoriesManager();
        mm.EditMemory(memoryName, needle, repl, mode, allowMultiple);
        return Task.FromResult($"Memory '{memoryName}' updated successfully.");
    }
}

[NoActiveProjectRequired]
public sealed class ActivateProjectTool : ToolBase
{
    public ActivateProjectTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Activates the project with the given name or path.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("project", "The name of a registered project or path to a project directory.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string project = GetRequired<string>(arguments, "project");

        var result = await Context.Agent.TryActivateProjectAsync(project, ct);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }

        var active = Context.ActiveProject;
        if (active is null)
        {
            return $"Activated project at {project}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Activated project '{active.Name}' at {active.Root}");

        // Onboarding & memories
        var mm = CreateMemoriesManager();
        var projectMemories = mm.ListProjectMemories();
        var allMemories = mm.ListMemories();

        if (projectMemories.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Onboarding: NOT performed. No project memories found.");
            sb.AppendLine("Consider calling the 'onboarding' tool to learn about this project.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine($"Onboarding: performed ({projectMemories.Count} project memories).");
        }

        if (allMemories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available memories:");
            foreach (string memory in allMemories)
            {
                sb.AppendLine($"  - {memory}");
            }
        }

        // Active language servers
        var activeServers = Context.Agent.ActiveLanguageServers;
        if (activeServers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Active language servers:");
            foreach (string lang in activeServers)
            {
                sb.AppendLine($"  - {lang}");
            }
        }

        // Mode-specific info
        var mode = Context.Agent.ActiveMode;
        if (mode is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Active mode: {mode.Name}");
            if (!string.IsNullOrWhiteSpace(mode.Description))
            {
                sb.AppendLine($"  {mode.Description}");
            }
        }

        // Reminder about manual
        sb.AppendLine();
        sb.AppendLine("Reminder: If you have not yet read the Serena Instructions Manual, " +
                       "call the 'initial_instructions' tool for essential guidance.");

        return sb.ToString().TrimEnd();
    }
}

[NoActiveProjectRequired]
public sealed class GetCurrentConfigTool : ToolBase
{
    public GetCurrentConfigTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Print the current configuration of the agent, including the active and available projects.";

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var agent = Context.Agent;
        var active = Context.ActiveProject;
        var sb = new StringBuilder();

        sb.AppendLine("=== Serena Agent Configuration ===");
        sb.AppendLine();

        if (active is not null)
        {
            sb.AppendLine($"Active Project: {active.Name}");
            sb.AppendLine($"  Path: {active.Root}");
        }
        else
        {
            sb.AppendLine("Active Project: (none)");
        }
        sb.AppendLine();

        var registeredProjects = agent.RegisteredProjects;
        sb.AppendLine($"Registered Projects ({registeredProjects.Count}):");
        foreach (string projectName in registeredProjects)
        {
            sb.AppendLine($"  - {projectName}");
        }
        sb.AppendLine();

        var mode = agent.ActiveMode;
        sb.AppendLine($"Active Mode: {mode?.Name ?? "(none)"}");
        sb.AppendLine();

        try
        {
            var tools = agent.Tools.All;
            sb.AppendLine($"Available Tools ({tools.Count}):");
            foreach (var tool in tools.OrderBy(t => t.Name))
            {
                sb.AppendLine($"  - {tool.Name}");
            }
        }
        catch (InvalidOperationException)
        {
            sb.AppendLine("Available Tools: (not initialized)");
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

[NoActiveProjectRequired]
public sealed class RemoveProjectTool : ToolBase
{
    public RemoveProjectTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Removes a registered project by name.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("project", "The name of the project to remove.", typeof(string), Required: true),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string project = GetRequired<string>(arguments, "project");

        var result = Context.Agent.Config.RemoveProject(project);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error);
        }

        return Task.FromResult($"Removed project '{project}'.");
    }
}

[OptionalTool]
[NoActiveProjectRequired]
public sealed class RestartLanguageServerTool : ToolBase
{
    public RestartLanguageServerTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Restart the language server. Use when LSP tools return errors.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() => [];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        await Context.Agent.ResetLanguageServerManagerAsync();
        return "Language server has been restarted successfully.";
    }
}

[OptionalTool]
[NoActiveProjectRequired]
public sealed class ListQueryableProjectsTool : ToolBase
{
    public ListQueryableProjectsTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Returns names of all registered projects EXCEPT the active one.";

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var activeProject = Context.ActiveProject;
        var projects = Context.Agent.RegisteredProjects
            .Where(name => activeProject is null
                || !string.Equals(name, activeProject.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult(projects.Count > 0
            ? string.Join("\n", projects)
            : "No queryable projects registered.");
    }
}

[OptionalTool]
[NoActiveProjectRequired]
public sealed class QueryProjectTool : ToolBase
{
    public QueryProjectTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Temporarily activates another project, executes a read-only tool in it, and returns the result.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("project", "Name or path of the project to query.", typeof(string), Required: true),
        new("tool_name", "Name of the tool to execute (must be read-only).", typeof(string), Required: true),
        new("tool_params", "JSON object with tool parameters.", typeof(string), Required: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string project = GetRequired<string>(arguments, "project");
        string toolName = GetRequired<string>(arguments, "tool_name");
        string toolParamsJson = GetRequired<string>(arguments, "tool_params");

        // Look up the tool in the current registry before switching projects.
        ITool? tool = Context.Agent.Tools.Get(toolName)
            ?? throw new InvalidOperationException($"Tool '{toolName}' not found.");

        // Only allow read-only tools (no [CanEdit] or [SymbolicEdit] attributes).
        if (tool is ToolBase tb && tb.CanEdit)
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' is not read-only and cannot be used with query_project.");
        }

        // Parse the tool parameters JSON.
        var parsedParams = ParseToolParams(toolParamsJson);

        // Temporarily activate the target project, run the tool, then restore.
        await using var ctx = await Context.Agent.ActivateProjectTemporarilyAsync(project, ct);
        return await tool.ExecuteAsync(parsedParams, ct);
    }

    private static Dictionary<string, object?> ParseToolParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }
        return result;
    }
}

[CanEdit]
public sealed class SetActiveSolutionTool : ToolBase
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];

    public SetActiveSolutionTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Scope the C# Roslyn workspace to one or more .sln/.slnx files. " +
        "You MUST supply 'solution_path' (single) or 'solution_paths' (multiple). The choice is " +
        "persisted into .serena/project.yml under csharp.scope.solutions and the C# language " +
        "server is restarted so Roslyn loads only the listed projects. Use this on large " +
        "mono-repos to dramatically reduce C# language server cold-load time.\n\n" +
        "DISCOVERY WORKFLOW (MANDATORY when the user hasn't named a specific solution): " +
        "DO NOT enumerate solutions yourself — large repos can have hundreds. Instead: " +
        "(1) use 'search_for_pattern' (pure ripgrep, zero Roslyn cost) to grep for terms from the " +
        "user's question; " +
        "(2) inspect the file paths of the hits; " +
        "(3) use 'find_file' with pattern '*.sln*' relative_path=<directory of hits> to find the " +
        "nearest enclosing solution; " +
        "(4) call this tool with that solution_path. Only AFTER scoping should you use " +
        "find_symbol / find_referencing_symbols / rename_symbol — those force Roslyn to load " +
        "every project in scope.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("solution_path", "Path (relative to project root, or absolute) to a single .sln/.slnx file. Required unless 'solution_paths' is supplied.",
            typeof(string), Required: false, DefaultValue: ""),
        new("solution_paths", "List of paths to .sln/.slnx files. Takes precedence over solution_path.",
            typeof(List<string>), Required: false),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var project = Context.ActiveProject
            ?? throw new InvalidOperationException("No active project. Call activate_project first.");

        var requestedPaths = CollectRequestedPaths(arguments);
        if (requestedPaths.Count == 0)
        {
            throw new ArgumentException(
                "set_active_solution requires 'solution_path' or 'solution_paths'. " +
                "To discover which solution is relevant, first use 'search_for_pattern' on terms from " +
                "the user's question, then 'find_file' with pattern '*.sln*' near the hits to locate " +
                "the enclosing solution. Do NOT enumerate solutions blindly — large repos can have " +
                "hundreds.");
        }

        var resolved = ResolveAndValidate(project.Root, requestedPaths);
        var totalProjects = await CountScopedProjectsAsync(resolved, ct);

        project.UpdateAndPersistCSharpScope(resolved);
        await Context.Agent.RestartLanguageServerAsync(Language.CSharp, ct);

        return FormatSuccess(project.Root, resolved, totalProjects);
    }

    private static List<string> CollectRequestedPaths(IReadOnlyDictionary<string, object?> arguments)
    {
        var multi = ExtractStringList(arguments, "solution_paths");
        if (multi.Count > 0)
        {
            return multi;
        }

        string single = GetOptional(arguments, "solution_path", string.Empty);
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }

    private static List<string> ExtractStringList(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return [];
        }
        if (value is IEnumerable<object?> objects)
        {
            return objects
                .Where(o => o is not null)
                .Select(o => o!.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        if (value is string s)
        {
            return string.IsNullOrWhiteSpace(s) ? [] : [s];
        }
        return [];
    }

    private static List<string> ResolveAndValidate(string projectRoot, IEnumerable<string> requestedPaths)
    {
        var resolved = new List<string>();
        foreach (var raw in requestedPaths)
        {
            var full = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(projectRoot, raw));
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Solution file not found: {raw} (resolved to {full})");
            }
            var ext = Path.GetExtension(full);
            if (!SolutionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{raw}' is not a .sln or .slnx file (extension: '{ext}').");
            }
            resolved.Add(full);
        }
        return resolved;
    }

    private static async Task<int> CountScopedProjectsAsync(IReadOnlyList<string> solutionPaths, CancellationToken ct)
    {
        var projects = await SolutionParser.GetCSharpProjectPathsAsync(solutionPaths, ct);
        return projects.Count;
    }

    /// <summary>
    /// Threshold above which the success message prepends a warning that the
    /// first symbol call may time out while Roslyn loads the workspace.
    /// Calibrated against OnBase (185 projects \u2192 10\u201330 min cold load).
    /// </summary>
    private const int LargeScopeProjectThreshold = 50;

    private static string FormatSuccess(string projectRoot, IReadOnlyList<string> solutionPaths, int projectCount)
    {
        var sb = new StringBuilder();
        if (projectCount > LargeScopeProjectThreshold)
        {
            sb.AppendLine($"\u26a0\ufe0f Large scope ({projectCount} projects). First symbol call may take 5\u201315 min while Roslyn loads.");
            sb.AppendLine("Prefer search_for_pattern for initial exploration; call get_language_server_status to check readiness; use kill_language_server if it stalls.");
            sb.AppendLine();
        }
        sb.AppendLine($"C# scope set to {solutionPaths.Count} solution(s) ({projectCount} C# project(s)):");
        foreach (var path in solutionPaths)
        {
            sb.AppendLine($"  - {Path.GetRelativePath(projectRoot, path)}");
        }
        sb.AppendLine();
        sb.AppendLine("Persisted to .serena/project.yml under csharp.scope.solutions.");
        sb.AppendLine("C# language server restarted; next request will pick up the new scope.");
        return sb.ToString().TrimEnd();
    }
}

[CanEdit]
public sealed class ClearActiveSolutionTool : ToolBase
{
    public ClearActiveSolutionTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Clear the C# Roslyn scope previously set by 'set_active_solution'. Removes " +
        "csharp.scope.solutions from .serena/project.yml and stops the running C# language server " +
        "(it will be re-started lazily on the next semantic request, with NO scope — i.e., the " +
        "legacy whole-repo glob). Use this to undo a scope choice or to free Roslyn memory. On " +
        "very large repos, prefer to set a new narrower scope instead of clearing entirely.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() => [];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var project = Context.ActiveProject
            ?? throw new InvalidOperationException("No active project. Call activate_project first.");

        bool wasScoped = !project.GetCSharpScope().IsEmpty;
        project.UpdateAndPersistCSharpScope(null);
        await Context.Agent.RestartLanguageServerAsync(Language.CSharp, ct);

        return wasScoped
            ? "C# scope cleared. csharp.scope.solutions removed from .serena/project.yml. C# language server stopped; next semantic request will reload with no scope (whole-repo glob)."
            : "No active C# scope to clear. C# language server restarted anyway.";
    }
}

[CanEdit]
[NoActiveProjectRequired]
public sealed class KillLanguageServerTool : ToolBase
{
    public KillLanguageServerTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Force-kill ALL running language server processes (no graceful shutdown, no auto-restart). " +
        "Use when a language server is consuming excessive CPU or memory (e.g., Roslyn cold-loading " +
        "a huge repo) and you want to stop it immediately. Symbol caches are NOT flushed. The " +
        "language server will start fresh on the next semantic request.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() => [];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var active = Context.Agent.ActiveLanguageServers;
        if (active.Count == 0)
        {
            return "No language servers are currently running.";
        }

        await Context.Agent.ForceResetLanguageServerManagerAsync();
        return $"Force-killed {active.Count} language server(s): {string.Join(", ", active)}. They will restart on the next semantic request.";
    }
}

/// <summary>
/// Reports the workspace-load readiness of each running language server.
/// Used by the agent to decide whether to wait, retry, or pivot to
/// search_for_pattern after a warming response from a symbol tool.
/// </summary>
[NoActiveProjectRequired]
public sealed class LanguageServerStatusTool : ToolBase
{
    public LanguageServerStatusTool(IToolContext context) : base(context) { }

    // Override default name (would be "language_server_status"). Prefixing with
    // "get_" matches sibling read-only status tools (get_current_config) and
    // the verb the InitialInstructions guidance uses.
    public override string Name => "get_language_server_status";

    public override string Description =>
        "Report the workspace-load readiness of each running language server. " +
        "Returns a JSON object keyed by language with state (NotStarted/Loading/Ready/Failed), " +
        "elapsed seconds since load began, and scope description when known. " +
        "Call after set_active_solution (especially on large solutions) to know when " +
        "find_symbol / find_referencing_symbols are viable. Cheap; safe to poll.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() => [];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var entries = new Dictionary<string, object?>();

        // Always include CSharp (the primary scope-driven language) so the
        // agent can poll readiness even before a server has been started.
        var languagesToReport = new HashSet<Language> { Language.CSharp };
        foreach (var name in Context.Agent.ActiveLanguageServers)
        {
            if (Enum.TryParse<Language>(name, ignoreCase: true, out var lang))
            {
                languagesToReport.Add(lang);
            }
        }

        foreach (var lang in languagesToReport)
        {
            var snap = Context.Agent.GetLanguageServerReadyState(lang);
            entries[lang.ToString().ToLowerInvariant()] = new Dictionary<string, object?>
            {
                ["state"] = snap.State.ToString(),
                ["projects_loaded"] = snap.ProjectsLoaded,
                ["projects_total"] = snap.ProjectsTotal,
                ["elapsed_seconds"] = Math.Round(snap.ElapsedSeconds, 1),
                ["scope"] = snap.ScopeDescription,
                ["advice"] = AdviceFor(snap.State),
            };
        }

        var json = System.Text.Json.JsonSerializer.Serialize(
            entries,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(json);
    }

    private static string AdviceFor(WorkspaceReadyState state) => state switch
    {
        WorkspaceReadyState.NotStarted => "Server not started. Call warm_language_server to begin warmup, then poll this tool.",
        WorkspaceReadyState.Loading => "Server warming. Use search_for_pattern; poll this tool again before retrying symbol calls.",
        WorkspaceReadyState.Ready => "Ready for symbol queries.",
        WorkspaceReadyState.Failed => "Workspace load failed. Use kill_language_server, then activate_project / set_active_solution to retry.",
        _ => "",
    };
}

/// <summary>
/// v1.0.27: Eagerly starts the language server for the active project so
/// workspace warmup begins without requiring a real symbol call. Returns
/// immediately with the current readiness snapshot; warmup continues in
/// the background. Pair with get_language_server_status to poll for Ready.
/// </summary>
public sealed class WarmLanguageServerTool : ToolBase
{
    public WarmLanguageServerTool(IToolContext context) : base(context) { }

    public override string Name => "warm_language_server";

    public override string Description =>
        "Start the language server and begin workspace load (Roslyn solution/open + indexing) WITHOUT issuing a symbol query. " +
        "Returns immediately with a readiness snapshot; warmup runs in the background (10–30 min on large solutions). " +
        "Use immediately after set_active_solution, then poll get_language_server_status until state == Ready before " +
        "calling solution-wide find_symbol, find_referencing_symbols, or rename_symbol.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new ToolParameter(
            Name: "language",
            Description: "Language to warm up. Defaults to 'csharp' (the only language that currently uses Roslyn).",
            Type: typeof(string),
            Required: false,
            DefaultValue: "csharp"),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string langName = arguments.TryGetValue("language", out var v) && v is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : "csharp";

        if (!Enum.TryParse<Language>(langName, ignoreCase: true, out var language))
        {
            return $"{{\"error\": \"Unknown language '{langName}'. Try 'csharp'.\"}}";
        }

        var snapshot = await Context.Agent.WarmLanguageServerAsync(language, ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>
        {
            ["language"] = language.ToString().ToLowerInvariant(),
            ["state"] = snapshot.State.ToString(),
            ["projects_loaded"] = snapshot.ProjectsLoaded,
            ["projects_total"] = snapshot.ProjectsTotal,
            ["elapsed_seconds"] = Math.Round(snapshot.ElapsedSeconds, 1),
            ["scope"] = snapshot.ScopeDescription,
            ["advice"] = snapshot.State == WorkspaceReadyState.Ready
                ? "Already Ready — symbol tools are viable now."
                : "Warmup started. Poll get_language_server_status until state == Ready before calling find_symbol (solution-wide), find_referencing_symbols, or rename_symbol.",
        };

        return System.Text.Json.JsonSerializer.Serialize(
            payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}





