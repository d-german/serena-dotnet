using Serena.Lsp.Client;

namespace Serena.Lsp.Tests;

public class UriPathConversionTests
{
    [Theory]
    [InlineData("file:///C:/work/project/src/file.cs", @"C:\work\project\src\file.cs")]
    [InlineData("file:///c%3A/work/project/src/file.ts", @"c:\work\project\src\file.ts")]
    [InlineData("file:///c%3a/work/project/src/file.ts", @"c:\work\project\src\file.ts")]
    [InlineData("file:///C:/path%20with%20spaces/file.cs", @"C:\path with spaces\file.cs")]
    [InlineData("file:///C:/work/project/file%231.cs", @"C:\work\project\file#1.cs")]
    public void UriToPath_HandlesPercentEncoding(string uri, string expectedPath)
    {
        string result = LspClient.UriToPath(uri);
        Assert.Equal(expectedPath, result);
    }

    [Theory]
    [InlineData(@"C:\work\project\src\file.cs", "file:///C:/work/project/src/file.cs")]
    [InlineData(@"C:\path with spaces\file.cs", "file:///C:/path%20with%20spaces/file.cs")]
    [InlineData(@"C:\work\project\file#1.cs", "file:///C:/work/project/file%231.cs")]
    public void PathToUri_ProducesValidUri(string path, string expectedUri)
    {
        string result = LspClient.PathToUri(path);
        Assert.Equal(expectedUri, result);
    }

    [Fact]
    public void UriToPath_NonFileUri_ReturnsAsIs()
    {
        string input = "https://example.com/file.cs";
        Assert.Equal(input, LspClient.UriToPath(input));
    }

    [Theory]
    [InlineData(@"C:\work\project\src\file.cs")]
    [InlineData(@"C:\path with spaces\file.cs")]
    [InlineData(@"C:\work\project\file#1.cs")]
    [InlineData(@"C:\work\résumé\naïve.cs")]
    public void Roundtrip_PathToUri_UriToPath(string originalPath)
    {
        string uri = LspClient.PathToUri(originalPath);
        string roundtripped = LspClient.UriToPath(uri);
        Assert.Equal(originalPath, roundtripped);
    }

    [Theory]
    [InlineData("file:///C:/work/project/src/file.cs")]
    [InlineData("file:///C:/path%20with%20spaces/file.cs")]
    [InlineData("file:///C:/work/project/file%231.cs")]
    public void Roundtrip_UriToPath_PathToUri(string originalUri)
    {
        string path = LspClient.UriToPath(originalUri);
        string roundtripped = LspClient.PathToUri(path);
        Assert.Equal(originalUri, roundtripped);
    }
}
