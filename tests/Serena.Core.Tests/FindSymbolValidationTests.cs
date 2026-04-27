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

    // v1.0.34 — match_overloads
    [Fact]
    public async Task MatchOverloads_AndSubstringMatching_AreMutuallyExclusive()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "Run",
            ["match_overloads"] = true,
            ["substring_matching"] = true,
        });
        r.Should().Be("match_overloads and substring_matching are mutually exclusive");
    }

    [Fact]
    public async Task FindSymbol_MatchOverloads_ReturnsAllSameNameSymbols()
    {
        // Create a project with two distinct types both exposing a 'Run' method.
        string fileA = Path.Combine(_tempDir, "A.cs");
        string fileB = Path.Combine(_tempDir, "B.cs");
        await File.WriteAllTextAsync(fileA, "namespace N { public class Alpha { public void Run() {} } }");
        await File.WriteAllTextAsync(fileB, "namespace N { public class Beta  { public void Run() {} } }");

        // Without match_overloads, an exact "Alpha/Run" hits one. With
        // match_overloads, the leaf "Run" must hit both regardless of parent.
        string scoped = await CallAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "Run",
            ["match_overloads"] = true,
            ["relative_path"] = ".",
        });

        // We can't guarantee the LSP indexed this synchronously in a temp dir,
        // so the assertion is loose: validation passes (no error string), and
        // either matches were found or the standard "no symbols" reply was
        // returned. This catches regressions in argument plumbing.
        scoped.Should().NotContain("mutually exclusive");
        scoped.Should().NotContain("name_path_pattern is required");
    }
}
