// ProjectIndexer - Orchestrates pre-indexing of all source files in a project
// Equivalent to Python Serena's `serena project index` / `_index_project`

using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Serena.Core.Config;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Project;

/// <summary>
/// Progress information reported during project indexing.
/// </summary>
public sealed record IndexProgress(
    int CurrentFile,
    int TotalFiles,
    string FilePath,
    Language Language,
    bool Success,
    string? Error = null);

/// <summary>
/// Summary of a completed indexing run.
/// </summary>
public sealed record IndexResult(
    Dictionary<Language, int> FilesPerLanguage,
    List<IndexFailure> Failures,
    int TotalFiles,
    int SkippedFiles);

/// <summary>
/// Records a single file indexing failure.
/// </summary>
public sealed record IndexFailure(string FilePath, string Error);

/// <summary>
/// Orchestrates indexing of all source files in a project by launching
/// language servers and requesting document symbols for each file.
/// </summary>
public sealed class ProjectIndexer
{
    private readonly LanguageServerRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProjectIndexer> _logger;

    public ProjectIndexer(
        LanguageServerRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ProjectIndexer>();
    }

    /// <summary>
    /// Indexes all source files in a project, reporting progress via callback.
    /// </summary>
    public async Task<IndexResult> IndexProjectAsync(
        string projectRoot,
        Action<IndexProgress>? onProgress = null,
        TimeSpan? perFileTimeout = null,
        CancellationToken ct = default)
    {
        var timeout = perFileTimeout ?? TimeSpan.FromSeconds(10);
        var project = CreateProject(projectRoot);
        var lsManager = new LanguageServerManager(projectRoot, _registry, _loggerFactory);

        try
        {
            var sourceFiles = project.GatherSourceFiles();
            var grouped = GroupFilesByLanguage(sourceFiles, project);

            var filesPerLanguage = new Dictionary<Language, int>();
            var failures = new List<IndexFailure>();
            int skipped = sourceFiles.Count - grouped.Sum(g => g.Value.Count);
            int fileIndex = 0;
            int totalMapped = grouped.Sum(g => g.Value.Count);

            foreach (var (language, files) in grouped)
            {
                LspClient? client = await StartLanguageServerSafe(lsManager, language);
                if (client is null)
                {
                    foreach (string file in files)
                    {
                        failures.Add(new IndexFailure(file, $"Failed to start {language} language server"));
                    }
                    continue;
                }

                filesPerLanguage[language] = 0;
                var cache = lsManager.GetSymbolCache(language);

                fileIndex = await IndexLanguageFilesAsync(
                    client, files, projectRoot, timeout, cache,
                    filesPerLanguage, language, failures,
                    fileIndex, totalMapped, onProgress, ct);
            }

            return new IndexResult(filesPerLanguage, failures, sourceFiles.Count, skipped);
        }
        finally
        {
            await lsManager.DisposeAsync();
        }
    }

    private static async Task<int> IndexLanguageFilesAsync(
        LspClient client, List<string> files, string projectRoot, TimeSpan timeout,
        SymbolCache<UnifiedSymbolInformation[]>? cache,
        Dictionary<Language, int> filesPerLanguage, Language language,
        List<IndexFailure> failures, int fileIndex, int totalMapped,
        Action<IndexProgress>? onProgress, CancellationToken ct)
    {
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            fileIndex++;
            string absolutePath = Path.Combine(projectRoot, file);

            var result = await IndexSingleFileSafe(client, absolutePath, timeout, cache);
            bool success = result.IsSuccess;

            if (success)
            {
                filesPerLanguage[language]++;
            }
            else
            {
                failures.Add(new IndexFailure(file, result.Error));
            }

            onProgress?.Invoke(new IndexProgress(
                fileIndex, totalMapped, file, language, success,
                success ? null : result.Error));
        }

