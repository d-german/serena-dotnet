// Language Server Process Lifecycle - Ported from solidlsp/ls_process.py
// Phase 2A: Process spawning, StreamJsonRpc transport, shutdown
// StreamJsonRpc replaces ~400 lines of manual JSON-RPC framing from Python

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Serena.Lsp.Protocol;
using StreamJsonRpc;

namespace Serena.Lsp.Process;

/// <summary>
/// Manages a language server subprocess and the JSON-RPC connection over stdin/stdout.
/// Uses StreamJsonRpc with HeaderDelimitedMessageHandler for LSP Content-Length framing.
/// Ported from solidlsp/ls_process.py LanguageServerProcess.
/// </summary>
public sealed class LanguageServerProcess : IAsyncDisposable
{
    private readonly ProcessLaunchInfo _launchInfo;
    private readonly Language _language;
    private readonly ILogger _logger;
    private readonly TimeSpan? _requestTimeout;
    private readonly bool _politeMode;

    /// <summary>
    /// Tracks all live LSP child processes so we can force-kill them if our own
    /// process is shut down without DisposeAsync running (e.g., VS Code force-killing
    /// the MCP stdio server). Keyed by PID.
    /// </summary>
    private static readonly ConcurrentDictionary<int, System.Diagnostics.Process> s_liveProcesses = new();
    private static int s_exitHandlerInstalled;

    private static void EnsureExitHandlerInstalled()
    {
        if (Interlocked.Exchange(ref s_exitHandlerInstalled, 1) != 0)
        {
            return;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllTrackedProcesses();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillAllTrackedProcesses();
    }

    private static void KillAllTrackedProcesses() => KillAllLiveProcesses();

    /// <summary>
    /// Immediately kills every tracked language server process tree.
    /// Safe to call from any thread; intended for "drop CPU now" cancel paths.
    /// Returns the number of processes that were alive when killed.
    /// </summary>
    public static int KillAllLiveProcesses()
    {
        int killed = 0;
        foreach (var kvp in s_liveProcesses)
        {
            try
            {
                if (!kvp.Value.HasExited)
                {
                    kvp.Value.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
        s_liveProcesses.Clear();
        return killed;
    }

    private System.Diagnostics.Process? _process;
    private JsonRpc? _rpc;
    private Task? _stderrReaderTask;
    private bool _isShuttingDown;
    private bool _disposed;

    /// <summary>
    /// The underlying JsonRpc instance for sending requests/notifications.
    /// </summary>
    public JsonRpc Rpc => _rpc ?? throw new InvalidOperationException("Language server not started.");

    /// <summary>
    /// Whether the language server process is currently running.
    /// </summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// The language this process serves.
    /// </summary>
    public Language Language => _language;

    public LanguageServerProcess(
        ProcessLaunchInfo launchInfo,
        Language language,
        ILogger logger,
        TimeSpan? requestTimeout = null,
        bool politeMode = false)
    {
        _launchInfo = launchInfo;
        _language = language;
        _logger = logger;
        _requestTimeout = requestTimeout;
        _politeMode = politeMode;
    }

    /// <summary>
    /// Best-effort application of polite-mode OS scheduling: BelowNormal
    /// priority and a CPU affinity mask covering only half the cores
    /// (rounded down, minimum 1). Catches and logs any exception — these
    /// APIs are Windows-meaningful but throw on Linux/macOS.
    /// </summary>
    private static void ApplyPoliteSettings(System.Diagnostics.Process p, ILogger logger)
    {
        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Polite mode: PriorityClass=BelowNormal not applied (likely non-Windows)");
        }

        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                return;
            }
            int capCores = Math.Max(1, Environment.ProcessorCount / 2);
            p.ProcessorAffinity = (IntPtr)((1L << capCores) - 1);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Polite mode: ProcessorAffinity cap not applied (likely non-Windows)");
        }
    }

    /// <summary>
    /// Starts the language server subprocess and establishes the JSON-RPC connection.
    /// </summary>
    /// <param name="configureRpc">
    /// Optional callback invoked after JsonRpc is created but before StartListening().
    /// Use this to register handlers for server-initiated requests (e.g., workspace/configuration).
    /// </param>
    public void Start(Action<JsonRpc>? configureRpc = null)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Language server process already started.");
        }

