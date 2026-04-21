namespace Serena.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.NotNull(typeof(Serena.Core.AssemblyMarker));
    }
}
