// v1.0.28: FileWriteGate serializes concurrent writes to the same path.

using System.Text;
using FluentAssertions;
using Serena.Core.Editor;

namespace Serena.Core.Tests;

public class FileWriteGateTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string NewTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"serena-fwg-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string p in _tempFiles)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task FiftyParallelWritesToSamePath_AllSucceed_FinalContentIsOneOfThem()
    {
        string path = NewTempPath();
        const int n = 50;
        string[] contents = Enumerable.Range(0, n).Select(i => $"content-{i}").ToArray();

        var tasks = contents.Select(c =>
            FileWriteGate.WriteAllTextAsync(path, c, Encoding.UTF8, CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks); // must not throw

        string final = await File.ReadAllTextAsync(path);
        contents.Should().Contain(final);
    }

    [Fact]
    public async Task ConcurrentWritesToDifferentPaths_DoNotContend()
    {
        string a = NewTempPath();
        string b = NewTempPath();

        // If the gate keyed globally we'd serialize these; assert by writing
        // both N times in parallel and confirming both end up with valid
        // content. The functional assertion is that no IOException is thrown
        // and contents match (exact value not racy because each path is its
        // own gate so the LAST write to that path wins deterministically...
        // except the order is not deterministic, so we just assert one of the
        // candidates).
        var tasksA = Enumerable.Range(0, 20).Select(i =>
            FileWriteGate.WriteAllTextAsync(a, $"a-{i}", Encoding.UTF8, CancellationToken.None));
        var tasksB = Enumerable.Range(0, 20).Select(i =>
            FileWriteGate.WriteAllTextAsync(b, $"b-{i}", Encoding.UTF8, CancellationToken.None));

        await Task.WhenAll(tasksA.Concat(tasksB));

        (await File.ReadAllTextAsync(a)).Should().StartWith("a-");
        (await File.ReadAllTextAsync(b)).Should().StartWith("b-");
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceledException()
    {
        string path = NewTempPath();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await FileWriteGate.WriteAllTextAsync(
            path, "x", Encoding.UTF8, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