        string command;
        string arguments;
        var cmdParts = _launchInfo.Command;

        if (cmdParts.Count == 0)
        {
            throw new ArgumentException("ProcessLaunchInfo.Command must not be empty.");
        }
        else if (cmdParts.Count == 1)
        {
            command = cmdParts[0];
            arguments = string.Empty;
        }
        else
        {
            command = cmdParts[0];
            arguments = string.Join(' ', cmdParts.Skip(1).Select(QuoteArgIfNeeded));
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _launchInfo.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };

        // Apply environment variables
        foreach (var (key, value) in _launchInfo.Environment)
        {
            psi.Environment[key] = value;
        }

        _logger.LogInformation("Starting language server [{Language}]: {Command} {Args}",
            _language, command, arguments);

        _process = System.Diagnostics.Process.Start(psi)!;
        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"Language server process failed to start: {command} {arguments}");
        }

        // Track this process for emergency cleanup if our process dies abruptly.
        EnsureExitHandlerInstalled();
        s_liveProcesses[_process.Id] = _process;

        if (_politeMode)
        {
            ApplyPoliteSettings(_process, _logger);
        }

        // Set up StreamJsonRpc over stdin/stdout with LSP Content-Length framing.
        // Use JsonMessageFormatter (Newtonsoft.Json) with camelCase for LSP compatibility.
        // SystemTextJsonFormatter is NOT compatible with Roslyn Language Server.
        var formatter = new JsonMessageFormatter();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        var handler = new HeaderDelimitedMessageHandler(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            formatter);

        _rpc = new JsonRpc(handler);
        _rpc.Disconnected += OnRpcDisconnected;
        configureRpc?.Invoke(_rpc);
        _rpc.StartListening();

        // Start stderr reader
        _stderrReaderTask = Task.Run(() => ReadStderrAsync());

        _logger.LogInformation("Language server [{Language}] started (PID: {Pid})", _language, _process.Id);
    }

    /// <summary>
    /// Sends an LSP request and returns the response.
    /// </summary>
    public async Task<TResult> SendRequestAsync<TResult>(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();

        using var cts = CreateTimeoutCts(cancellationToken);
        try
        {
            if (parameters is not null)
            {
                return await Rpc.InvokeWithParameterObjectAsync<TResult>(method, parameters, cts.Token);
            }
            return await Rpc.InvokeAsync<TResult>(method, cts.Token);
        }
        catch (RemoteInvocationException ex)
        {
            throw new LspClientException(
                $"LSP request '{method}' failed: {ex.Message}",
                _language,
                ex);
        }
        catch (ConnectionLostException ex)
        {
            _logger.LogError(ex, "Language server [{Language}] disconnected during '{Method}'. Process running: {IsRunning}",
                _language, method, IsRunning);
            throw new LanguageServerTerminatedException(
                $"Language server [{_language}] disconnected during '{method}'",
                _language,
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "LSP request '{Method}' timed out or LS died. Process running: {IsRunning}",
                method, IsRunning);
            if (!IsRunning)
            {
                throw new LanguageServerTerminatedException(
                    $"Language server [{_language}] terminated during '{method}'",
                    _language,
                    ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Sends an LSP request that returns void/null.
    /// </summary>
    public async Task SendRequestAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();

        using var cts = CreateTimeoutCts(cancellationToken);
        try
        {
            if (parameters is not null)
            {
                await Rpc.InvokeWithParameterObjectAsync(method, parameters, cts.Token);
            }
            else
            {
                await Rpc.InvokeAsync(method, cts.Token);
            }
        }
        catch (RemoteInvocationException ex)
        {
            throw new LspClientException(
                $"LSP request '{method}' failed: {ex.Message}",
                _language,
                ex);
        }
        catch (ConnectionLostException ex)
        {
            _logger.LogError(ex, "Language server [{Language}] disconnected during '{Method}'. Process running: {IsRunning}",
                _language, method, IsRunning);
            throw new LanguageServerTerminatedException(
                $"Language server [{_language}] disconnected during '{method}'",
                _language,
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "LSP request '{Method}' timed out or LS died. Process running: {IsRunning}",
                method, IsRunning);
            if (!IsRunning)
            {
                throw new LanguageServerTerminatedException(
                    $"Language server [{_language}] terminated during '{method}'",
                    _language,
                    ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Sends an LSP notification (no response expected).
    /// </summary>
    public async Task SendNotificationAsync(string method, object? parameters = null)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            if (parameters is not null)
            {
                await Rpc.NotifyWithParameterObjectAsync(method, parameters);
            }
            else
            {
                await Rpc.NotifyAsync(method);
            }
        }
        catch (ConnectionLostException)
        {
            _logger.LogWarning("Cannot send notification '{Method}' — server disconnected", method);
        }
    }

    /// <summary>
    /// Registers a handler for server-initiated requests.
    /// </summary>
    public void OnRequest(string method, Func<JToken?, object?> handler)
    {
        Rpc.AddLocalRpcMethod(method, handler);
    }

    /// <summary>
    /// Performs the LSP shutdown sequence: shutdown request → exit notification → process kill.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _logger.LogInformation("Shutting down language server [{Language}]", _language);

        try
        {
            // Send shutdown request
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            await Rpc.InvokeWithCancellationAsync<object?>("shutdown", cancellationToken: linked.Token);

            // Send exit notification
            await Rpc.NotifyAsync("exit");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error during LSP shutdown sequence for [{Language}]", _language);
        }

        // Wait briefly for graceful exit, then force kill
        await WaitForExitOrKillAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Force-stops the language server process and all its children.
    /// </summary>
    public void ForceStop()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        _logger.LogWarning("Force-stopping language server [{Language}] (PID: {Pid})", _language, _process.Id);
        KillProcessTree(_process);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (IsRunning && !_isShuttingDown)
        {
            try
            {
                await ShutdownAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during dispose shutdown");
                ForceStop();
            }
        }

        _rpc?.Dispose();

        if (_stderrReaderTask is not null)
        {
            try
            {
                await _stderrReaderTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // Ignore — stderr reader will die with the process
            }
        }

        if (_process is not null)
        {
            s_liveProcesses.TryRemove(_process.Id, out _);
            _process.Dispose();
        }
    }

    private void EnsureRunning()
    {
        if (!IsRunning)
        {
            throw new LanguageServerTerminatedException(
                $"Language server [{_language}] is not running.",
                _language);
        }
    }

    private CancellationTokenSource CreateTimeoutCts(CancellationToken cancellationToken)
    {
        if (_requestTimeout.HasValue)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_requestTimeout.Value);
            return cts;
        }
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (_process is { HasExited: false })
            {
                string? line = await _process.StandardError.ReadLineAsync();
                if (line is null)
                {
                    break;
                }
                _logger.LogDebug("[{Language} stderr] {Line}", _language, line);
            }
        }
        catch (Exception ex) when (!_isShuttingDown)
        {
            _logger.LogError(ex, "Error reading stderr from [{Language}]", _language);
        }
    }

    private void OnRpcDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        if (!_isShuttingDown)
        {
            _logger.LogWarning(
                "Language server [{Language}] disconnected: {Description} ({Reason})",
                _language, e.Description, e.Reason);
        }
    }

    private async Task WaitForExitOrKillAsync(TimeSpan timeout)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Language server [{Language}] didn't exit in time, killing", _language);
            KillProcessTree(_process);
        }
    }

    private static void KillProcessTree(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited
        }
        catch (Exception)
        {
            // Best-effort cleanup
        }
        finally
        {
            s_liveProcesses.TryRemove(process.Id, out _);
        }
    }

    private static string QuoteArgIfNeeded(string arg) =>
        arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;
}
