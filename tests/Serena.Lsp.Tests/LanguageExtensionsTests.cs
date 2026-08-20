using FluentAssertions;
using Serena.Lsp;

namespace Serena.Lsp.Tests;

public sealed class LanguageExtensionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(".cache")]
    [InlineData(".unknown")]
    public void FromFileExtension_ReturnsNull_ForUnknownOrEmptyExtensions(string extension)
    {
        LanguageExtensions.FromFileExtension(extension).Should().BeNull();
    }

    [Theory]
    [InlineData(".cs", Language.CSharp)]
    [InlineData("cs", Language.CSharp)]
    [InlineData(".ts", Language.TypeScript)]
    [InlineData(".py", Language.Python)]
    public void FromFileExtension_ReturnsKnownLanguage_ForRecognizedExtensions(string extension, Language expected)
    {
        LanguageExtensions.FromFileExtension(extension).Should().Be(expected);
    }
}
