// Integration Tests - Phase F1
// Tests that verify multi-component integration without requiring external LSP.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Editor;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

/// <summary>
/// Integration tests for the tool wiring pipeline:
/// Agent → ToolContext → Tool → ToolRegistry → MCP Bridge
/// </summary>
public class ToolWiringIntegrationTests
{
    private static (SerenaAgent Agent, ToolRegistry Registry, AgentToolContext Context) CreateFullStack()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool(context));
        registry.Register(new CreateTextFileTool(context));
        registry.Register(new ListDirTool(context));
        registry.Register(new FindFileTool(context));
        registry.Register(new SearchForPatternTool(context));
        registry.Register(new FindSymbolTool(context));
        registry.Register(new GetSymbolsOverviewTool(context));
        registry.Register(new FindReferencingSymbolsTool(context));
        registry.Register(new ReadMemoryTool(context));
        registry.Register(new WriteMemoryTool(context));
        registry.Register(new ListMemoriesTool(context));
        registry.Register(new DeleteMemoryTool(context));
        registry.Register(new ExecuteShellCommandTool(context));
        registry.Register(new ActivateProjectTool(context));
        registry.Register(new GetCurrentConfigTool(context));
        registry.Register(new ReplaceSymbolBodyTool(context));
        registry.Register(new InsertBeforeSymbolTool(context));
        registry.Register(new InsertAfterSymbolTool(context));
        registry.Register(new ReplaceContentTool(context));
        registry.Register(new RenameSymbolTool(context));
        registry.Register(new SafeDeleteSymbolTool(context));
        registry.Register(new RenameMemoryTool(context));
        registry.Register(new EditMemoryTool(context));
        registry.Register(new CheckOnboardingPerformedTool(context));
        registry.Register(new OnboardingTool(context));
        registry.Register(new InitialInstructionsTool(context));
        registry.Register(new RemoveProjectTool(context));
        registry.Register(new RestartLanguageServerTool(context));
        registry.Register(new ListQueryableProjectsTool(context));
        registry.Register(new QueryProjectTool(context));
        registry.Register(new DeleteLinesTool(context));
        registry.Register(new InsertAtLineTool(context));
        registry.Register(new ReplaceLinesTool(context));

        agent.SetToolRegistry(registry);
        return (agent, registry, context);
    }

    [Fact]
    public void FullStack_AllToolsRegistered()
    {
        var (_, registry, _) = CreateFullStack();
        Assert.Equal(33, registry.All.Count);
    }

    [Fact]
    public void FullStack_AllToolsHaveNames()
    {
        var (_, registry, _) = CreateFullStack();
        foreach (var tool in registry.All)
        {
            Assert.False(string.IsNullOrEmpty(tool.Name), $"Tool {tool.GetType().Name} has no name");
            Assert.False(string.IsNullOrEmpty(tool.Description), $"Tool {tool.Name} has no description");
        }
    }

    [Fact]
    public void FullStack_AllToolsHaveParametersOrAreParameterless()
    {
        var (_, registry, _) = CreateFullStack();
        // Most tools have parameters; these are parameterless
        string[] parameterlessTools = ["get_current_config", "check_onboarding_performed", "onboarding", "initial_instructions", "restart_language_server", "list_queryable_projects"];
        var parameterizedTools = registry.All.Where(t => !parameterlessTools.Contains(t.Name));
        foreach (var tool in parameterizedTools)
        {
            Assert.True(tool.Parameters.Count > 0, $"Tool {tool.Name} has no parameters");
        }
    }

    [Fact]
    public void FullStack_AllToolsHaveValidJsonSchema()
    {
        var (_, registry, _) = CreateFullStack();
        foreach (var tool in registry.All)
        {
            using var schema = ToolBase.GenerateJsonSchema(tool.Parameters);
            var root = schema.RootElement;

            Assert.Equal("object", root.GetProperty("type").GetString());
            Assert.True(root.TryGetProperty("properties", out _),
                $"Tool {tool.Name} schema missing 'properties'");
        }
    }

    [Fact]
    public void FullStack_ToolLookupByName()
    {
        var (_, registry, _) = CreateFullStack();

        Assert.NotNull(registry.All.First(t => t.Name == "read_file"));
        Assert.NotNull(registry.All.First(t => t.Name == "find_symbol"));
        Assert.NotNull(registry.All.First(t => t.Name == "replace_content"));
    }

    [Fact]
    public async Task FullStack_GetCurrentConfig_NoProject()
    {
        var (_, registry, _) = CreateFullStack();
        var tool = registry.All.First(t => t.Name == "get_current_config");

        // Should work even without active project
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Contains("serena", result, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Integration tests for file tools against real temporary files.
/// </summary>
public class FileToolIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SerenaAgent _agent;
    private readonly ToolRegistry _registry;

    public FileToolIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // Create test files
        File.WriteAllText(Path.Combine(_tempDir, "hello.txt"), "Hello, World!\nLine 2\nLine 3");
        File.WriteAllText(Path.Combine(_tempDir, "data.cs"), "namespace Test;\npublic class Foo { }");
        Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        File.WriteAllText(Path.Combine(_tempDir, "sub", "nested.txt"), "Nested content");

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        _agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(_agent, loggerFactory);

        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool(context));
        _registry.Register(new CreateTextFileTool(context));
        _registry.Register(new ListDirTool(context));
        _registry.Register(new FindFileTool(context));
        _registry.Register(new SearchForPatternTool(context));

        _agent.SetToolRegistry(_registry);
        _agent.ActivateProjectAsync(_tempDir).Wait();
    }

    [Fact]
    public async Task ReadFile_ReadsExistingFile()
    {
        var tool = _registry.All.First(t => t.Name == "read_file");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["relative_path"] = "hello.txt" },
            CancellationToken.None);

        Assert.Contains("Hello, World!", result);
    }

    [Fact]
    public async Task ReadFile_WithLineRange()
    {
        var tool = _registry.All.First(t => t.Name == "read_file");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "hello.txt",
                ["start_line"] = 1,
                ["end_line"] = 1,
            },
            CancellationToken.None);

        Assert.Contains("Line 2", result);
        Assert.DoesNotContain("Hello, World!", result);
    }

    [Fact]
    public async Task CreateTextFile_CreatesNew()
    {
        var tool = _registry.All.First(t => t.Name == "create_text_file");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "newfile.txt",
                ["content"] = "Created content",
            },
            CancellationToken.None);

        Assert.Contains("created", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(_tempDir, "newfile.txt")));
        Assert.Equal("Created content", File.ReadAllText(Path.Combine(_tempDir, "newfile.txt")));
    }

    [Fact]
    public async Task ListDir_ListsContents()
    {
        var tool = _registry.All.First(t => t.Name == "list_dir");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = ".",
                ["recursive"] = false,
            },
            CancellationToken.None);

        Assert.Contains("hello.txt", result);
        Assert.Contains("sub", result);
    }

    [Fact]
    public async Task FindFile_FindsByMask()
    {
        var tool = _registry.All.First(t => t.Name == "find_file");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["file_mask"] = "*.cs",
                ["relative_path"] = ".",
            },
            CancellationToken.None);

        Assert.Contains("data.cs", result);
    }

    [Fact]
    public async Task SearchForPattern_FindsMatch()
    {
        var tool = _registry.All.First(t => t.Name == "search_for_pattern");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["substring_pattern"] = "class Foo",
            },
            CancellationToken.None);

        Assert.Contains("data.cs", result);
        Assert.Contains("class Foo", result);
    }

    [Fact]
    public async Task ReadFile_PathTraversal_Blocked()
    {
        var tool = _registry.All.First(t => t.Name == "read_file");
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["relative_path"] = "../../../etc/passwd" },
            CancellationToken.None);

        Assert.Contains("Error", result);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