        return fileIndex;
    }

    /// <summary>
    /// Indexes a single file, returning the symbols found. Useful for debugging.
    /// </summary>
    public async Task<Result<IReadOnlyList<UnifiedSymbolInformation>>> IndexFileAsync(
        string projectRoot,
        string filePath,
        CancellationToken ct = default)
    {
        string absolutePath = Path.GetFullPath(filePath);
        string ext = Path.GetExtension(absolutePath);
        var language = LanguageExtensions.FromFileExtension(ext);
        if (language is null)
        {
            return Result.Failure<IReadOnlyList<UnifiedSymbolInformation>>(
                $"No language server for extension '{ext}'");
        }

        var lsManager = new LanguageServerManager(projectRoot, _registry, _loggerFactory);
        try
        {
            var client = await lsManager.GetOrStartAsync(language.Value, ct: ct);
            var cache = lsManager.GetSymbolCache(language.Value);
            var fingerprint = CacheFingerprint.ForFile(absolutePath);

            if (cache is not null && fingerprint.Length > 0)
            {
                var cached = cache.TryGet(absolutePath, fingerprint);
                if (cached is not null)
                {
                    return Result.Success<IReadOnlyList<UnifiedSymbolInformation>>(cached);
                }
            }

            await client.OpenFileAsync(absolutePath);
            try
            {
                var symbols = await client.RequestDocumentSymbolsAsync(absolutePath, ct);
                if (cache is not null && fingerprint.Length > 0)
                {
                    cache.Set(absolutePath, fingerprint, symbols.ToArray());
                }
                return Result.Success<IReadOnlyList<UnifiedSymbolInformation>>(symbols);
            }
            finally
            {
                await client.CloseFileAsync(absolutePath);
            }
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<UnifiedSymbolInformation>>(ex.Message);
        }
        finally
        {
            await lsManager.DisposeAsync();
        }
    }

    private static SerenaProject CreateProject(string projectRoot)
    {
        return new SerenaProject(
            projectRoot,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SerenaProject>.Instance);
    }

    private static Dictionary<Language, List<string>> GroupFilesByLanguage(
        IReadOnlyList<string> files, SerenaProject project)
    {
        var grouped = new Dictionary<Language, List<string>>();
        foreach (string file in files)
        {
            // Skip files that are gitignored
            if (project.IsIgnoredPath(file))
            {
                continue;
            }

            string ext = Path.GetExtension(file);
            var language = LanguageExtensions.FromFileExtension(ext);
            if (language is null)
            {
                continue;
            }

            if (!grouped.TryGetValue(language.Value, out var list))
            {
                list = [];
                grouped[language.Value] = list;
            }
            list.Add(file);
        }
        return grouped;
    }

    private async Task<LspClient?> StartLanguageServerSafe(
        LanguageServerManager lsManager, Language language)
    {
        try
        {
            return await lsManager.GetOrStartAsync(language);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start language server for {Language}", language);
            return null;
        }
    }

    private static async Task<Result> IndexSingleFileSafe(
        LspClient client, string absolutePath, TimeSpan timeout,
        SymbolCache<UnifiedSymbolInformation[]>? cache = null)
    {
        try
        {
            var fingerprint = CacheFingerprint.ForFile(absolutePath);
            if (cache is not null && fingerprint.Length > 0)
            {
                var cached = cache.TryGet(absolutePath, fingerprint);
                if (cached is not null)
                {
                    return Result.Success();
                }
            }

            using var cts = new CancellationTokenSource(timeout);
            await client.OpenFileAsync(absolutePath);
            try
            {
                var symbols = await client.RequestDocumentSymbolsAsync(absolutePath, cts.Token);
                if (cache is not null && fingerprint.Length > 0)
                {
                    cache.Set(absolutePath, fingerprint, symbols.ToArray());
                }
            }
            finally
            {
                await client.CloseFileAsync(absolutePath);
            }
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure($"Timed out after {timeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message);
        }
    }
}
