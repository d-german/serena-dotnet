// JSON-RPC and LSP error types - Ported from solidlsp/lsp_protocol_handler/server.py

using System.Text.Json.Serialization;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Protocol;

/// <summary>
/// Represents an error returned by the language server via the LSP protocol.
/// </summary>
public sealed class LspException : Exception
{
    public ErrorCodes Code { get; }

    public LspException(ErrorCodes code, string message)
        : base(message)
    {
        Code = code;
    }

    public Dictionary<string, object> ToLsp() => new()
    {
        ["code"] = (int)Code,
        ["message"] = Message,
    };

    public static LspException FromLsp(Dictionary<string, object> dict)
    {
        var code = (ErrorCodes)Convert.ToInt32(dict["code"]);
        var message = dict["message"]?.ToString() ?? "Unknown LSP error";
        return new LspException(code, message);
    }
}

/// <summary>
/// Information required to launch a language server process.
/// Ported from server.py ProcessLaunchInfo.
/// </summary>
public sealed record ProcessLaunchInfo
{
    /// <summary>
    /// The command used to launch the process. List form is preferred.
    /// </summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>
    /// The environment variables to set for the process.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The working directory for the process.
    /// </summary>
    public string WorkingDirectory { get; init; } = System.IO.Directory.GetCurrentDirectory();
}

/// <summary>
/// A JSON-RPC request message.
/// </summary>
public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public object? Params { get; init; }
}

/// <summary>
/// A JSON-RPC notification message (no id, no response expected).
/// </summary>
public sealed record JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public object? Params { get; init; }
}

/// <summary>
/// A JSON-RPC response message.
/// </summary>
public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("result")]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

/// <summary>
/// A JSON-RPC error object.
/// </summary>
public sealed record JsonRpcError
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}
