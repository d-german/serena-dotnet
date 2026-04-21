// Memory, Config & Shell Tools - Phase 6D
// Tools for: read_memory, write_memory, list_memories, execute_shell_command, etc.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Serena.Core.Project;

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
        try
        {
            string content = mm.LoadMemory(memoryName);
            return Task.FromResult(content);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult($"Error: Memory '{memoryName}' not found.");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
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
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string memoryName = GetRequired<string>(arguments, "memory_name");
        string content = GetRequired<string>(arguments, "content");

        var mm = CreateMemoriesManager();
        try
        {
            mm.SaveMemory(memoryName, content);
            return Task.FromResult($"Memory '{memoryName}' saved ({content.Length} chars).");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
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
        try
        {
            mm.DeleteMemory(memoryName);
            return Task.FromResult($"Memory '{memoryName}' deleted.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
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
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string command = GetRequired<string>(arguments, "command");
        string? cwd = GetOptional<string?>(arguments, "cwd", null);
        bool captureStderr = GetOptional(arguments, "capture_stderr", true);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);

        string projectRoot = RequireProjectRoot();
        string workingDir = cwd is not null ? ResolvePath(cwd) : projectRoot;

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start process.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = captureStderr
                ? process.StandardError.ReadToEndAsync(timeoutCts.Token)
                : Task.FromResult("");

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            var result = new ShellResult(stdout, captureStderr ? stderr : null, process.ExitCode);
            return LimitLength(ToJson(result), maxChars);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill
            }
            return ToJson(new { error = $"Command timed out after {DefaultTimeoutSeconds} seconds." });
        }
    }

    private sealed record ShellResult(string Stdout, string? Stderr, int ExitCode);
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
        try
        {
            mm.MoveMemory(oldName, newName);
            return Task.FromResult($"Memory renamed from '{oldName}' to '{newName}'.");
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult($"Error: Memory '{oldName}' not found.");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
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
        try
        {
            mm.EditMemory(memoryName, needle, repl, mode, allowMultiple);
            return Task.FromResult($"Memory '{memoryName}' updated successfully.");
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult($"Error: Memory '{memoryName}' not found.");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
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
            return $"Error: {result.Error}";
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

        return Task.FromResult(result.IsSuccess
            ? $"Removed project '{project}'."
            : $"Error: {result.Error}");
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
        ITool? tool = Context.Agent.Tools.Get(toolName);
        if (tool is null)
        {
            return $"Error: Tool '{toolName}' not found.";
        }

        // Only allow read-only tools (no [CanEdit] or [SymbolicEdit] attributes).
        if (tool is ToolBase tb && tb.CanEdit)
        {
            return $"Error: Tool '{toolName}' is not read-only and cannot be used with query_project.";
        }

        // Parse the tool parameters JSON.
        Dictionary<string, object?>? parsedParams;
        try
        {
            parsedParams = ParseToolParams(toolParamsJson);
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid tool_params JSON: {ex.Message}";
        }

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


