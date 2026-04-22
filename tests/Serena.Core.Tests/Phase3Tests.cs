// Tests for Phase 3 changes: new tools, modified tools, line editing
// Covers: search filters, shell output format, write_memory truncation,
//         line editing tools, tool registration, new tool markers

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

/// <summary>
/// Tests for the 3 new SearchForPatternTool filter parameters.
/// </summary>
public class SearchFilterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITool _searchTool;

    public SearchFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_search_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        File.WriteAllText(Path.Combine(_tempDir, "app.cs"), "class App { void Run() { } }");
        File.WriteAllText(Path.Combine(_tempDir, "data.json"), "{ \"key\": \"Run\" }");
        File.WriteAllText(Path.Combine(_tempDir, "style.min.js"), "function Run(){}");
        File.WriteAllText(Path.Combine(_tempDir, "utils.py"), "def Run(): pass");

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        var registry = new ToolRegistry();
        registry.Register(new SearchForPatternTool(context));
        agent.SetToolRegistry(registry);
        agent.ActivateProjectAsync(_tempDir).Wait();

        _searchTool = registry.All.First(t => t.Name == "search_for_pattern");
    }

    [Fact]
    public async Task IncludeGlob_OnlyMatchingFiles()
    {
        var result = await _searchTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["substring_pattern"] = "Run",
            ["paths_include_glob"] = "*.cs",
        });

        Assert.Contains("app.cs", result);
        Assert.DoesNotContain("data.json", result);
        Assert.DoesNotContain("style.min.js", result);
    }

    [Fact]
    public async Task ExcludeGlob_ExcludesMatchingFiles()
    {
        var result = await _searchTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["substring_pattern"] = "Run",
            ["paths_exclude_glob"] = "*.min.js",
        });

        Assert.Contains("app.cs", result);
        Assert.DoesNotContain("style.min.js", result);
    }

    [Fact]
    public async Task RestrictToCodeFiles_ExcludesNonCode()
    {
        var result = await _searchTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["substring_pattern"] = "Run",
            ["restrict_search_to_code_files"] = true,
        });

        // .cs and .py are code files; .json and .min.js should still be code
        Assert.Contains("app.cs", result);
        Assert.DoesNotContain("data.json", result);
    }

    [Fact]
    public async Task DefaultBehavior_NoFiltersApplied()
    {
        var result = await _searchTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["substring_pattern"] = "Run",
        });

        // Should find in all files
        Assert.Contains("app.cs", result);
        Assert.Contains("data.json", result);
    }

    [Fact]
    public void SearchForPatternTool_Has8Parameters()
    {
        Assert.Equal(8, _searchTool.Parameters.Count);
        Assert.Contains(_searchTool.Parameters, p => p.Name == "paths_include_glob");
        Assert.Contains(_searchTool.Parameters, p => p.Name == "paths_exclude_glob");
        Assert.Contains(_searchTool.Parameters, p => p.Name == "restrict_search_to_code_files");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}

/// <summary>
/// Tests for ExecuteShellCommandTool output format and timeout_seconds parameter.
/// </summary>
public class ExecuteShellCommandOutputTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITool _shellTool;

    public ExecuteShellCommandOutputTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_shell_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        var registry = new ToolRegistry();
        registry.Register(new ExecuteShellCommandTool(context));
        agent.SetToolRegistry(registry);
        agent.ActivateProjectAsync(_tempDir).Wait();

        _shellTool = registry.All.First(t => t.Name == "execute_shell_command");
    }

    [Fact]
    public async Task Output_HasReturnCode_NotExitCode()
    {
        var result = await _shellTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hello",
        });

        Assert.Contains("return_code", result);
        Assert.DoesNotContain("exit_code", result);
    }

    [Fact]
    public async Task Output_HasCwdField()
    {
        var result = await _shellTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["command"] = "echo hello",
        });

        Assert.Contains("cwd", result);

        // Parse JSON and verify cwd value
        using var doc = JsonDocument.Parse(result);
        string cwd = doc.RootElement.GetProperty("cwd").GetString()!;
        Assert.Equal(_tempDir, cwd);
    }

    [Fact]
    public void HasTimeoutParameter()
    {
        Assert.Contains(_shellTool.Parameters, p => p.Name == "timeout_seconds");
        var param = _shellTool.Parameters.First(p => p.Name == "timeout_seconds");
        Assert.Equal(240, param.DefaultValue);
        Assert.False(param.Required);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}

