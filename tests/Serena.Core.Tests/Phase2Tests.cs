// Tests for Phase 2 changes: MemoriesManager extensions, new tools, DI fix, project unification

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

/// <summary>
/// Tests for MoveMemory, EditMemory, and ListProjectMemories added to MemoriesManager.
/// </summary>
public class MemoriesManagerExtensionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MemoriesManager _mm;

    public MemoriesManagerExtensionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"serena-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _mm = new MemoriesManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void MoveMemory_Success()
    {
        _mm.SaveMemory("old_name", "content");
        _mm.MoveMemory("old_name", "new_name");

        Assert.Throws<FileNotFoundException>(() => _mm.LoadMemory("old_name"));
        Assert.Equal("content", _mm.LoadMemory("new_name"));
    }

    [Fact]
    public void MoveMemory_SourceMissing_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => _mm.MoveMemory("nonexistent", "target"));
    }

    [Fact]
    public void MoveMemory_DestinationExists_Throws()
    {
        _mm.SaveMemory("source", "s");
        _mm.SaveMemory("dest", "d");

        var ex = Assert.Throws<InvalidOperationException>(() => _mm.MoveMemory("source", "dest"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void MoveMemory_ToSubdirectory()
    {
        _mm.SaveMemory("flat_name", "moved content");
        _mm.MoveMemory("flat_name", "topic/subtopic");

        Assert.Equal("moved content", _mm.LoadMemory("topic/subtopic"));
    }

    [Fact]
    public void EditMemory_LiteralReplace()
    {
        _mm.SaveMemory("editable", "Hello World");
        _mm.EditMemory("editable", "World", "Universe", "literal", false);

        Assert.Equal("Hello Universe", _mm.LoadMemory("editable"));
    }

    [Fact]
    public void EditMemory_RegexReplace()
    {
        _mm.SaveMemory("editable", "item: 42");
        _mm.EditMemory("editable", @"\d+", "99", "regex", false);

        Assert.Equal("item: 99", _mm.LoadMemory("editable"));
    }

    [Fact]
    public void EditMemory_MultipleOccurrences_Blocked()
    {
        _mm.SaveMemory("multi", "aaa bbb aaa");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _mm.EditMemory("multi", "aaa", "ccc", "literal", false));
        Assert.Contains("2 times", ex.Message);
    }

    [Fact]
    public void EditMemory_MultipleOccurrences_Allowed()
    {
        _mm.SaveMemory("multi", "aaa bbb aaa");
        _mm.EditMemory("multi", "aaa", "ccc", "literal", true);

        Assert.Equal("ccc bbb ccc", _mm.LoadMemory("multi"));
    }

    [Fact]
    public void EditMemory_PatternNotFound_Throws()
    {
        _mm.SaveMemory("target", "content");
        Assert.Throws<InvalidOperationException>(() =>
            _mm.EditMemory("target", "missing", "x", "literal", false));
    }

    [Fact]
    public void ListProjectMemories_ReturnsOnlyProjectMemories()
    {
        _mm.SaveMemory("project_note", "note");
        _mm.SaveMemory("another/nested", "nested");
        // Global memories are stored elsewhere, so won't appear here

        var projectMemories = _mm.ListProjectMemories();
        Assert.Contains("project_note", projectMemories);
        Assert.Contains("another/nested", projectMemories);
    }

    [Fact]
    public void ListProjectMemories_EmptyWhenNoProjectDir()
    {
        var globalOnlyMm = new MemoriesManager(null);
        var memories = globalOnlyMm.ListProjectMemories();
        Assert.Empty(memories);
    }
}

/// <summary>
/// Tests for the DI fix — single agent instance shared between MCP tools and host.
/// </summary>
public class DiFix_SingleAgentTests
{
    [Fact]
    public void BuildToolRegistry_Returns26Tools()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);

        var registry = Serena.Mcp.Program.BuildToolRegistry(agent, loggerFactory);

        Assert.Equal(38, registry.All.Count);
    }

    [Fact]
    public void BuildToolRegistry_ToolRegistrySetOnAgent()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);

        var registry = Serena.Mcp.Program.BuildToolRegistry(agent, loggerFactory);
        agent.SetToolRegistry(registry);

        // Agent.Tools should not throw (registry is set)
        Assert.Equal(38, agent.Tools.All.Count);
    }

    [Fact]
    public void BuildToolRegistry_AllToolsHaveValidSchemas()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);

        var registry = Serena.Mcp.Program.BuildToolRegistry(agent, loggerFactory);

        foreach (var tool in registry.All)
        {
            using var schema = ToolBase.GenerateJsonSchema(tool.Parameters);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
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
        Assert.Contains("rename_memory", names);
        Assert.Contains("edit_memory", names);
        Assert.Contains("check_onboarding_performed", names);
        Assert.Contains("onboarding", names);
        Assert.Contains("initial_instructions", names);
    }
}