/// <summary>
/// Integration tests for ReplaceContent tool (literal and regex modes).
/// Tests file editing without requiring LSP (ReplaceContent doesn't need symbol resolution).
/// </summary>
public class ReplaceContentIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITool _replaceContentTool;

    public ReplaceContentIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_edit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        var registry = new ToolRegistry();
        registry.Register(new ReplaceContentTool(context));
        agent.SetToolRegistry(registry);
        agent.ActivateProjectAsync(_tempDir).Wait();

        _replaceContentTool = registry.All.First(t => t.Name == "replace_content");
    }

    [Fact]
    public async Task LiteralReplace_SingleOccurrence()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test.cs"), "int x = 1;\nint y = 2;");

        var result = await _replaceContentTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "test.cs",
                ["needle"] = "int x = 1;",
                ["repl"] = "int x = 42;",
                ["mode"] = "literal",
            },
            CancellationToken.None);

        Assert.Contains("Replaced", result);
        string content = File.ReadAllText(Path.Combine(_tempDir, "test.cs"));
        Assert.Contains("int x = 42;", content);
        Assert.Contains("int y = 2;", content);
    }

    [Fact]
    public async Task RegexReplace_SingleOccurrence()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test2.cs"), "Console.WriteLine(\"hello\");");

        var result = await _replaceContentTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "test2.cs",
                ["needle"] = "Console\\.WriteLine\\(\"(\\w+)\"\\)",
                ["repl"] = "Logger.Info(\"$!1\")",
                ["mode"] = "regex",
            },
            CancellationToken.None);

        Assert.Contains("Replaced", result);
        string content = File.ReadAllText(Path.Combine(_tempDir, "test2.cs"));
        Assert.Contains("Logger.Info(\"hello\")", content);
    }

    [Fact]
    public async Task LiteralReplace_MultipleOccurrences_Blocked()
    {
        File.WriteAllText(Path.Combine(_tempDir, "multi.cs"), "foo bar foo");

        var result = await _replaceContentTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "multi.cs",
                ["needle"] = "foo",
                ["repl"] = "baz",
                ["mode"] = "literal",
                ["allow_multiple_occurrences"] = false,
            },
            CancellationToken.None);

        // Should fail because multiple occurrences and allow_multiple_occurrences=false
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task LiteralReplace_MultipleOccurrences_Allowed()
    {
        File.WriteAllText(Path.Combine(_tempDir, "multi2.cs"), "foo bar foo");

        var result = await _replaceContentTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "multi2.cs",
                ["needle"] = "foo",
                ["repl"] = "baz",
                ["mode"] = "literal",
                ["allow_multiple_occurrences"] = true,
            },
            CancellationToken.None);

        Assert.Contains("Replaced 2 occurrence", result);
        Assert.Equal("baz bar baz", File.ReadAllText(Path.Combine(_tempDir, "multi2.cs")));
    }

    [Fact]
    public async Task LiteralReplace_NotFound()
    {
        File.WriteAllText(Path.Combine(_tempDir, "nf.cs"), "hello world");

        var result = await _replaceContentTool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "nf.cs",
                ["needle"] = "nonexistent",
                ["repl"] = "x",
                ["mode"] = "literal",
            },
            CancellationToken.None);

        Assert.Contains("Error", result);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

