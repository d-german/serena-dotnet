// ProjectIndexer - Orchestrates pre-indexing of all source files in a project
// Equivalent to Python Serena's `serena project index` / `_index_project`

using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Serena.Core.Config;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Project;

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
        Action<string>? onStatus = null,
        TimeSpan? perFileTimeout = null,
        IReadOnlyList<string>? solutionPaths = null,
        CancellationToken ct = default)
    {
        var timeout = perFileTimeout ?? TimeSpan.FromSeconds(10);
        var project = CreateProject(projectRoot);
        var lsManager = new LanguageServerManager(projectRoot, _registry, _loggerFactory);

        try
        {
            onStatus?.Invoke("Discovering source files…");
            var sourceFiles = solutionPaths is { Count: > 0 }
                ? await GatherSolutionSourceFilesAsync(
                    project, projectRoot, solutionPaths, onStatus, ct).ConfigureAwait(false)
                : project.GatherSourceFiles();
            var grouped = GroupFilesByLanguage(sourceFiles, project);
            var configuredLanguages = project.GetConfiguredLanguages();
            if (configuredLanguages.Count > 0)
            {
                onStatus?.Invoke(
                    "Using configured languages: " +
                    string.Join(", ", configuredLanguages.Select(l => l.ToIdentifier())));
            }
            var filesPerLanguage = new Dictionary<Language, int>();
            var failures = new List<IndexFailure>();
            int skipped = sourceFiles.Count - grouped.Sum(g => g.Value.Count);
            int fileIndex = 0;
            int totalMapped = grouped.Sum(g => g.Value.Count);
            onStatus?.Invoke(skipped > 0
                ? $"Found {totalMapped} source files ({grouped.Count} language(s)); skipped {skipped} ignored, non-source, or disabled-language file(s)"
                : $"Found {totalMapped} source files ({grouped.Count} language(s))");

            foreach (var (language, files) in grouped)
            {
                filesPerLanguage[language] = 0;
                var cache = lsManager.GetOrLoadSymbolCache(language);
                var (cachedFiles, filesToIndex) = PartitionFilesByCache(
                    files, projectRoot, cache);

                foreach (string file in cachedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    fileIndex++;
                    filesPerLanguage[language]++;
                    onProgress?.Invoke(new IndexProgress(
                        fileIndex, totalMapped, file, language, true));
                }

                if (filesToIndex.Count == 0)
                {
                    onStatus?.Invoke(
                        $"Reused {cachedFiles.Count} cached {language.ToIdentifier()} files; language server not started");
                    continue;
                }

                onStatus?.Invoke(
                    $"Starting {language.ToIdentifier()} language server for {filesToIndex.Count} new or changed file(s)…");
                LspClient? client = await StartLanguageServerSafe(
                    lsManager, language, solutionPaths);
                if (client is null)
                {
                    foreach (string file in filesToIndex)
                    {
                        fileIndex++;
                        failures.Add(new IndexFailure(file, $"Failed to start {language} language server"));
                        onProgress?.Invoke(new IndexProgress(
                            fileIndex, totalMapped, file, language, false,
                            $"Failed to start {language} language server"));
                    }
                    continue;
                }
                onStatus?.Invoke(
                    $"Indexing {filesToIndex.Count} {language.ToIdentifier()} files" +
                    (cachedFiles.Count > 0 ? $" ({cachedFiles.Count} reused from cache)…" : "…"));

                cache = lsManager.GetSymbolCache(language);

                fileIndex = await IndexLanguageFilesAsync(
                    client, filesToIndex, projectRoot, timeout, cache,
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
            var cache = lsManager.GetOrLoadSymbolCache(language.Value);
            var fingerprint = CacheFingerprint.ForFile(absolutePath);

            if (cache is not null && fingerprint.Length > 0)
            {
                var cached = cache.TryGet(absolutePath, fingerprint);
                if (cached is not null)
                {
                    return Result.Success<IReadOnlyList<UnifiedSymbolInformation>>(cached);
                }
            }

            var client = await lsManager.GetOrStartAsync(language.Value, ct: ct);
            cache = lsManager.GetSymbolCache(language.Value);

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

    internal static Dictionary<Language, List<string>> GroupFilesByLanguage(
        IReadOnlyList<string> files, SerenaProject project)
    {
        var grouped = new Dictionary<Language, List<string>>();
        var configuredLanguages = project.GetConfiguredLanguages();
        HashSet<Language>? allowedLanguages = configuredLanguages.Count > 0
            ? configuredLanguages.ToHashSet()
            : null;

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
            if (allowedLanguages is not null && !allowedLanguages.Contains(language.Value))
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
        LanguageServerManager lsManager,
        Language language,
        IReadOnlyList<string>? solutionPaths = null)
    {
        try
        {
            CustomLsSettings? settings = null;
            if (language == Language.CSharp && solutionPaths is { Count: > 0 })
            {
                settings = new CustomLsSettings(new Dictionary<string, object>
                {
                    [CSharpLanguageServer.ScopeSolutionsSetting] = string.Join(';', solutionPaths)
                }, _logger);
            }
            return await lsManager.GetOrStartAsync(language, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start language server for {Language}", language);
            return null;
        }
    }

    internal static (List<string> CachedFiles, List<string> FilesToIndex) PartitionFilesByCache(
        IReadOnlyList<string> files,
        string projectRoot,
        SymbolCache<UnifiedSymbolInformation[]>? cache)
    {
        if (cache is null)
        {
            return ([], files.ToList());
        }

        var candidates = files.Select(file =>
        {
            string absolutePath = Path.Combine(projectRoot, file);
            return (File: file, AbsolutePath: absolutePath,
                Fingerprint: CacheFingerprint.ForFile(absolutePath));
        }).ToList();
        var freshPaths = cache.GetFreshPaths(
            candidates.Select(candidate => (candidate.AbsolutePath, candidate.Fingerprint)));

        var cachedFiles = new List<string>(files.Count);
        var filesToIndex = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate.Fingerprint.Length > 0
                && freshPaths.Contains(SymbolCacheKeys.Normalize(candidate.AbsolutePath)))
            {
                cachedFiles.Add(candidate.File);
            }
            else
            {
                filesToIndex.Add(candidate.File);
            }
        }
        return (cachedFiles, filesToIndex);
    }

    /// <summary>
    /// Discovers source files only beneath the project directories referenced
    /// by the selected solution. The cache remains rooted at the repository,
    /// so future solution-scoped and whole-repository queries can share it.
    /// Linked source files referenced by project items are included as well.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> GatherSolutionSourceFilesAsync(
        SerenaProject project,
        string projectRoot,
        IReadOnlyList<string> solutionPaths,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var directProjectPaths = await SolutionParser.GetCSharpProjectPathsAsync(solutionPaths, ct)
            .ConfigureAwait(false);
        var projectPaths = await SolutionParser.GetCSharpProjectClosureAsync(solutionPaths, ct)
            .ConfigureAwait(false);
        if (projectPaths.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected solution does not contain any existing C# projects.");
        }

        int transitiveCount = projectPaths.Count - directProjectPaths.Count;
        onStatus?.Invoke(transitiveCount > 0
            ? $"Solution scope contains {directProjectPaths.Count} direct and {transitiveCount} transitive dependency C# project(s); scanning only those project directories"
            : $"Solution scope contains {projectPaths.Count} C# project(s); scanning only those project directories");

        string root = Path.GetFullPath(projectRoot);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string projectPath in projectPaths)
        {
            ct.ThrowIfCancellationRequested();
            string? projectDirectory = Path.GetDirectoryName(projectPath);
            if (projectDirectory is null || !Directory.Exists(projectDirectory))
            {
                continue;
            }

            foreach (string absolutePath in EnumerateProjectFiles(
                         project, root, projectDirectory, ct))
            {
                AddSupportedSourceFile(project, root, absolutePath, files);
            }

            AddLinkedProjectItems(project, root, projectPath, files);
        }

        return files.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> EnumerateProjectFiles(
        SerenaProject project,
        string projectRoot,
        string startDirectory,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(startDirectory);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();

            IEnumerable<string> childDirectories;
            IEnumerable<string> childFiles;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
                childFiles = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string childDirectory in childDirectories)
            {
                string relative = Path.GetRelativePath(projectRoot, childDirectory)
                    .Replace('\\', '/') + "/";
                if (!project.IsIgnoredPath(relative))
                {
                    pending.Push(childDirectory);
                }
            }

            foreach (string childFile in childFiles)
            {
                yield return childFile;
            }
        }
    }

    private static void AddSupportedSourceFile(
        SerenaProject project,
        string projectRoot,
        string absolutePath,
        HashSet<string> files)
    {
        if (LanguageExtensions.FromFileExtension(Path.GetExtension(absolutePath)) is null)
        {
            return;
        }

        string relativePath = Path.GetRelativePath(projectRoot, absolutePath).Replace('\\', '/');
        if (!project.IsIgnoredPath(relativePath))
        {
            files.Add(relativePath);
        }
    }

    private static void AddLinkedProjectItems(
        SerenaProject project,
        string projectRoot,
        string projectPath,
        HashSet<string> files)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Load(projectPath);
            string projectDirectory = Path.GetDirectoryName(projectPath)!;
            foreach (var item in document.Descendants())
            {
                string? include = item.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)
                    || include.IndexOfAny(['*', '?']) >= 0
                    || LanguageExtensions.FromFileExtension(Path.GetExtension(include)) is null)
                {
                    continue;
                }

                string absolutePath = Path.GetFullPath(Path.Combine(projectDirectory, include));
                if (File.Exists(absolutePath))
                {
                    AddSupportedSourceFile(project, projectRoot, absolutePath, files);
                }
            }
        }
        catch (Exception)
        {
            // Roslyn will report malformed/unloadable project files during
            // scoped warmup. Directory discovery still provides a useful cache.
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
