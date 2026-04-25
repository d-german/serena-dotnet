// v1.0.31: find_symbol input validation (T5/T6/T7).

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class FindSymbolValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public FindSymbolValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_findsym_v_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new FindSymbolTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Task<string> CallAsync(IDictionary<string, object?> args)
    {
        var tool = _registry.All.First(t => t.Name == "find_symbol");
        return tool.ExecuteAsync(new Dictionary<string, object?>(args), CancellationToken.None);
    }

    // T5
    [Fact]
    public async Task EmptyPattern_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?> { ["name_path_pattern"] = "" });
        r.Should().Be("name_path_pattern is required");
    }

    [Fact]
    public async Task WhitespacePattern_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?> { ["name_path_pattern"] = "   " });
        r.Should().Be("name_path_pattern is required");
    }

    [Fact]
    public async Task Wildcard_WithoutSubstringMatching_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?> { ["name_path_pattern"] = "Foo*" });
        r.Should().Be("wildcards not supported in name_path_pattern; pass substring_matching: true for partial matches");
    }

    [Fact]
    public async Task QuestionMark_WithoutSubstringMatching_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?> { ["name_path_pattern"] = "Foo?" });
        r.Should().Be("wildcards not supported in name_path_pattern; pass substring_matching: true for partial matches");
    }

    [Fact]
    public async Task Wildcard_WithSubstringMatching_PassesValidation()
    {
        // Should not return either validation error; falls through to LSP/cache lookup.
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "Foo*",
            ["substring_matching"] = true,
        });
        r.Should().NotContain("wildcards not supported");
    }

    // T6
    [Fact]
    public async Task IncludeKinds_OutOfRange_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["include_kinds"] = new List<int> { 999 },
        });
        r.Should().Be("include_kinds contains invalid LSP SymbolKind value: 999 (valid: 1-26)");
    }

    [Fact]
    public async Task IncludeKinds_NegativeOne_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["include_kinds"] = new List<int> { -1 },
        });
        r.Should().Be("include_kinds contains invalid LSP SymbolKind value: -1 (valid: 1-26)");
    }

    [Fact]
    public async Task IncludeKinds_Zero_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["include_kinds"] = new List<int> { 0 },
        });
        r.Should().Be("include_kinds contains invalid LSP SymbolKind value: 0 (valid: 1-26)");
    }

    [Fact]
    public async Task ExcludeKinds_TwentySeven_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["exclude_kinds"] = new List<int> { 27 },
        });
        r.Should().Be("exclude_kinds contains invalid LSP SymbolKind value: 27 (valid: 1-26)");
    }

    [Fact]
    public async Task IncludeKinds_Five_PassesValidation()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["include_kinds"] = new List<int> { 5 },
        });
        r.Should().NotContain("invalid LSP SymbolKind");
    }

    // T7
    [Fact]
    public async Task MaxMatches_LargeNegative_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["max_matches"] = -999,
        });
        r.Should().Be("max_matches must be -1 (unlimited) or a positive integer");
    }

    [Fact]
    public async Task MaxMatches_Zero_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["max_matches"] = 0,
        });
        r.Should().Be("max_matches must be -1 (unlimited) or a positive integer");
    }

    [Fact]
    public async Task MaxMatches_NegativeOne_PassesValidation()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["max_matches"] = -1,
        });
        r.Should().NotContain("max_matches must be");
    }

    [Fact]
    public async Task MaxMatches_Ten_PassesValidation()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "X",
            ["max_matches"] = 10,
        });
        r.Should().NotContain("max_matches must be");
    }
}
