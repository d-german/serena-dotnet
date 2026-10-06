// CSharpParseOptionsProbe - waits until Roslyn parses loose files with build symbols

using Serena.Lsp.Client;

namespace Serena.Core.Project;

/// <summary>
/// Waits until a freshly started Roslyn server applies preprocessor symbols to files
/// outside a loaded workspace. Roslyn parses such files through a canonical
/// miscellaneous-files project that takes a moment to load; until it has, documents are
/// parsed with no symbols defined, so <c>#if DEBUG</c> and <c>#if NET8_0_OR_GREATER</c>
/// blocks silently vanish from their outlines. Indexing during that window produces an
/// index that differs from one built a moment later, and every restart reopens it.
/// </summary>
internal static class CSharpParseOptionsProbe
{
    private const string ProbeSymbol = "SerenaParseOptionsProbe";

    private const string ProbeSource =
        $$"""
        #if DEBUG
        internal sealed class {{ProbeSymbol}} { }
        #endif
        """;

    /// <summary>
    /// Returns true once a probe file's <c>#if DEBUG</c> class appears in its outline, or
    /// false if it has not appeared within <paramref name="timeout"/> or the probe could not
    /// run at all (no writable temp directory, a request that failed or hung). On false the
    /// server is assumed never to define the symbol and indexing proceeds anyway; only
    /// cancellation of <paramref name="ct"/> propagates.
    /// </summary>
    public static async Task<bool> WaitUntilAppliedAsync(
        LspClient client, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        string? probePath = null;
        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "serena-parse-probe");
            Directory.CreateDirectory(directory);
            probePath = Path.Combine(directory, $"{Guid.NewGuid():N}.cs");
            await File.WriteAllTextAsync(probePath, ProbeSource, deadline.Token).ConfigureAwait(false);

            await client.OpenFileAsync(probePath).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var symbols = await client.RequestDocumentSymbolsAsync(probePath, deadline.Token)
                        .ConfigureAwait(false);
                    if (symbols.Any(s => s.Name == ProbeSymbol))
                    {
                        return true;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                try { await client.CloseFileAsync(probePath).ConfigureAwait(false); } catch { /* best-effort */ }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (probePath is not null)
            {
                try { File.Delete(probePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