/// <summary>
/// Tests for WriteMemoryTool max_chars parameter.
/// </summary>
public class WriteMemoryMaxCharsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public WriteMemoryMaxCharsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_wm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        _registry = new ToolRegistry();
        _registry.Register(new WriteMemoryTool(context));
        _registry.Register(new ReadMemoryTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    [Fact]
    public async Task MaxChars_TruncatesContent()
    {
        var write = _registry.All.First(t => t.Name == "write_memory");
        var read = _registry.All.First(t => t.Name == "read_memory");

        await write.ExecuteAsync(new Dictionary<string, object?>
        {
            ["memory_name"] = "truncated",
            ["content"] = "Hello, World! This is a long string.",
            ["max_chars"] = 5,
        });

        var result = await read.ExecuteAsync(new Dictionary<string, object?>
        {
            ["memory_name"] = "truncated",
        });

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task MaxChars_DefaultNoTruncation()
    {
        var write = _registry.All.First(t => t.Name == "write_memory");
        var read = _registry.All.First(t => t.Name == "read_memory");

        string longContent = new string('x', 10000);
        await write.ExecuteAsync(new Dictionary<string, object?>
        {
            ["memory_name"] = "no_truncate",
            ["content"] = longContent,
        });

        var result = await read.ExecuteAsync(new Dictionary<string, object?>
        {
            ["memory_name"] = "no_truncate",
        });

        Assert.Equal(10000, result.Length);
    }

    [Fact]
    public void WriteMemoryTool_Has3Parameters()
    {
        var tool = _registry.All.First(t => t.Name == "write_memory");
        Assert.Equal(3, tool.Parameters.Count);
        Assert.Contains(tool.Parameters, p => p.Name == "max_chars");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}

/// <summary>
/// Tests for line-level editing tools: delete_lines, insert_at_line, replace_lines.
/// </summary>
public class LineEditToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public LineEditToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_line_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        _registry = new ToolRegistry();
        _registry.Register(new DeleteLinesTool(context));
        _registry.Register(new InsertAtLineTool(context));
        _registry.Register(new ReplaceLinesTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    private string CreateTestFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return name;
    }

    // ---- DeleteLinesTool ----

    [Fact]
    public async Task DeleteLines_RemovesCorrectRange()
    {
        string file = CreateTestFile("del.txt", "line1\nline2\nline3\nline4\n");
        var tool = _registry.All.First(t => t.Name == "delete_lines");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 2,
            ["end_line"] = 3,
        });

        Assert.Contains("Deleted 2", result);
        string content = File.ReadAllText(Path.Combine(_tempDir, file));
        Assert.Contains("line1", content);
        Assert.Contains("line4", content);
        Assert.DoesNotContain("line2", content);
        Assert.DoesNotContain("line3", content);
    }

    [Fact]
    public async Task DeleteLines_InvalidStartLine_ReturnsError()
    {
        string file = CreateTestFile("del_err.txt", "line1\n");
        var tool = _registry.All.First(t => t.Name == "delete_lines");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 0,
            ["end_line"] = 1,
        });

        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task DeleteLines_EndBeforeStart_ReturnsError()
    {
        string file = CreateTestFile("del_rev.txt", "line1\nline2\n");
        var tool = _registry.All.First(t => t.Name == "delete_lines");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 3,
            ["end_line"] = 1,
        });

        Assert.Contains("Error", result);
    }

    // ---- InsertAtLineTool ----

    [Fact]
    public async Task InsertAtLine_InsertsAtBeginning()
    {
        string file = CreateTestFile("ins.txt", "line1\nline2\n");
        var tool = _registry.All.First(t => t.Name == "insert_at_line");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["line_number"] = 1,
            ["content"] = "inserted",
        });

        Assert.Contains("Inserted", result);
        string content = File.ReadAllText(Path.Combine(_tempDir, file));
        string[] lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("inserted", lines[0]);
        Assert.Equal("line1", lines[1]);
    }

    [Fact]
    public async Task InsertAtLine_InsertsAtEnd()
    {
        string file = CreateTestFile("ins_end.txt", "line1\nline2\n");
        var tool = _registry.All.First(t => t.Name == "insert_at_line");

        await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["line_number"] = 999,
            ["content"] = "appended",
        });

        string content = File.ReadAllText(Path.Combine(_tempDir, file));
        Assert.EndsWith("appended" + Environment.NewLine, content);
    }

    [Fact]
    public async Task InsertAtLine_InvalidLineNumber_ReturnsError()
    {
        string file = CreateTestFile("ins_err.txt", "line1\n");
        var tool = _registry.All.First(t => t.Name == "insert_at_line");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["line_number"] = 0,
            ["content"] = "x",
        });

        Assert.Contains("Error", result);
    }

    // ---- ReplaceLinesTool ----

    [Fact]
    public async Task ReplaceLines_ReplacesCorrectRange()
    {
        string file = CreateTestFile("repl.txt", "line1\nline2\nline3\nline4\n");
        var tool = _registry.All.First(t => t.Name == "replace_lines");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 2,
            ["end_line"] = 3,
            ["new_content"] = "replaced_a\nreplaced_b",
        });

        Assert.Contains("Replaced 2", result);
        string content = File.ReadAllText(Path.Combine(_tempDir, file));
        Assert.Contains("line1", content);
        Assert.Contains("replaced_a", content);
        Assert.Contains("replaced_b", content);
        Assert.Contains("line4", content);
        Assert.DoesNotContain("line2", content);
    }

    [Fact]
    public async Task ReplaceLines_InvalidRange_ReturnsError()
    {
        string file = CreateTestFile("repl_err.txt", "line1\n");
        var tool = _registry.All.First(t => t.Name == "replace_lines");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 3,
            ["end_line"] = 1,
            ["new_content"] = "x",
        });

        Assert.Contains("Error", result);
    }

    // ---- Tool name tests ----

    [Theory]
    [InlineData(typeof(DeleteLinesTool), "delete_lines")]
    [InlineData(typeof(InsertAtLineTool), "insert_at_line")]
    [InlineData(typeof(ReplaceLinesTool), "replace_lines")]
    public void LineEditTool_Names(Type toolType, string expectedName)
    {
        Assert.Equal(expectedName, ToolBase.DeriveNameFromType(toolType));
    }

    [Fact]
    public void AllLineEditTools_AreCanEdit()
    {
        var ctx = NullToolContext.Instance;
        Assert.True(new DeleteLinesTool(ctx).CanEdit);
        Assert.True(new InsertAtLineTool(ctx).CanEdit);
        Assert.True(new ReplaceLinesTool(ctx).CanEdit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}

/// <summary>
/// Tests for the 4 newly-registered tools' name derivation and markers.
/// </summary>
public class NewlyRegisteredToolTests
{
    [Theory]
    [InlineData(typeof(RemoveProjectTool), "remove_project")]
    [InlineData(typeof(RestartLanguageServerTool), "restart_language_server")]
    [InlineData(typeof(ListQueryableProjectsTool), "list_queryable_projects")]
    [InlineData(typeof(QueryProjectTool), "query_project")]
    public void ToolName_MatchesExpected(Type toolType, string expectedName)
    {
        Assert.Equal(expectedName, ToolBase.DeriveNameFromType(toolType));
    }

    [Fact]
    public void RemoveProjectTool_NoActiveProjectRequired()
    {
        var tool = new RemoveProjectTool(NullToolContext.Instance);
        Assert.False(tool.RequiresActiveProject);
    }

    [Fact]
    public void RestartLanguageServerTool_IsOptionalAndNoProjectRequired()
    {
        var tool = new RestartLanguageServerTool(NullToolContext.Instance);
        Assert.True(tool.IsOptional);
        Assert.False(tool.RequiresActiveProject);
    }

    [Fact]
    public void ListQueryableProjectsTool_IsOptionalAndNoProjectRequired()
    {
        var tool = new ListQueryableProjectsTool(NullToolContext.Instance);
        Assert.True(tool.IsOptional);
        Assert.False(tool.RequiresActiveProject);
    }

    [Fact]
    public void QueryProjectTool_IsOptionalAndHas3Params()
    {
        var tool = new QueryProjectTool(NullToolContext.Instance);
        Assert.True(tool.IsOptional);
        Assert.Equal(3, tool.Parameters.Count);
    }
}

/// <summary>
/// Tests that BuildToolRegistry now registers all 33 tools.
/// </summary>
public class ToolRegistrationCountTests
{
    [Fact]
    public void BuildToolRegistry_Returns33Tools()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);

        var registry = Serena.Mcp.Program.BuildToolRegistry(agent, loggerFactory);

        Assert.Equal(33, registry.All.Count);
    }

    [Fact]
    public void BuildToolRegistry_ContainsAllNewTools()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);

        var registry = Serena.Mcp.Program.BuildToolRegistry(agent, loggerFactory);
        var names = registry.Names;

        // Previously unregistered tools
        Assert.Contains("remove_project", names);
        Assert.Contains("restart_language_server", names);
        Assert.Contains("list_queryable_projects", names);
        Assert.Contains("query_project", names);

        // New line edit tools
        Assert.Contains("delete_lines", names);
        Assert.Contains("insert_at_line", names);
        Assert.Contains("replace_lines", names);
    }
}