/// <summary>
/// Integration tests for memory tool CRUD operations.
/// </summary>
public class MemoryToolIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public MemoryToolIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_mem_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        _registry = new ToolRegistry();
        _registry.Register(new ReadMemoryTool(context));
        _registry.Register(new WriteMemoryTool(context));
        _registry.Register(new ListMemoriesTool(context));
        _registry.Register(new DeleteMemoryTool(context));

        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    [Fact]
    public async Task WriteAndReadMemory()
    {
        var write = _registry.All.First(t => t.Name == "write_memory");
        var read = _registry.All.First(t => t.Name == "read_memory");

        await write.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["memory_name"] = "test/note",
                ["content"] = "Test content here",
            },
            CancellationToken.None);

        var result = await read.ExecuteAsync(
            new Dictionary<string, object?> { ["memory_name"] = "test/note" },
            CancellationToken.None);

        Assert.Contains("Test content here", result);
    }

    [Fact]
    public async Task ListMemories_ShowsWritten()
    {
        var write = _registry.All.First(t => t.Name == "write_memory");
        var list = _registry.All.First(t => t.Name == "list_memories");

        await write.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["memory_name"] = "mylist/item1",
                ["content"] = "Item 1",
            },
            CancellationToken.None);

        var result = await list.ExecuteAsync(
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.Contains("mylist/item1", result);
    }

    [Fact]
    public async Task DeleteMemory()
    {
        var write = _registry.All.First(t => t.Name == "write_memory");
        var delete = _registry.All.First(t => t.Name == "delete_memory");
        var list = _registry.All.First(t => t.Name == "list_memories");

        await write.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["memory_name"] = "todelete",
                ["content"] = "Delete me",
            },
            CancellationToken.None);

        await delete.ExecuteAsync(
            new Dictionary<string, object?> { ["memory_name"] = "todelete" },
            CancellationToken.None);

        var result = await list.ExecuteAsync(
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.DoesNotContain("todelete", result);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

/// <summary>
/// Config loading integration tests.
/// </summary>
public class ConfigIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void LoadFromFile_ValidYaml()
    {
        string yamlPath = Path.Combine(_tempDir, "config.yml");
        File.WriteAllText(yamlPath, @"
default_project: myproject
projects:
  - name: myproject
    path: /some/path
");

        var result = SerenaConfig.LoadFromFile(yamlPath, NullLogger<SerenaConfig>.Instance);

        Assert.True(result.IsSuccess);
        Assert.Equal("myproject", result.Value.DefaultProject);
        Assert.True(result.Value.RegisteredProjects.ContainsKey("myproject"));
    }

    [Fact]
    public void LoadFromFile_MissingFile_ReturnsSuccessWithDefaults()
    {
        // Missing file returns success with default config (not failure)
        var result = SerenaConfig.LoadFromFile(
            Path.Combine(_tempDir, "nonexistent.yml"),
            NullLogger<SerenaConfig>.Instance);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.DefaultProject);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