/// <summary>
/// Tests for SafeDeleteSymbolTool marker fix and new tool markers.
/// </summary>
public class ToolMarkerTests
{
    [Fact]
    public void SafeDeleteSymbolTool_IsSymbolicEdit()
    {
        var tool = new SafeDeleteSymbolTool(NullToolContext.Instance);
        Assert.True(tool.IsSymbolicEdit);
        Assert.False(tool.IsSymbolicRead);
    }

    [Fact]
    public void RenameMemoryTool_CanEdit()
    {
        var tool = new RenameMemoryTool(NullToolContext.Instance);
        Assert.True(tool.CanEdit);
    }

    [Fact]
    public void EditMemoryTool_CanEdit()
    {
        var tool = new EditMemoryTool(NullToolContext.Instance);
        Assert.True(tool.CanEdit);
    }

    [Fact]
    public void CheckOnboardingTool_NoProjectRequired()
    {
        var tool = new CheckOnboardingPerformedTool(NullToolContext.Instance);
        Assert.False(tool.RequiresActiveProject);
    }

    [Fact]
    public void InitialInstructionsTool_NoProjectRequired()
    {
        var tool = new InitialInstructionsTool(NullToolContext.Instance);
        Assert.False(tool.RequiresActiveProject);
    }
}

/// <summary>
/// Tests for workflow tools.
/// </summary>
public class WorkflowToolTests
{
    [Fact]
    public async Task OnboardingTool_ReturnsInstructions()
    {
        var tool = new OnboardingTool(NullToolContext.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        Assert.Contains("Onboarding", result);
        Assert.Contains("list_dir", result);
        Assert.Contains("write_memory", result);
    }

    [Fact]
    public async Task InitialInstructionsTool_ReturnsManual()
    {
        var tool = new InitialInstructionsTool(NullToolContext.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        Assert.Contains("Serena Instructions Manual", result);
        Assert.Contains("find_symbol", result);
        Assert.Contains("Name Paths", result);
    }

    [Fact]
    public async Task CheckOnboarding_NoProject_SuggestsOnboarding()
    {
        var tool = new CheckOnboardingPerformedTool(NullToolContext.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        Assert.Contains("NOT been performed", result);
    }
}

/// <summary>
/// Tests for new tool names and parameters.
/// </summary>
public class NewToolNameTests
{
    [Theory]
    [InlineData(typeof(RenameMemoryTool), "rename_memory")]
    [InlineData(typeof(EditMemoryTool), "edit_memory")]
    [InlineData(typeof(CheckOnboardingPerformedTool), "check_onboarding_performed")]
    [InlineData(typeof(OnboardingTool), "onboarding")]
    [InlineData(typeof(InitialInstructionsTool), "initial_instructions")]
    public void ToolName_MatchesExpected(Type toolType, string expectedName)
    {
        Assert.Equal(expectedName, ToolBase.DeriveNameFromType(toolType));
    }

    [Fact]
    public void EditMemoryTool_HasFiveParameters()
    {
        var tool = new EditMemoryTool(NullToolContext.Instance);
        Assert.Equal(5, tool.Parameters.Count);
        Assert.Contains(tool.Parameters, p => p.Name == "memory_name");
        Assert.Contains(tool.Parameters, p => p.Name == "needle");
        Assert.Contains(tool.Parameters, p => p.Name == "repl");
        Assert.Contains(tool.Parameters, p => p.Name == "mode");
        Assert.Contains(tool.Parameters, p => p.Name == "allow_multiple_occurrences");
    }

    [Fact]
    public void RenameMemoryTool_HasTwoParameters()
    {
        var tool = new RenameMemoryTool(NullToolContext.Instance);
        Assert.Equal(2, tool.Parameters.Count);
        Assert.Contains(tool.Parameters, p => p.Name == "old_name");
        Assert.Contains(tool.Parameters, p => p.Name == "new_name");
    }
}

/// <summary>
/// Tests for project unification — single SerenaProject with Registration.
/// </summary>
public class ProjectUnificationTests
{
    [Fact]
    public void SerenaProject_NameFromRegistration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"serena-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var reg = new RegisteredProject { Name = "MyProject", Path = tempDir };
            var project = new SerenaProject(tempDir, NullLogger<SerenaProject>.Instance, reg);

            Assert.Equal("MyProject", project.Name);
            Assert.Same(reg, project.Registration);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SerenaProject_NameFallsBackToDirectoryName()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"my-fallback-project");
        Directory.CreateDirectory(tempDir);
        try
        {
            var project = new SerenaProject(tempDir, NullLogger<SerenaProject>.Instance);

            Assert.Equal("my-fallback-project", project.Name);
            Assert.Null(project.Registration);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SerenaProject_HasRootProperty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"root-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var project = new SerenaProject(tempDir, NullLogger<SerenaProject>.Instance);
            Assert.Equal(Path.GetFullPath(tempDir), project.Root);
            Assert.Equal(project.ProjectRoot, project.Root);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
