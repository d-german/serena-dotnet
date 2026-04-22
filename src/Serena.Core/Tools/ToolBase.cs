// Tool Base Class & Registry - Ported from serena/tools/tools_base.py
// Phase 6A: ITool, ToolBase, ToolRegistry, ToolMarkers

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Serena.Core.Agent;
using Serena.Core.Editor;
using Serena.Core.Hooks;
using Serena.Core.Project;

namespace Serena.Core.Tools;

/// <summary>
/// Marker attributes for tool categorization.
/// Used by context/mode system for filtering.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class CanEditAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class SymbolicReadAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class SymbolicEditAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class OptionalToolAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class BetaToolAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class NoActiveProjectRequiredAttribute : Attribute;

/// <summary>
/// Represents a parameter for a tool.
/// </summary>
public sealed record ToolParameter(
    string Name,
    string Description,
    Type Type,
    bool Required,
    object? DefaultValue = null);

/// <summary>
/// Interface for all Serena tools.
/// </summary>
public interface ITool
{
    /// <summary>
    /// The snake_case name of the tool (e.g., "find_symbol").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description shown to the LLM.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The tool's parameters.
    /// </summary>
    IReadOnlyList<ToolParameter> Parameters { get; }

    /// <summary>
    /// Executes the tool with the given arguments.
    /// </summary>
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default);
}

/// <summary>
/// Base class for all Serena tools.
/// Handles logging, error wrapping, result truncation, and parameter extraction.
/// </summary>
public abstract class ToolBase : ITool
{
    protected readonly IToolContext Context;
    protected readonly ILogger Logger;
    private readonly int _maxResultLength;

    /// <summary>
    /// Cached parameters extracted from the ApplyAsync method signature.
    /// </summary>
    private IReadOnlyList<ToolParameter>? _parameters;

    protected ToolBase(IToolContext context, int maxResultLength = -1)
    {
        Context = context;
        Logger = context.LoggerFactory.CreateLogger(GetType());
        _maxResultLength = maxResultLength;
    }

    public virtual string Name => DeriveNameFromType(GetType());

    public abstract string Description { get; }

    public IReadOnlyList<ToolParameter> Parameters =>
        _parameters ??= ExtractParameters();

    public async Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        // Run pre-tool-use hooks before execution.
        var (hookResult, hookMessage) = await Context.Agent.RunPreToolHooksAsync(Name, arguments, ct);

        if (hookResult == HookResult.Deny)
        {
            return $"Error: {hookMessage}";
        }

        var result = await ExecuteSafeAsync(arguments, ct);
        string output = result.IsSuccess ? result.Value : $"Error: {result.Error}";

        if (hookResult == HookResult.AllowWithMessage && !string.IsNullOrEmpty(hookMessage))
        {
            output = hookMessage + "\n\n" + output;
        }

