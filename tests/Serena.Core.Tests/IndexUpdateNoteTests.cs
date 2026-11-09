// The one line apply_edits adds when a written file's outline has to wait for the server.

using FluentAssertions;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp;

namespace Serena.Core.Tests;

public class IndexUpdateNoteTests
{
    [Fact]
    public void Silent_WhenAUsableServerIsRebuildingEveryOutline()
    {
        var updates = new[]
        {
            new SymbolCacheUpdate(OutlineRefresh.Started, OccurrencesRefreshed: true, WorkspaceReadyState.Ready),
            new SymbolCacheUpdate(OutlineRefresh.Started, OccurrencesRefreshed: true, WorkspaceReadyState.Partial),
        };

        ApplyEditsTool.DescribeIndexUpdates(updates).Should().BeNull();
    }

    [Fact]
    public void NamesTheServerState_AndCountsTheDeferredFiles()
    {
        var updates = new[]
        {
            new SymbolCacheUpdate(OutlineRefresh.Deferred, OccurrencesRefreshed: true, WorkspaceReadyState.Loading),
            new SymbolCacheUpdate(OutlineRefresh.Deferred, OccurrencesRefreshed: true, WorkspaceReadyState.Loading),
            new SymbolCacheUpdate(OutlineRefresh.Started, OccurrencesRefreshed: true, WorkspaceReadyState.Loading),
        };

        string? note = ApplyEditsTool.DescribeIndexUpdates(updates);

        note.Should().NotBeNull();
        note.Should().Contain("all 3 written file(s)");
        note.Should().Contain("the outline of 2 waits");
        note.Should().Contain("now Loading");
        note.Should().Contain("find_occurrences works now");
    }

    [Fact]
    public void Silent_WhenNothingWasIndexed()
    {
        ApplyEditsTool.DescribeIndexUpdates([]).Should().BeNull();
    }
}
