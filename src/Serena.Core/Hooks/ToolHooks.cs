// PreToolUse hook infrastructure – allows cross-cutting checks before tool execution.

namespace Serena.Core.Hooks;

/// <summary>
/// Outcome of a pre-tool-use hook evaluation.
/// </summary>
public enum HookResult
{
    /// <summary>Tool execution proceeds normally.</summary>
    Allow,

    /// <summary>Tool execution is blocked; the hook supplies an error message.</summary>
    Deny,

    /// <summary>Tool execution proceeds but the hook's message is prepended to the result.</summary>
    AllowWithMessage,
}

/// <summary>
/// A hook that runs before a tool is executed.
/// Implementations can inspect the tool name and parameters to allow, deny, or annotate execution.
/// </summary>
public interface IPreToolUseHook
{
    /// <summary>
    /// Evaluates whether the tool invocation should proceed.
    /// </summary>
    Task<(HookResult Result, string? Message)> OnBeforeToolUseAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct = default);
}

/// <summary>
/// Reminds the agent to prefer symbolic editing tools over raw file-based edits.
/// Returns <see cref="HookResult.AllowWithMessage"/> for file-level edit tools.
/// </summary>
public sealed class SymbolicToolReminderHook : IPreToolUseHook
{
    private static readonly HashSet<string> s_fileEditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "replace_content",
        "create_text_file",
    };

    private const string ReminderMessage =
        "[Hook] Reminder: Prefer symbolic editing tools (replace_symbol_body, insert_before_symbol, " +
        "insert_after_symbol, rename_symbol, safe_delete_symbol) over file-based edits when working " +
        "with code. Symbolic tools are safer and produce more precise changes.";

    public Task<(HookResult Result, string? Message)> OnBeforeToolUseAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct = default)
    {
        if (s_fileEditTools.Contains(toolName))
        {
            return Task.FromResult<(HookResult, string?)>((HookResult.AllowWithMessage, ReminderMessage));
        }

        return Task.FromResult<(HookResult, string?)>((HookResult.Allow, null));
    }
}

/// <summary>
/// Permissive hook that always allows tool execution without any message.
/// </summary>
public sealed class AutoApproveHook : IPreToolUseHook
{
    public Task<(HookResult Result, string? Message)> OnBeforeToolUseAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct = default)
    {
        return Task.FromResult<(HookResult, string?)>((HookResult.Allow, null));
    }
}
