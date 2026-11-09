// Files created, changed or deleted by anything other than serena's own edit
// tools (an agent's built-in create tool, git, dotnet format) used to enter
// the index only at the next activation. The watcher brings them in as they
// land: a source file is re-outlined from its text, a vanished file is dropped,
// a folder that appears or moves in has its files outlined (they raise no
// events of their own), and a vanished folder triggers one reconcile of the
// whole index, which prunes what is gone.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Serena.Lsp;
using Serena.Lsp.Caching;

namespace Serena.Core.Project;

internal sealed class ProjectFileWatcher : IDisposable
{
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    private const string ReconcileAllKey = "*";
    private static readonly string[] s_privateFolders = [".git", ".serena"];

    private readonly SerenaProject _project;
    private readonly Func<LanguageServerManager?> _manager;
    private readonly Func<Task> _reconcileAll;
    private readonly HashSet<Language> _languages;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private int _processed;
    private int _inFlight;

    public ProjectFileWatcher(
        SerenaProject project,
        Func<LanguageServerManager?> manager,
        Func<Task> reconcileAll,
        IEnumerable<Language> languages,
        ILogger logger,
        TimeSpan? debounce = null)
    {
        _project = project;
        _manager = manager;
        _reconcileAll = reconcileAll;
        _languages = [.. languages];
        _logger = logger;
        _debounce = debounce ?? DefaultDebounce;

        _watcher = new FileSystemWatcher(project.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Schedule(e.FullPath, e.ChangeType);
        _watcher.Changed += (_, e) => Schedule(e.FullPath, e.ChangeType);
        _watcher.Deleted += (_, e) => Schedule(e.FullPath, e.ChangeType);
        _watcher.Renamed += (_, e) =>
        {
            Schedule(e.OldFullPath, WatcherChangeTypes.Deleted);
            Schedule(e.FullPath, WatcherChangeTypes.Created);
        };
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "File watcher for {Root} overflowed; reconciling the whole index", project.Root);
            Debounce(ReconcileAllKey, _reconcileAll);
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Changes applied to the index so far, for status output and tests.</summary>
    public int Processed => Volatile.Read(ref _processed);

    /// <summary>True when no change is waiting or being applied.</summary>
    public bool IsIdle => _pending.IsEmpty && Volatile.Read(ref _inFlight) == 0;

    private void Schedule(string fullPath, WatcherChangeTypes change)
    {
        if (_lifetime.IsCancellationRequested || !IsCandidate(fullPath))
        {
            return;
        }

        if (IsSourceFile(fullPath))
        {
            Debounce(fullPath, () => ProcessFileAsync(fullPath));
            return;
        }

        // A folder's LastWrite moves on every child write; only its arrival or
        // departure matters here.
        if (change == WatcherChangeTypes.Changed)
        {
            return;
        }

        // A folder that appeared or moved in: its files raised no events of their own.
        if (Directory.Exists(fullPath))
        {
            Debounce(fullPath, () => ProcessFolderAsync(fullPath));
            return;
        }

        // Something that is not a source file vanished, possibly a folder of them.
        if (change == WatcherChangeTypes.Deleted)
        {
            Debounce(ReconcileAllKey, _reconcileAll);
        }
    }

    private async Task ProcessFolderAsync(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string file in files)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }
            if (IsCandidate(file) && IsSourceFile(file))
            {
                await ProcessFileAsync(file).ConfigureAwait(false);
            }
        }
    }

    private bool IsCandidate(string fullPath)
    {
        string relative = Path.GetRelativePath(_project.Root, fullPath).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }
        string top = relative.Split('/', 2)[0];
        if (s_privateFolders.Contains(top, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        return !_project.IsIgnoredPath(relative) && !_project.IsExcludedByPattern(relative);
    }

    private bool IsSourceFile(string fullPath) =>
        LanguageExtensions.FromFileExtension(Path.GetExtension(fullPath)) is { } language
        && _languages.Contains(language);

    private void Debounce(string key, Func<Task> work)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pending.AddOrUpdate(key, cts, (_, previous) =>
        {
            previous.Cancel();
            return cts;
        });
        _ = DelayThenRunAsync(key, cts, work);
    }

    private async Task DelayThenRunAsync(string key, CancellationTokenSource cts, Func<Task> work)
    {
        try
        {
            try
            {
                await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a later event for the same key, or disposed
            }

            if (!_pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts)))
            {
                return;
            }

            Interlocked.Increment(ref _inFlight);
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "External change to {Path} was not applied to the index", key);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task ProcessFileAsync(string fullPath)
    {
        var manager = _manager();
        var language = LanguageExtensions.FromFileExtension(Path.GetExtension(fullPath));
        if (manager is null || language is null)
        {
            return;
        }
        var cache = manager.GetOrLoadSymbolCache(language.Value);
        if (cache is null)
        {
            return;
        }

        if (!File.Exists(fullPath))
        {
            if (cache.TryGetUnchecked(fullPath) is not null)
            {
                cache.Remove(fullPath);
                Interlocked.Increment(ref _processed);
            }
            return;
        }

        // Serena's own edit tools refresh the entry before the event arrives;
        // a current fingerprint means there is nothing to do.
        if (cache.TryGet(fullPath, CacheFingerprint.ForFile(fullPath)) is not null)
        {
            return;
        }

        string? content = await ReadWhenReleasedAsync(fullPath).ConfigureAwait(false);
        if (content is null)
        {
            return;
        }
        await manager.UpdateSymbolCacheForWrittenFileAsync(language.Value, fullPath, content, _lifetime.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _processed);
    }

    /// <summary>The writer may still hold the file when the event fires.</summary>
    private async Task<string?> ReadWhenReleasedAsync(string fullPath)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(fullPath, _lifetime.Token).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50 * (attempt + 1), _lifetime.Token).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _lifetime.Dispose();
    }
}
