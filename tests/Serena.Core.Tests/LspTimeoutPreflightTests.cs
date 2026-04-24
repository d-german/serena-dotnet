// v1.0.27: Pre-flight readiness fast-fail in LspTimeout.WithTimeoutAsync.
// Locks in the fast-fail contract: when the workspace isn't Ready, the op
// delegate must NEVER be invoked and a LanguageServerWarmingException must
// be thrown immediately.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Editor;
using Serena.Lsp;

namespace Serena.Core.Tests;

public class LspTimeoutPreflightTests
{
    private static ReadyStateSnapshot Snapshot(WorkspaceReadyState state) =>
        new(state, ProjectsLoaded: 12, ProjectsTotal: 185, ElapsedSeconds: 30.0);

    [Theory]
    [InlineData(WorkspaceReadyState.NotStarted)]
    [InlineData(WorkspaceReadyState.Loading)]
    [InlineData(WorkspaceReadyState.Failed)]
    public async Task NonReadyState_ThrowsWarmingException_WithoutInvokingOp(WorkspaceReadyState state)
    {
        bool opInvoked = false;
        Func<Task> act = async () => await LspTimeout.WithTimeoutAsync<int>(
            _ => { opInvoked = true; return Task.FromResult(0); },
            () => Snapshot(state),
            Language.CSharp,
            NullLogger.Instance,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<LanguageServerWarmingException>();
        ex.Which.Snapshot.State.Should().Be(state);
        opInvoked.Should().BeFalse("pre-flight must short-circuit before the op runs");
    }

    [Fact]
    public async Task ReadyState_InvokesOp()
    {
        bool opInvoked = false;
        int result = await LspTimeout.WithTimeoutAsync<int>(
            _ => { opInvoked = true; return Task.FromResult(42); },
            () => Snapshot(WorkspaceReadyState.Ready),
            Language.CSharp,
            NullLogger.Instance,
            CancellationToken.None);

        opInvoked.Should().BeTrue();
        result.Should().Be(42);
    }

    [Fact]
    public async Task NotStartedState_AdviceMentionsWarmTool()
    {
        var ex = await Assert.ThrowsAsync<LanguageServerWarmingException>(async () =>
            await LspTimeout.WithTimeoutAsync<int>(
                _ => Task.FromResult(0),
                () => Snapshot(WorkspaceReadyState.NotStarted),
                Language.CSharp,
                NullLogger.Instance,
                CancellationToken.None));

        ex.Advice.Should().Contain("warm_language_server");
    }
}
