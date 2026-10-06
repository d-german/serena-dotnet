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
    /// <summary>
    /// Files requested concurrently when the caller does not choose. Half the logical
    /// CPUs leaves room for the language server's own work; past eight, a single
    /// language server stops getting faster and only uses more memory.
    /// </summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>
    /// Completed files between cache checkpoints. Without checkpoints a multi-hour
    /// index lives only in memory until the language server stops, so a crash near the
    /// end loses everything; with them a rerun resumes from the last checkpoint.
    /// </summary>
    internal const int CheckpointInterval = 1000;

    /// <summary>
    /// Files per language-server session when the caller does not choose, applied only
    /// while the server has no workspace loaded. Roslyn treats files outside a loaded
    /// workspace as miscellaneous documents and gets slower with every one it has seen,
    /// so a 57,000-file index degrades quadratically; a restart costs one to two
    /// seconds and resets the per-file cost. With a loaded workspace a restart would
    /// reload it, and the slowdown does not occur, so the default leaves it alone.
    /// </summary>
    public const int AutoRestartInterval = 2000;

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
        int? maxParallelism = null,
        int? restartEvery = null,
        CancellationToken ct = default)
    {
        var timeout = perFileTimeout ?? TimeSpan.FromSeconds(10);
        int parallelism = Math.Max(1, maxParallelism ?? DefaultParallelism);
        int restartInterval = Math.Max(0, restartEvery ?? AutoRestartInterval);
        bool restartOnlyWithoutWorkspace = restartEvery is null;
        // Once the probe has timed out, later sessions of the same server will not do
        // better; waiting the full timeout after every restart would only add up.
        bool parseOptionsProbeFailed = false;
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
                    $"Indexing {filesToIndex.Count} {language.ToIdentifier()} files, {parallelism} at a time" +
                    (cachedFiles.Count > 0 ? $" ({cachedFiles.Count} reused from cache)…" : "…"));

                var batches = restartInterval > 0
                    ? filesToIndex.Chunk(restartInterval).Select(b => b.ToList()).ToList()
                    : [filesToIndex];
                for (int b = 0; b < batches.Count; b++)
                {
                    // Also restart a server that died during the previous batch, so one crash
                    // costs that batch rather than every file after it.
                    bool restart = b > 0 && (!client.IsRunning
                        || !restartOnlyWithoutWorkspace
                        || lsManager.IsRepositoryTooLargeToLoad(language));
                    if (restart)
                    {
                        onStatus?.Invoke(
                            $"Restarting {language.ToIdentifier()} language server after {b * restartInterval} files…");
                        await lsManager.RecycleAsync(language, ct);
                        client = await StartLanguageServerSafe(lsManager, language, solutionPaths);
                        if (client is null)
                        {
                            foreach (string file in batches.Skip(b).SelectMany(x => x))
                            {
                                fileIndex++;
                                failures.Add(new IndexFailure(file, $"Failed to restart {language} language server"));
                                onProgress?.Invoke(new IndexProgress(
                                    fileIndex, totalMapped, file, language, false,
                                    $"Failed to restart {language} language server"));
                            }
                            break;
                        }
                    }

                    if (language == Language.CSharp && (b == 0 || restart) && !parseOptionsProbeFailed)
                    {
                        parseOptionsProbeFailed = !await WaitForCSharpParseOptionsAsync(client, onStatus, ct);
                    }

                    cache = lsManager.GetSymbolCache(language);
                    fileIndex = await IndexLanguageFilesAsync(
                        client, batches[b], projectRoot, timeout, parallelism, cache,
                        filesPerLanguage, language, failures,
                        fileIndex, totalMapped, onProgress, onStatus, ct);
                }
            }

            return new IndexResult(filesPerLanguage, failures, sourceFiles.Count, skipped);
        }
        finally
        {
            await lsManager.DisposeAsync();
        }
    }

    /// <summary>
    /// Holds indexing until Roslyn parses loose files with its build symbols, so outlines
    /// do not depend on how soon after a (re)start a file happened to be requested.
    /// </summary>
    private static async Task<bool> WaitForCSharpParseOptionsAsync(
        LspClient client, Action<string>? onStatus, CancellationToken ct)
    {
        if (await CSharpParseOptionsProbe.WaitUntilAppliedAsync(client, TimeSpan.FromSeconds(30), ct))
        {
            return true;
        }
        onStatus?.Invoke(
            "Warning: Roslyn did not define DEBUG for a probe file within 30s; " +
            "#if blocks may be missing from C# outlines. Not probing again this run.");
        return false;
    }

    /// <summary>
    /// Requests symbols for <paramref name="files"/>, <paramref name="parallelism"/> at a
    /// time. Language servers answer concurrent requests, and requesting one file at a
    /// time leaves all but one core idle on large repositories. Files that time out are
    /// retried one at a time with a longer timeout once the parallel pass has finished,
    /// since a timeout under load is usually contention rather than a broken file.
    /// </summary>
    private static async Task<int> IndexLanguageFilesAsync(
        LspClient client, List<string> files, string projectRoot, TimeSpan timeout,
        int parallelism, SymbolCache<UnifiedSymbolInformation[]>? cache,
        Dictionary<Language, int> filesPerLanguage, Language language,
        List<IndexFailure> failures, int fileIndex, int totalMapped,
        Action<IndexProgress>? onProgress, Action<string>? onStatus, CancellationToken ct)
    {
        var gate = new object();
        var timedOut = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int sinceCheckpoint = 0;

        void Report(string file, FileIndexOutcome outcome)
        {
            lock (gate)
            {
                fileIndex++;
                if (outcome.Success)
                {
                    filesPerLanguage[language]++;
                }
                else
                {
                    failures.Add(new IndexFailure(file, outcome.Error!));
                }
                onProgress?.Invoke(new IndexProgress(
                    fileIndex, totalMapped, file, language, outcome.Success, outcome.Error));
            }

            if (outcome.Success && cache is not null
                && Interlocked.Increment(ref sinceCheckpoint) >= CheckpointInterval)
            {
                Interlocked.Exchange(ref sinceCheckpoint, 0);
                cache.Save();
            }
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (file, token) =>
            {
                var outcome = await IndexSingleFileSafe(
                    client, Path.Combine(projectRoot, file), timeout, cache, token);
                if (outcome.TimedOut)
                {
                    timedOut.Enqueue(file);
                    return;
                }
                Report(file, outcome);
            }).ConfigureAwait(false);

        if (!timedOut.IsEmpty)
        {
            var retryTimeout = timeout * 3;
            onStatus?.Invoke(
                $"Retrying {timedOut.Count} {language.ToIdentifier()} file(s) that timed out, " +
                $"one at a time with a {retryTimeout.TotalSeconds:0}s timeout…");
            // Retries that keep timing out mean the server is unresponsive, not that these
            // files are slow; waiting out every remaining retry could take hours.
            const int MaxConsecutiveRetryTimeouts = 3;
            int consecutiveTimeouts = 0;
            foreach (string file in timedOut.Order(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (consecutiveTimeouts >= MaxConsecutiveRetryTimeouts)
                {
                    Report(file, FileIndexOutcome.Failed(
                        $"Not retried: {MaxConsecutiveRetryTimeouts} consecutive retries timed out"));
                    continue;
                }
                var outcome = await IndexSingleFileSafe(
                    client, Path.Combine(projectRoot, file), retryTimeout, cache, ct);
                consecutiveTimeouts = outcome.TimedOut ? consecutiveTimeouts + 1 : 0;
                Report(file, outcome);
            }
        }

        cache?.Save();
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
                var symbols = await FrameworkConditionalSymbols.RequestAsync(client, absolutePath, ct);
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

    /// <summary>
    /// Requests and caches symbols for one file. The caller has already excluded files
    /// whose cached fingerprint is current (see <see cref="PartitionFilesByCache"/>), so
    /// there is no per-file cache lookup here: once the cache has a database, that lookup
    /// is a SQLite query under the cache's lock, which serializes parallel workers.
    /// </summary>
    private static async Task<FileIndexOutcome> IndexSingleFileSafe(
        LspClient client, string absolutePath, TimeSpan timeout,
        SymbolCache<UnifiedSymbolInformation[]>? cache, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var fingerprint = CacheFingerprint.ForFile(absolutePath);
            await client.OpenFileAsync(absolutePath);
            try
            {
                var symbols = await FrameworkConditionalSymbols.RequestAsync(client, absolutePath, timeoutCts.Token);
                if (cache is not null && fingerprint.Length > 0)
                {
                    cache.Set(absolutePath, fingerprint, symbols.ToArray());
                }
            }
            finally
            {
                await client.CloseFileAsync(absolutePath);
            }
            return FileIndexOutcome.Indexed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FileIndexOutcome.TimeOut(timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileIndexOutcome.Failed(ex.Message);
        }
    }

    private readonly record struct FileIndexOutcome(bool Success, bool TimedOut, string? Error)
    {
        public static FileIndexOutcome Indexed => new(true, false, null);

        public static FileIndexOutcome TimeOut(TimeSpan timeout) =>
            new(false, true, $"Timed out after {timeout.TotalSeconds:0.#}s");

        public static FileIndexOutcome Failed(string error) => new(false, false, error);
    }
}
