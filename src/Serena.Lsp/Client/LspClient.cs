// Core LSP Client - Ported from solidlsp/ls.py SolidLanguageServer
// Phase 3A: The main language server client for symbol operations

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serena.Lsp.Process;
using Serena.Lsp.Protocol;
using Serena.Lsp.Protocol.Constants;
using Serena.Lsp.Protocol.Types;
using StreamJsonRpc;

namespace Serena.Lsp.Client;

/// <summary>
/// Core LSP client providing language-agnostic symbol operations.
/// Manages a language server process and provides methods for querying symbols,
/// definitions, references, hover information, and code editing.
/// Ported from solidlsp/ls.py SolidLanguageServer (2,608 lines).
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly ILogger<LspClient> _logger;
    private readonly LanguageServerProcess _process;
    private readonly LspRequestClient _requests;
    private readonly LspNotificationClient _notifications;
    private readonly FileBufferManager _fileBuffers;
    private readonly string _projectRoot;
    private readonly Language _language;
    private readonly string _encoding;

    private ServerCapabilities? _serverCapabilities;
    private bool _serverStarted;

    public Language Language => _language;
    public string ProjectRoot => _projectRoot;
    public bool IsRunning => _process.IsRunning;
    public ServerCapabilities? ServerCapabilities => _serverCapabilities;
    public FileBufferManager FileBuffers => _fileBuffers;

    public LspClient(
        LanguageServerProcess process,
        string projectRoot,
        Language language,
        ILogger<LspClient> logger,
        string encoding = "utf-8")
    {
        _process = process;
        _projectRoot = Path.GetFullPath(projectRoot);
        _language = language;
        _logger = logger;
        _encoding = encoding;
        _requests = new LspRequestClient(process);
        _notifications = new LspNotificationClient(process);
        _fileBuffers = new FileBufferManager();
    }

    /// <summary>
    /// Starts the language server and performs the initialization handshake.
    /// </summary>
    public async Task StartAsync(
        object? initializationOptions = null,
        object? workspaceSettings = null,
        CancellationToken cancellationToken = default)
    {
        _process.Start(ConfigureDefaultHandlers);

        var initParams = new InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = PathToUri(_projectRoot),
            RootPath = _projectRoot,
            Capabilities = ClientCapabilitiesFactory.CreateDefaultCapabilities(),
            InitializationOptions = initializationOptions,
            WorkspaceFolders =
            [
                new WorkspaceFolder(PathToUri(_projectRoot), Path.GetFileName(_projectRoot)),
            ],
        };

        var result = await _requests.InitializeAsync(initParams, cancellationToken);
        _serverCapabilities = result.Capabilities;
        _serverStarted = true;

        // Send initialized notification
        await _process.SendNotificationAsync("initialized", new { });

        // Send workspace settings if provided
        if (workspaceSettings is not null)
        {
            await _notifications.DidChangeConfigurationAsync(
                new DidChangeConfigurationParams { Settings = workspaceSettings });
        }

        _logger.LogInformation("Language server [{Language}] initialized successfully", _language);
    }

    /// <summary>
    /// Configures default handlers for common server-initiated requests/notifications.
    /// Called before StartListening() via the configureRpc callback.
    /// Roslyn LS requires workspace/configuration and window/workDoneProgress/create.
    /// </summary>
    private static void ConfigureDefaultHandlers(JsonRpc rpc)
    {
        // Roslyn LS sends workspace/configuration with a list of items.
        // We must return one result per requested item (null = use defaults).
        rpc.AddLocalRpcMethod("workspace/configuration", (JToken? paramsToken) =>
        {
            int itemCount = 0;
            if (paramsToken is JObject obj && obj["items"] is JArray items)
            {
                itemCount = items.Count;
            }

            var result = new object?[itemCount];
            return (object?)result;
        });

        rpc.AddLocalRpcMethod("window/workDoneProgress/create",
            (JToken? _) => (object?)null);

        rpc.AddLocalRpcMethod("client/registerCapability",
            (JToken? _) => (object?)null);
    }

    /// <summary>
    /// Opens a file in the language server and returns the file buffer.
    /// </summary>
    public async Task<LspFileBuffer> OpenFileAsync(string absolutePath)
    {
        string uri = PathToUri(absolutePath);
        string languageId = _language.ToIdentifier();

        var buffer = _fileBuffers.OpenFile(absolutePath, uri, languageId, _encoding);

        if (!buffer.IsOpenInLs)
        {
            await _notifications.DidOpenTextDocumentAsync(new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem(uri, languageId, 0, buffer.Contents),
            });
            buffer.MarkOpenInLs();
        }

        return buffer;
    }

    /// <summary>
    /// Closes a file in the language server.
    /// </summary>
    public async Task CloseFileAsync(string absolutePath)
    {
        string uri = PathToUri(absolutePath);
        if (_fileBuffers.CloseFile(uri))
        {
            await _notifications.DidCloseTextDocumentAsync(new DidCloseTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
            });
        }
    }

    /// <summary>
    /// Sends a didChange notification to the language server with full document sync.
    /// </summary>
    public async Task NotifyFileChangedAsync(string absolutePath, string newContent)
    {
        string uri = PathToUri(absolutePath);
        var buffer = _fileBuffers.GetBuffer(uri);
        if (buffer is null)
        {
            return;
        }

        buffer.Contents = newContent;
        int version = buffer.IncrementVersion();

        await _notifications.DidChangeTextDocumentAsync(new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier(uri, version ),
            ContentChanges = [new TextDocumentContentChangeEvent { Text = newContent }],
        });
    }

    /// <summary>
    /// Requests document symbols for a file.
    /// </summary>
    public async Task<IReadOnlyList<UnifiedSymbolInformation>> RequestDocumentSymbolsAsync(
        string absolutePath, CancellationToken ct = default)
    {
        string uri = PathToUri(absolutePath);
        string? relativePath = GetRelativePath(absolutePath);

        var result = await _requests.DocumentSymbolAsync(
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
            }, ct);

        if (result is null || result.Type == JTokenType.Null)
        {
            return [];
        }

        return ParseDocumentSymbolResponse(result, uri, relativePath);
    }

    /// <summary>
    /// Requests the definition of the symbol at a given position.
    /// </summary>
    public async Task<IReadOnlyList<Location>> RequestDefinitionAsync(
        string absolutePath, int line, int character, CancellationToken ct = default)
    {
        string uri = PathToUri(absolutePath);

        var result = await _requests.DefinitionAsync(
            new DefinitionParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
                Position = new Position(line, character ),
            }, ct);

        return ParseLocationResponse(result);
    }

    /// <summary>
    /// Requests implementations of the symbol at a given position.
    /// </summary>
    public async Task<IReadOnlyList<Location>> RequestImplementationAsync(
        string absolutePath, int line, int character, CancellationToken ct = default)
    {
        string uri = PathToUri(absolutePath);

        var result = await _requests.ImplementationAsync(
            new ImplementationParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
                Position = new Position(line, character ),
            }, ct);

        return ParseLocationResponse(result);
    }

    /// <summary>
    /// Requests all references to the symbol at a given position.
    /// </summary>
    public async Task<IReadOnlyList<Location>> RequestReferencesAsync(
        string absolutePath, int line, int character,
        bool includeDeclaration = true, CancellationToken ct = default)
    {
        string uri = PathToUri(absolutePath);

        var result = await _requests.ReferencesAsync(
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
                Position = new Position(line, character ),
                Context = new ReferenceContext(includeDeclaration ),
            }, ct);

        if (result is null)
        {
            return [];
        }

        return result;
    }

    /// <summary>
    /// Requests hover information at a given position.
    /// </summary>
    public async Task<Hover?> RequestHoverAsync(
        string absolutePath, int line, int character, CancellationToken ct = default)
    {
        string uri = PathToUri(absolutePath);

        return await _requests.HoverAsync(
            new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
                Position = new Position(line, character ),
            }, ct);
    }

    /// <summary>
    /// Requests a workspace-wide rename of the symbol at a given position.
    /// </summary>
    public async Task<WorkspaceEdit?> RequestRenameAsync(
        string absolutePath, int line, int character, string newName,
        CancellationToken ct = default)
    {
        string uri = PathToUri(absolutePath);

        return await _requests.RenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier(uri ),
                Position = new Position(line, character ),
                NewName = newName,
            }, ct);
    }

    /// <summary>
    /// Requests workspace symbols matching a query string.
    /// </summary>
    public async Task<IReadOnlyList<SymbolInformation>> RequestWorkspaceSymbolAsync(
        string query, CancellationToken ct = default)
    {
        var result = await _requests.WorkspaceSymbolAsync(
            new WorkspaceSymbolParams { Query = query }, ct);

        return result ?? [];
    }

    /// <summary>
    /// Shuts down and stops the language server.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_serverStarted)
        {
            return;
        }

        _logger.LogInformation("Stopping language server [{Language}]", _language);

        // Close all open buffers
        foreach (var buffer in _fileBuffers.AllBuffers.ToList())
        {
            if (buffer.IsOpenInLs)
            {
                try
                {
                    await _notifications.DidCloseTextDocumentAsync(new DidCloseTextDocumentParams
                    {
                        TextDocument = new TextDocumentIdentifier(buffer.Uri ),
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error closing buffer {Uri} during shutdown", buffer.Uri);
                }
            }
        }
        _fileBuffers.Clear();

        await _process.ShutdownAsync(ct);
        _serverStarted = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_serverStarted)
        {
            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during LspClient disposal");
            }
        }
        await _process.DisposeAsync();
    }

    // --- Utility Methods ---

    /// <summary>
    /// Converts a file path to a file:// URI.
    /// </summary>
    public static string PathToUri(string absolutePath)
    {
        string normalized = Path.GetFullPath(absolutePath).Replace('\\', '/');
        return normalized.StartsWith('/') ? $"file://{normalized}" : $"file:///{normalized}";
    }

    /// <summary>
    /// Converts a file:// URI back to a local path.
    /// </summary>
    public static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }
        string path = uri["file://".Length..];
        if (path.StartsWith('/') && path.Length > 2 && path[2] == ':')
        {
            path = path[1..]; // Remove leading / for Windows paths like /C:/...
        }
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private string? GetRelativePath(string absolutePath)
    {
        try
        {
            return Path.GetRelativePath(_projectRoot, absolutePath);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<UnifiedSymbolInformation> ParseDocumentSymbolResponse(
        JToken token, string uri, string? relativePath)
    {
        if (token is JArray arr && arr.Count > 0)
        {
            var first = arr[0];
            // DocumentSymbol has "range" and "selectionRange"; SymbolInformation has "location"
            if (first["selectionRange"] is not null)
            {
                var docSymbols = arr.ToObject<DocumentSymbol[]>();
                if (docSymbols is not null)
                {
                    return docSymbols
                        .Select(ds => UnifiedSymbolInformation.FromDocumentSymbol(ds, uri, relativePath))
                        .ToList();
                }
            }
            else if (first["location"] is not null)
            {
                var symbolInfos = arr.ToObject<SymbolInformation[]>();
                if (symbolInfos is not null)
                {
                    return symbolInfos
                        .Select(UnifiedSymbolInformation.FromSymbolInformation)
                        .ToList();
                }
            }
        }
        return [];
    }

    private static IReadOnlyList<Location> ParseLocationResponse(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return [];
        }

        // Single Location
        if (token is JObject obj && obj["uri"] is not null)
        {
            var loc = obj.ToObject<Location>();
            return loc is not null ? [loc] : [];
        }

        // Array of Location or LocationLink
        if (token is JArray arr)
        {
            var locations = new List<Location>();
            foreach (var item in arr)
            {
                if (item is JObject itemObj && itemObj["targetUri"] is not null)
                {
                    // LocationLink → convert to Location
                    var link = itemObj.ToObject<LocationLink>();
                    if (link is not null)
                    {
                        locations.Add(new Location(link.TargetUri, link.TargetRange));
                    }
                }
                else
                {
                    var loc = item.ToObject<Location>();
                    if (loc is not null)
                    {
                        locations.Add(loc);
                    }
                }
            }
            return locations;
        }

        return [];
    }
}
