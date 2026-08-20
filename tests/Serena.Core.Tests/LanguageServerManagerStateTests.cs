// v1.0.26: State machine tests for LanguageServerManager's IServerReadyStateSink
// implementation. Verifies the lifecycle the symbol-tool layer relies on.

using FluentAssertions;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp;

namespace Serena.Core.Tests;

public class LanguageServerManagerStateTests
{
    private static IServerReadyStateSink NewSink()
    {
        // The sink half of the manager doesn't depend on a real LSP/registry,
        // so we can exercise it via the public IServerReadyStateSink contract.
        var registry = new Lsp.LanguageServers.LanguageServerRegistry();
        return new LanguageServerManager(
            Path.GetTempPath(),
            registry,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }

    [Fact]
    public void GetReadyState_NoSignals_ReturnsNotStarted()
    {
        var sink = NewSink();
        var snap = sink.GetReadyState(Language.CSharp);
        snap.State.Should().Be(WorkspaceReadyState.NotStarted);
    }

    [Fact]
    public void MarkLoading_ThenGetReadyState_ReportsLoadingWithProjectsTotal()
    {
        var sink = NewSink();
        sink.MarkLoading(Language.CSharp, projectsTotal: 185, scopeDescription: "1 solution(s)");
        var snap = sink.GetReadyState(Language.CSharp);
        snap.State.Should().Be(WorkspaceReadyState.Loading);
        snap.ProjectsTotal.Should().Be(185);
        snap.ScopeDescription.Should().Be("1 solution(s)");
    }

    [Fact]
    public void MarkReady_AfterLoading_TransitionsToReady()
    {
        var sink = NewSink();
        sink.MarkLoading(Language.CSharp, projectsTotal: 5);
        sink.MarkReady(Language.CSharp);
        sink.GetReadyState(Language.CSharp).State.Should().Be(WorkspaceReadyState.Ready);
    }

    [Fact]
    public void MarkFailed_TransitionsToFailed()
    {
        var sink = NewSink();
        sink.MarkLoading(Language.CSharp);
        sink.MarkFailed(Language.CSharp);
        sink.GetReadyState(Language.CSharp).State.Should().Be(WorkspaceReadyState.Failed);
    }

    [Fact]
    public void MarkPartial_PreservesProjectCountAndExplainsIncompleteResults()
    {
        var sink = NewSink();
        sink.MarkLoading(Language.CSharp, projectsTotal: 29, scopeDescription: "BigApp.sln");
        sink.MarkPartial(Language.CSharp, "one legacy project failed", projectsLoaded: 28);

        var snapshot = sink.GetReadyState(Language.CSharp);
        snapshot.State.Should().Be(WorkspaceReadyState.Partial);
        snapshot.ProjectsTotal.Should().Be(29);
        snapshot.ProjectsLoaded.Should().Be(28);
        snapshot.Warnings.Should().Contain("one legacy project failed");
    }

    [Fact]
    public void MarkFailed_ReportsActionableFailureReason()
    {
        var sink = NewSink();
        sink.MarkLoading(Language.CSharp);
        sink.MarkFailed(Language.CSharp, "select a solution first");

        var snapshot = sink.GetReadyState(Language.CSharp);
        snapshot.State.Should().Be(WorkspaceReadyState.Failed);
        snapshot.Warnings.Should().Contain("select a solution first");
    }

    [Fact]
    public void OtherLanguage_StaysNotStarted()
    {
        var sink = NewSink();
        sink.MarkReady(Language.CSharp);
        sink.GetReadyState(Language.Python).State.Should().Be(WorkspaceReadyState.NotStarted);
    }

    [Fact]
    public void PartialReferenceWarnings_AreSampledAndTruncated()
    {
        string longWarning = new('x', 500);

        var samples = FindReferencingSymbolsTool.SummarizeWorkspaceWarnings(
            ["first", longWarning, "third", "fourth"]);

        samples.Should().HaveCount(3);
        samples[0].Should().Be("first");
        samples[1].Should().HaveLength(241).And.EndWith("…");
        samples[2].Should().Be("third");
    }
}
