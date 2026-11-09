// apply_edits: batching, per-file atomicity, echoed context, argument shapes.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public sealed class ApplyEditsToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITool _applyEdits;
    private readonly ITool _replaceContent;

    public ApplyEditsToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_apply_edits_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, new LanguageServerRegistry(), loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        var registry = new ToolRegistry();
        registry.Register(new ApplyEditsTool(context));
        registry.Register(new ReplaceContentTool(context));
        agent.SetToolRegistry(registry);
        agent.ActivateProjectAsync(_tempDir).Wait();

        _applyEdits = registry.Get("apply_edits")!;
        _replaceContent = registry.Get("replace_content")!;
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return name;
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(_tempDir, name));

    private static Dictionary<string, object?> Edit(string path, string needle, string repl, string mode = "literal", bool multiple = false) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["relative_path"] = path,
            ["needle"] = needle,
            ["repl"] = repl,
            ["mode"] = mode,
            ["allow_multiple_occurrences"] = multiple,
        };

    [Fact]
    public async Task EditsToOneFile_ApplyInOrder_OnTheRunningText()
    {
        Write("A.cs", "class A { int Status; }\n");

        string result = await _applyEdits.ExecuteAsync(new Dictionary<string, object?>
        {
            ["edits"] = new List<object?>
            {
                Edit("A.cs", "int Status", "int StatusCode"),
                Edit("A.cs", "StatusCode;", "StatusCode = 200;"),
            },
            ["report_diagnostics"] = false,
        });

        Read("A.cs").Should().Be("class A { int StatusCode = 200; }\n");
        result.Should().StartWith("Applied 2 of 2 edit(s); 1 of 1 file(s) written.");
        result.Should().Contain("[1] replaced 1 occurrence(s), first at line 1");
        result.Should().Contain(">     1 | class A { int StatusCode = 200; }");
    }

    [Fact]
    public async Task FailedEdit_LeavesItsFileUntouched_AndOtherFilesProceed()
    {
        Write("A.cs", "alpha\n");
        Write("B.cs", "beta\n");

        string result = await _applyEdits.ExecuteAsync(new Dictionary<string, object?>
        {
            ["edits"] = new List<object?>
            {
                Edit("A.cs", "alpha", "ALPHA"),
                Edit("A.cs", "missing", "x"),
                Edit("B.cs", "beta", "BETA"),
            },
            ["report_diagnostics"] = false,
        });

        Read("A.cs").Should().Be("alpha\n", "a failed edit must not write a partially edited file");
        Read("B.cs").Should().Be("BETA\n");
        result.Should().StartWith("Applied 1 of 3 edit(s); 1 of 2 file(s) written.");
        result.Should().Contain("== A.cs: NOT written (edit [2] failed: Text not found in A.cs)");
        result.Should().Contain("== B.cs: 1 edit(s) applied");
    }

    [Fact]
    public async Task ContextLines_SurroundTheChange_WithOneBasedNumbers()
    {
        Write("C.cs", "line1\nline2\nline3\nline4\nline5\n");

        string result = await _applyEdits.ExecuteAsync(new Dictionary<string, object?>
        {
            ["edits"] = new List<object?> { Edit("C.cs", "line3", "LINE3") },
            ["context_lines"] = 1,
            ["report_diagnostics"] = false,
        });

        result.Should().Contain("      2 | line2");
        result.Should().Contain(">     3 | LINE3");
        result.Should().Contain("      4 | line4");
        result.Should().NotContain("| line1");
        result.Should().NotContain("| line5");
    }

    [Fact]
    public async Task RegexEdit_WithBackreference_AndMultipleOccurrences()
    {
        Write("D.cs", "foo(1); foo(2);\n");

        string result = await _applyEdits.ExecuteAsync(new Dictionary<string, object?>
        {
            ["edits"] = new List<object?> { Edit("D.cs", @"foo\((\d)\)", "bar($1)", mode: "regex", multiple: true) },
            ["report_diagnostics"] = false,
        });

        Read("D.cs").Should().Be("bar(1); bar(2);\n");
        result.Should().Contain("replaced 2 occurrence(s)");
    }

    [Fact]
    public async Task Edits_AcceptedAsJsonString_AndAsRawJsonObjectElements()
    {
        Write("E.cs", "one two\n");

        string viaString = await _applyEdits.ExecuteAsync(new Dictionary<string, object?>
        {
            ["edits"] = """[{"relative_path":"E.cs","needle":"one","repl":"ONE"}]""",
            ["report_diagnostics"] = false,
        });
        viaString.Should().StartWith("Applied 1 of 1");

        // The MCP bridge unwraps array elements that are objects to their raw JSON text.
        string viaElements = await _applyEdits.ExecuteAsync(new Dictionary<string, object?>
        {
            ["edits"] = new List<object?> { """{"relative_path":"E.cs","needle":"two","repl":"TWO"}""" },
            ["report_diagnostics"] = false,
        });
        viaElements.Should().StartWith("Applied 1 of 1");

        Read("E.cs").Should().Be("ONE TWO\n");
    }

    [Fact]
    public void ParseEdits_RejectsMissingFields_WithTheEditNumber()
    {
        var act = () => ApplyEditsTool.ParseEdits(new List<object?> { Edit("F.cs", "x", "y"), new Dictionary<string, object?> { ["relative_path"] = "F.cs" } });
        act.Should().Throw<ArgumentException>().WithMessage("Edit [2] is missing 'needle'.");
    }

    [Fact]
    public async Task ReplaceContent_DefaultOutputUnchanged_ContextOnRequest()
    {
        Write("G.cs", "a\nb\nc\n");

        string plain = await _replaceContent.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "G.cs",
            ["needle"] = "b",
            ["repl"] = "B",
            ["mode"] = "literal",
        });
        plain.Should().Be("Replaced 1 occurrence(s) in G.cs");

        string withContext = await _replaceContent.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "G.cs",
            ["needle"] = "c",
            ["repl"] = "C",
            ["mode"] = "literal",
            ["context_lines"] = 1,
        });
        withContext.Should().StartWith("Replaced 1 occurrence(s) in G.cs\n");
        withContext.Should().Contain("      2 | B");
        withContext.Should().Contain(">     3 | C");
    }

    [Fact]
    public void FormatContext_MarksEveryChangedLine_OfAMultiLineReplacement()
    {
        string content = "x\nfirst\nsecond\ny\n";
        int index = content.IndexOf("first", StringComparison.Ordinal);
        string context = ContentReplacement.FormatContext(content, index, "first\nsecond".Length, 0);
        context.Should().Be(">     2 | first\n>     3 | second");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