        return output;
    }

    /// <summary>
    /// Executes the tool, returning a <see cref="CSharpFunctionalExtensions.Result{T}"/>
    /// instead of a bare string. Tool failures are captured as Result.Failure.
    /// </summary>
    public async Task<Result<string>> ExecuteSafeAsync(
        IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        Logger.LogDebug("Executing tool {Name} with args: {Args}", Name, arguments);
        try
        {
            string result = await ApplyAsync(arguments, ct);
            if (_maxResultLength > 0 && result.Length > _maxResultLength)
            {
                result = result[.._maxResultLength] + $"\n... (truncated, {result.Length} total chars)";
            }
            return Result.Success(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Tool {Name} failed", Name);
            return Result.Failure<string>(ex.Message);
        }
    }

    /// <summary>
    /// Implement this in each tool. Process the arguments and return the result string.
    /// </summary>
    protected abstract Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);

    /// <summary>
    /// Helper to get a required argument from the arguments dictionary.
    /// </summary>
    protected static T GetRequired<T>(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            throw new ArgumentException($"Required parameter '{name}' is missing.");
        }
        if (value is T typed)
        {
            return typed;
        }
        return (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>
    /// Helper to get an optional argument with a default value.
    /// </summary>
    protected static T GetOptional<T>(IReadOnlyDictionary<string, object?> args, string name, T defaultValue)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }
        if (value is T typed)
        {
            return typed;
        }
        return (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>
    /// Helper to get an optional list argument. Returns null when the parameter is absent.
    /// Handles conversion from <c>List&lt;object?&gt;</c> (produced by MCP JSON deserialization)
    /// to the requested element type.
    /// </summary>
    protected static List<T>? GetOptionalList<T>(IReadOnlyDictionary<string, object?> args, string name)
        where T : struct
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }
        if (value is List<T> typed)
        {
            return typed;
        }
        if (value is IEnumerable<object?> objects)
        {
            return objects.Select(o => (T)Convert.ChangeType(o!, typeof(T))).ToList();
        }
        return null;
    }

    /// <summary>
    /// Gets the active project root, throwing if no project is active.
    /// </summary>
    protected string RequireProjectRoot() =>
        Context.ProjectRoot
        ?? throw new InvalidOperationException("No active project. Call activate_project first.");

    /// <summary>
    /// Resolves a relative path against the project root and validates it stays within bounds.
    /// </summary>
    protected string ResolvePath(string relativePath)
    {
        string root = RequireProjectRoot();
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Path '{relativePath}' resolves outside the project root.");
        }
        return fullPath;
    }

    /// <summary>
    /// Gets the file encoding configured for the active project, defaulting to UTF-8.
    /// </summary>
    protected Encoding GetProjectEncoding()
    {
        string? encodingName = Context.ActiveProject?.Config?.Encoding;
        return string.IsNullOrEmpty(encodingName)
            ? Encoding.UTF8
            : Encoding.GetEncoding(encodingName);
    }

    /// <summary>
    /// Best-effort LSP notification when a file's content changes.
    /// </summary>
    protected async Task TryNotifyLspAsync(string absolutePath, string content, CancellationToken ct)
    {
        try
        {
            var lsp = await Context.Agent.GetLanguageServerForFileAsync(absolutePath, ct);
            if (lsp is not null)
            {
                await lsp.NotifyFileChangedAsync(absolutePath, content);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "LSP notification failed for {Path} (best-effort)", absolutePath);
        }
    }

    /// <summary>
    /// Truncates text if it exceeds maxChars. Pass -1 to skip truncation.
    /// </summary>
    protected static string LimitLength(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }
        return text[..maxChars] + $"\n... (truncated at {maxChars} of {text.Length} chars)";
    }

    /// <summary>
    /// Limits a result string using a chain of progressively shorter factory functions.
    /// Delegates to <see cref="ToolResultFormatter.LimitWithFactories"/>.
    /// </summary>
    protected static string LimitLength(string result, int maxAnswerChars, params Func<string>[] shortenedFactories)
    {
        return ToolResultFormatter.LimitWithFactories(result, maxAnswerChars, shortenedFactories);
    }

    /// <summary>
    /// Serializes an object to JSON with snake_case property naming.
    /// </summary>
    protected static string ToJson(object value) =>
        JsonSerializer.Serialize(value, s_jsonOptions);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Checks whether a relative path should be ignored.
    /// Uses the active project's GitIgnoreFilter when available, falling back to
    /// <see cref="FileSystemHelpers.IsDefaultIgnored"/> when no project is active.
    /// </summary>
    protected bool IsPathIgnored(string relativePath)
    {
        var project = Context.ActiveProject;
        if (project is not null)
        {
            return project.IsIgnoredPath(relativePath);
        }
        return FileSystemHelpers.IsDefaultIgnored(relativePath);
    }

    /// <summary>
    /// Creates a MemoriesManager for the current project (or global-only if no project is active).
    /// </summary>
    protected MemoriesManager CreateMemoriesManager()
    {
        string? projectRoot = Context.ProjectRoot;
        string? serenaDir = projectRoot is not null
            ? Path.Combine(projectRoot, ".serena")
            : null;
        return new MemoriesManager(serenaDir);
    }

    /// <summary>
    /// Gets the ISymbolRetriever for the active project.
    /// Requires an LSP client for a file in the project to be available.
    /// </summary>
    protected async Task<ISymbolRetriever> RequireSymbolRetrieverAsync(
        string relativePath, CancellationToken ct = default)
    {
        string root = RequireProjectRoot();
        string absPath = Path.GetFullPath(Path.Combine(root, relativePath));

        // When the path is a directory, find a representative source file to determine the language
        string lspLookupPath = absPath;
        if (Directory.Exists(absPath))
        {
            lspLookupPath = Directory.EnumerateFiles(absPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(LanguageServerSymbolRetriever.CanAnalyzeFile)
                ?? absPath;
        }

        var lsp = await Context.Agent.GetLanguageServerForFileAsync(lspLookupPath, ct)
            ?? throw new InvalidOperationException(
                $"No language server available for '{relativePath}'. Ensure a language server is configured.");

        var logger = Context.LoggerFactory.CreateLogger<LanguageServerSymbolRetriever>();
        var cache = Context.Agent.GetSymbolCacheForLanguage(lsp.Language);
        return new LanguageServerSymbolRetriever(lsp, root, logger, cache);
    }

    /// <summary>
    /// Gets the ICodeEditor for the active project.
    /// Requires an LSP client for a file in the project to be available.
    /// </summary>
    protected async Task<ICodeEditor> RequireCodeEditorAsync(
        string relativePath, CancellationToken ct = default)
    {
        string root = RequireProjectRoot();
        string absPath = Path.GetFullPath(Path.Combine(root, relativePath));

        // When the path is a directory, find a representative source file to determine the language
        string lspLookupPath = absPath;
        if (Directory.Exists(absPath))
        {
            lspLookupPath = Directory.EnumerateFiles(absPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(LanguageServerSymbolRetriever.CanAnalyzeFile)
                ?? absPath;
        }

        var lsp = await Context.Agent.GetLanguageServerForFileAsync(lspLookupPath, ct)
            ?? throw new InvalidOperationException(
                $"No language server available for '{relativePath}'. Ensure a language server is configured.");

        var logger = Context.LoggerFactory.CreateLogger<LanguageServerCodeEditor>();
        var cache = Context.Agent.GetSymbolCacheForLanguage(lsp.Language);
        var retriever = new LanguageServerSymbolRetriever(lsp, root,
            Context.LoggerFactory.CreateLogger<LanguageServerSymbolRetriever>(), cache);
        return new LanguageServerCodeEditor(retriever, lsp, root, logger);
    }

    /// <summary>
    /// Derives a snake_case tool name from the class type.
    /// MyFancyTool → my_fancy (strips "Tool" suffix, converts PascalCase → snake_case).
    /// </summary>
    public static string DeriveNameFromType(Type type)
    {
        string name = type.Name;
        if (name.EndsWith("Tool", StringComparison.Ordinal))
        {
            name = name[..^4];
        }
        return Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();
    }

    /// <summary>
    /// Extracts parameter information from the concrete tool's constructor or apply method.
    /// Override to provide custom parameters.
    /// </summary>
    protected virtual IReadOnlyList<ToolParameter> ExtractParameters() => [];

    /// <summary>
    /// Generates a JSON Schema object from tool parameters for MCP registration.
    /// </summary>
    public static JsonDocument GenerateJsonSchema(IReadOnlyList<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in parameters)
        {
            var prop = new Dictionary<string, object>
            {
                ["description"] = param.Description,
            };

            ApplyTypeSchema(prop, param.Type);

            if (param.DefaultValue is not null)
            {
                prop["default"] = param.DefaultValue;
            }

            properties[param.Name] = prop;

            if (param.Required)
            {
                required.Add(param.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        string json = JsonSerializer.Serialize(schema, s_jsonOptions);
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Sets "type" (and "items" for arrays) on a JSON Schema property dictionary.
    /// </summary>
    private static void ApplyTypeSchema(Dictionary<string, object> prop, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            prop["type"] = "array";
            prop["items"] = new Dictionary<string, object>
            {
                ["type"] = MapClrTypeToJsonSchemaType(type.GetGenericArguments()[0]),
            };
        }
        else
        {
            prop["type"] = MapClrTypeToJsonSchemaType(type);
        }
    }

    private static string MapClrTypeToJsonSchemaType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            return "string";
        }
        if (type == typeof(int) || type == typeof(long))
        {
            return "integer";
        }
        if (type == typeof(bool))
        {
            return "boolean";
        }
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return "number";
        }
        return "string";
    }

    // ---- Marker checks ----

    public bool CanEdit => GetType().GetCustomAttribute<CanEditAttribute>() is not null
                        || GetType().GetCustomAttribute<SymbolicEditAttribute>() is not null;

    public bool IsSymbolicRead => GetType().GetCustomAttribute<SymbolicReadAttribute>() is not null;

    public bool IsSymbolicEdit => GetType().GetCustomAttribute<SymbolicEditAttribute>() is not null;

    public bool IsOptional => GetType().GetCustomAttribute<OptionalToolAttribute>() is not null;

    public bool IsBeta => GetType().GetCustomAttribute<BetaToolAttribute>() is not null;

    public bool RequiresActiveProject => GetType().GetCustomAttribute<NoActiveProjectRequiredAttribute>() is null;
}

