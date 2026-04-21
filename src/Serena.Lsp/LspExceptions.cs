// LSP Exception types - Ported from solidlsp/ls_exceptions.py

namespace Serena.Lsp;

/// <summary>
/// Base exception for all SolidLSP/Serena LSP client errors.
/// Ported from solidlsp/ls_exceptions.py SolidLSPException.
/// </summary>
public class LspClientException : Exception
{
    public Exception? Cause { get; }

    public LspClientException(string message, Exception? cause = null)
        : base(message, cause)
    {
        Cause = cause;
    }

    public LspClientException(string message, Language language, Exception? cause = null)
        : base(message, cause)
    {
        Cause = cause;
        AffectedLanguage = language;
    }

    public Language? AffectedLanguage { get; }


    public bool IsLanguageServerTerminated =>
        Cause is LanguageServerTerminatedException;

    public Language? GetAffectedLanguage() =>
        Cause is LanguageServerTerminatedException terminated
            ? terminated.Language
            : null;

    public override string ToString()
    {
        string s = base.Message;
        if (Cause is not null)
        {
            s += s.Contains('\n') ? "\n" : " ";
            s += $"(caused by {Cause})";
        }
        return s;
    }
}

/// <summary>
/// Raised when a language server process terminates unexpectedly.
/// </summary>
public sealed class LanguageServerTerminatedException : Exception
{
    public Language Language { get; }
    public int? ExitCode { get; }

    public LanguageServerTerminatedException(Language language, int? exitCode = null, string? message = null)
        : base(message ?? $"Language server for {language} terminated unexpectedly (exit code: {exitCode})")
    {
        Language = language;
        ExitCode = exitCode;
    }

    public LanguageServerTerminatedException(string message, Language language, Exception? innerException = null)
        : base(message, innerException)
    {
        Language = language;
    }
}
