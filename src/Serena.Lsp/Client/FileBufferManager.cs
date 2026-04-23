// File Buffer Management - Ported from solidlsp/ls.py LSPFileBuffer
// Phase 3B: Open file tracking, content caching, versioning

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Client;

/// <summary>
/// Tracks an open file in the language server, caching its contents
/// and managing version numbers for didChange notifications.
/// Ported from solidlsp/ls.py LSPFileBuffer.
/// </summary>
public sealed class LspFileBuffer
{
    private string? _contents;
    private string? _contentHash;
    private DateTime? _lastReadTime;
    private int _version;

    public string AbsolutePath { get; }
    public string Uri { get; }
    public string LanguageId { get; }
    public string Encoding { get; }
    internal int _refCount;
    public int RefCount => _refCount;
    public bool IsOpenInLs { get; private set; }
    public int Version => _version;

    public LspFileBuffer(
        string absolutePath,
        string uri,
        string encoding,
        int version,
        string languageId,
        int refCount)
    {
        AbsolutePath = absolutePath;
        Uri = uri;
        Encoding = encoding;
        _version = version;
        LanguageId = languageId;
        _refCount = refCount;
    }

    /// <summary>
    /// Gets the file contents, re-reading from disk if the file has been modified.
    /// </summary>
    public string Contents
    {
        get
        {
            if (File.Exists(AbsolutePath))
            {
                var modifiedTime = File.GetLastWriteTimeUtc(AbsolutePath);
                if (_contents is not null && _lastReadTime.HasValue && modifiedTime > _lastReadTime.Value)
                {
                    _contents = null;
                }

                if (_contents is null)
                {
                    _lastReadTime = modifiedTime;
                    _contents = File.ReadAllText(AbsolutePath, System.Text.Encoding.GetEncoding(Encoding));
                    _contentHash = null;
                }
            }

            return _contents ?? string.Empty;
        }
        set
        {
            _contents = value;
            _contentHash = null;
        }
    }

    /// <summary>
    /// MD5 hash of the current contents.
    /// </summary>
    public string ContentHash
    {
        get
        {
            if (_contentHash is null)
            {
                byte[] bytes = System.Text.Encoding.GetEncoding(Encoding).GetBytes(Contents);
                _contentHash = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            }
            return _contentHash;
        }
    }

    /// <summary>
    /// Increments the version and returns the new value.
    /// </summary>
    public int IncrementVersion() => Interlocked.Increment(ref _version);

    /// <summary>
    /// Splits the contents into lines.
    /// </summary>
    public string[] SplitLines() => Contents.Split('\n');

    /// <summary>
    /// Marks this buffer as open in the language server.
    /// </summary>
    public void MarkOpenInLs() => IsOpenInLs = true;

    /// <summary>
    /// Marks this buffer as closed in the language server.
    /// </summary>
    public void MarkClosedInLs() => IsOpenInLs = false;
}

/// <summary>
/// Manages the collection of open file buffers, handling open/close ref counting.
/// </summary>
public sealed class FileBufferManager
{
    private readonly ConcurrentDictionary<string, LspFileBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the buffer for a file URI, or null if not tracked.
    /// </summary>
    public LspFileBuffer? GetBuffer(string uri) =>
        _buffers.GetValueOrDefault(uri);

    /// <summary>
    /// Gets or creates a buffer for a file.
    /// Atomic via GetOrAdd — concurrent callers for the same URI get the same buffer.
    /// </summary>
    public LspFileBuffer OpenFile(string absolutePath, string uri, string languageId, string encoding = "utf-8")
    {
        var buffer = _buffers.GetOrAdd(uri,
            _ => new LspFileBuffer(absolutePath, uri, encoding, 0, languageId, 0));
        Interlocked.Increment(ref buffer._refCount);
        return buffer;
    }

    /// <summary>
    /// Decrements ref count and removes buffer if it reaches zero.
    /// Returns true if the buffer was removed (should send didClose).
    /// </summary>
    public bool CloseFile(string uri)
    {
        if (!_buffers.TryGetValue(uri, out var buffer))
        {
            return false;
        }

        int newCount = Interlocked.Decrement(ref buffer._refCount);
        if (newCount <= 0)
        {
            _buffers.TryRemove(uri, out _);
            return true;
        }
        return false;
    }

    /// <summary>
    /// All currently tracked buffers.
    /// </summary>
    public IReadOnlyCollection<LspFileBuffer> AllBuffers => [.. _buffers.Values];

    /// <summary>
    /// Number of tracked files.
    /// </summary>
    public int Count => _buffers.Count;

    /// <summary>
    /// Clears all tracked buffers.
    /// </summary>
    public void Clear() => _buffers.Clear();
}

/// <summary>
/// Represents extracted text of a symbol's body from a file buffer.
/// Ported from solidlsp/ls.py SymbolBody.
/// </summary>
public sealed class SymbolBody
{
    private readonly string[] _lines;
    private readonly int _startLine;
    private readonly int _startCol;
    private readonly int _endLine;
    private readonly int _endCol;

    public SymbolBody(string[] lines, int startLine, int startCol, int endLine, int endCol)
    {
        _lines = lines;
        _startLine = startLine;
        _startCol = startCol;
        _endLine = endLine;
        _endCol = endCol;
    }

    /// <summary>
    /// Extracts the symbol text from the source lines.
    /// </summary>
    public string GetText()
    {
        if (_startLine == _endLine)
        {
            return _lines[_startLine][_startCol.._endCol];
        }

        var sb = new StringBuilder();
        // First line: from startCol to end
        sb.Append(_lines[_startLine][_startCol..]);

        // Middle lines: full content
        for (int i = _startLine + 1; i < _endLine; i++)
        {
            sb.Append('\n');
            sb.Append(_lines[i]);
        }

        // Last line: from start to endCol
        sb.Append('\n');
        sb.Append(_lines[_endLine][.._endCol]);

        return sb.ToString();
    }
}
