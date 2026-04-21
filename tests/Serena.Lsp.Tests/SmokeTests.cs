namespace Serena.Lsp.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.NotNull(typeof(Serena.Lsp.AssemblyMarker));
        Assert.NotNull(typeof(Serena.Lsp.Protocol.AssemblyMarker));
    }
}
