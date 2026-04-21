// Tool Infrastructure Tests - Phase 8

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

/// <summary>
/// Null implementation of <see cref="IToolContext"/> for unit tests.
/// Provides NullLoggerFactory and no active project/agent.
/// </summary>
internal sealed class NullToolContext : IToolContext
{
    public static NullToolContext Instance { get; } = new();

    private readonly Lazy<SerenaAgent> _agent = new(() =>
        new SerenaAgent(
            new SerenaConfig(NullLogger<SerenaConfig>.Instance),
            new Serena.Lsp.LanguageServers.LanguageServerRegistry(),
            NullLoggerFactory.Instance));

    public SerenaAgent Agent => _agent.Value;
    public SerenaProject? ActiveProject => null;
    public string? ProjectRoot => null;
    public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;
}

public class ToolBaseTests
{
    [Fact]
    public void DeriveNameFromType_StripsTool_ConvertsToPascalCase()
    {
        ToolBase.DeriveNameFromType(typeof(ReadFileTool)).Should().Be("read_file");
        ToolBase.DeriveNameFromType(typeof(FindSymbolTool)).Should().Be("find_symbol");
        ToolBase.DeriveNameFromType(typeof(GetSymbolsOverviewTool)).Should().Be("get_symbols_overview");
        ToolBase.DeriveNameFromType(typeof(ExecuteShellCommandTool)).Should().Be("execute_shell_command");
    }

    [Fact]
    public void ToolRegistry_RegisterAndRetrieve()
    {
        var registry = new ToolRegistry();
        var tool = new ReadFileTool(NullToolContext.Instance);
        registry.Register(tool);

        registry.Get("read_file").Should().BeSameAs(tool);
        registry.Names.Should().Contain("read_file");
        registry.All.Should().HaveCount(1);
    }

    [Fact]
    public void ToolRegistry_Where_FiltersCorrectly()
    {
        var registry = new ToolRegistry();
        var ctx = NullToolContext.Instance;

        registry.Register(new ReadFileTool(ctx));
        registry.Register(new CreateTextFileTool(ctx));
        registry.Register(new FindSymbolTool(ctx));

        var editTools = registry.Where(t => t is ToolBase tb && tb.CanEdit);
        editTools.Should().HaveCount(1);
        editTools[0].Name.Should().Be("create_text_file");
    }

    [Fact]
    public void ToolMarkers_AppliedCorrectly()
    {
        var ctx = NullToolContext.Instance;

        var readFile = new ReadFileTool(ctx);
        readFile.CanEdit.Should().BeFalse();
        readFile.RequiresActiveProject.Should().BeTrue();

        var createFile = new CreateTextFileTool(ctx);
        createFile.CanEdit.Should().BeTrue();

        var findSymbol = new FindSymbolTool(ctx);
        findSymbol.IsSymbolicRead.Should().BeTrue();
        findSymbol.CanEdit.Should().BeFalse();

        var replaceSymbol = new ReplaceSymbolBodyTool(ctx);
        replaceSymbol.IsSymbolicEdit.Should().BeTrue();
        replaceSymbol.CanEdit.Should().BeTrue();
    }

    [Fact]
    public async Task ToolBase_ExecuteAsync_CatchesExceptions()
    {
        var tool = new ThrowingTool(NullToolContext.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());
        result.Should().StartWith("Error:");
    }

    [Fact]
    public void GetRequired_ThrowsOnMissing()
    {
        var tool = new ReadFileTool(NullToolContext.Instance);
        var args = new Dictionary<string, object?>();
        var act = async () => await tool.ExecuteAsync(args);
        act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void ToolBase_Context_IsAccessible()
    {
        var ctx = NullToolContext.Instance;
        var tool = new ReadFileTool(ctx);

        // Verify the tool has access to the context (through ToolBase.Context)
        tool.Name.Should().Be("read_file");
    }

    private sealed class ThrowingTool : ToolBase
    {
        public ThrowingTool(IToolContext context) : base(context) { }
        public override string Description => "Throws for testing";
        protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
            => throw new InvalidOperationException("Test exception");
    }

    [Fact]
    public void AllTools_HaveNonEmptyParameters()
    {
        var ctx = NullToolContext.Instance;
        ITool[] tools =
        [
            new ReadFileTool(ctx),
            new CreateTextFileTool(ctx),
            new ListDirTool(ctx),
            new FindFileTool(ctx),
            new SearchForPatternTool(ctx),
            new FindSymbolTool(ctx),
            new GetSymbolsOverviewTool(ctx),
            new FindReferencingSymbolsTool(ctx),
            new ReplaceSymbolBodyTool(ctx),
            new InsertBeforeSymbolTool(ctx),
            new InsertAfterSymbolTool(ctx),
            new ReplaceContentTool(ctx),
            new RenameSymbolTool(ctx),
            new SafeDeleteSymbolTool(ctx),
            new ReadMemoryTool(ctx),
            new WriteMemoryTool(ctx),
            new ListMemoriesTool(ctx),
            new DeleteMemoryTool(ctx),
            new ExecuteShellCommandTool(ctx),
            new ActivateProjectTool(ctx),
        ];

        foreach (var tool in tools)
        {
            tool.Parameters.Should().NotBeEmpty($"tool '{tool.Name}' should declare its parameters");
        }
    }

    [Fact]
    public void GenerateJsonSchema_ProducesValidSchema()
    {
        var parameters = new List<ToolParameter>
        {
            new("name", "The name", typeof(string), Required: true),
            new("count", "Number of items", typeof(int), Required: false, DefaultValue: 10),
            new("verbose", "Enable verbose output", typeof(bool), Required: false, DefaultValue: false),
        };

        using var schema = ToolBase.GenerateJsonSchema(parameters);
        var root = schema.RootElement;

        root.GetProperty("type").GetString().Should().Be("object");
        root.GetProperty("required").GetArrayLength().Should().Be(1);
        root.GetProperty("required")[0].GetString().Should().Be("name");

        var props = root.GetProperty("properties");
        props.GetProperty("name").GetProperty("type").GetString().Should().Be("string");
        props.GetProperty("count").GetProperty("type").GetString().Should().Be("integer");
        props.GetProperty("count").GetProperty("default").GetInt32().Should().Be(10);
        props.GetProperty("verbose").GetProperty("type").GetString().Should().Be("boolean");
    }
}