/// <summary>
/// Registry that discovers and manages all available tools.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tool instance.
    /// </summary>
    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    /// <summary>
    /// Gets a tool by name.
    /// </summary>
    public ITool? Get(string name) =>
        _tools.GetValueOrDefault(name);

    /// <summary>
    /// Gets all registered tools.
    /// </summary>
    public IReadOnlyCollection<ITool> All => _tools.Values;

    /// <summary>
    /// Gets all tool names.
    /// </summary>
    public IReadOnlyCollection<string> Names => _tools.Keys;

    /// <summary>
    /// Gets tools matching a filter predicate.
    /// </summary>
    public IReadOnlyList<ITool> Where(Func<ITool, bool> predicate) =>
        _tools.Values.Where(predicate).ToList();

}

/// <summary>
/// Provides access to the agent, project, and other services for tools.
/// Ported from Python Component base class (tools_base.py).
/// </summary>
public interface IToolContext
{
    /// <summary>The Serena agent orchestrator.</summary>
    SerenaAgent Agent { get; }

    /// <summary>The currently active project, or null if none is activated.</summary>
    Project.SerenaProject? ActiveProject { get; }

    /// <summary>Root directory of the active project, or null.</summary>
    string? ProjectRoot { get; }

    /// <summary>Logger factory for creating typed loggers.</summary>
    ILoggerFactory LoggerFactory { get; }
}

/// <summary>
/// Production implementation of <see cref="IToolContext"/> backed by a <see cref="SerenaAgent"/>.
/// </summary>
public sealed class AgentToolContext : IToolContext
{
    public AgentToolContext(SerenaAgent agent, ILoggerFactory loggerFactory)
    {
        Agent = agent;
        LoggerFactory = loggerFactory;
    }

    public SerenaAgent Agent { get; }
    public Project.SerenaProject? ActiveProject => Agent.ActiveProject;
    public string? ProjectRoot => Agent.ActiveProject?.Root;
    public ILoggerFactory LoggerFactory { get; }
}
