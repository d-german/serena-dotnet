// Client Capabilities - The capabilities structure Serena sends during initialization
// Based on the Python InitializeParams/ClientCapabilities from lsp_types.py

using System.Text.Json.Serialization;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Protocol;

/// <summary>
/// Builds the full client capabilities object for the initialize request.
/// Serena needs a comprehensive capabilities declaration to get full responses from language servers.
/// </summary>
public static class ClientCapabilitiesFactory
{
    /// <summary>
    /// Creates the default capabilities matching what Python Serena sends to language servers.
    /// Roslyn LS in particular requires specific capability structures.
    /// </summary>
    public static object CreateDefaultCapabilities()
    {
        return new
        {
            textDocument = new
            {
                synchronization = new
                {
                    dynamicRegistration = true,
                    willSave = true,
                    willSaveWaitUntil = true,
                    didSave = true,
                },
                completion = new
                {
                    dynamicRegistration = true,
                    completionItem = new
                    {
                        snippetSupport = false,
                        commitCharactersSupport = true,
                        documentationFormat = new[] { MarkupKind.Markdown, MarkupKind.PlainText },
                        deprecatedSupport = true,
                        preselectSupport = true,
                        labelDetailsSupport = true,
                    },
                    contextSupport = true,
                },
                hover = new
                {
                    dynamicRegistration = true,
                    contentFormat = new[] { MarkupKind.Markdown, MarkupKind.PlainText },
                },
                signatureHelp = new
                {
                    dynamicRegistration = true,
                    signatureInformation = new
                    {
                        documentationFormat = new[] { MarkupKind.Markdown, MarkupKind.PlainText },
                        parameterInformation = new { labelOffsetSupport = true },
                    },
                },
                definition = new { dynamicRegistration = true },
                typeDefinition = new { dynamicRegistration = true },
                implementation = new { dynamicRegistration = true },
                references = new { dynamicRegistration = true },
                documentHighlight = new { dynamicRegistration = true },
                documentSymbol = new
                {
                    dynamicRegistration = true,
                    symbolKind = new
                    {
                        valueSet = Enumerable.Range(1, 26).ToArray(),
                    },
                    hierarchicalDocumentSymbolSupport = true,
                    labelSupport = true,
                },
                codeAction = new
                {
                    dynamicRegistration = true,
                    codeActionLiteralSupport = new
                    {
                        codeActionKind = new
                        {
                            valueSet = new[]
                            {
                                CodeActionKind.QuickFix,
                                CodeActionKind.Refactor,
                                CodeActionKind.RefactorExtract,
                                CodeActionKind.RefactorInline,
                                CodeActionKind.RefactorRewrite,
                                CodeActionKind.Source,
                                CodeActionKind.SourceOrganizeImports,
                            },
                        },
                    },
                    isPreferredSupport = true,
                    resolveSupport = new { properties = new[] { "edit" } },
                },
                formatting = new { dynamicRegistration = true },
                rangeFormatting = new { dynamicRegistration = true },
                rename = new
                {
                    dynamicRegistration = true,
                    prepareSupport = true,
                },
                foldingRange = new { dynamicRegistration = true },
                selectionRange = new { dynamicRegistration = true },
                callHierarchy = new { dynamicRegistration = true },
                typeHierarchy = new { dynamicRegistration = true },
                publishDiagnostics = new
                {
                    relatedInformation = true,
                    tagSupport = new { valueSet = new[] { 1, 2 } },
                    versionSupport = true,
                },
            },
            workspace = new
            {
                applyEdit = true,
                workspaceEdit = new
                {
                    documentChanges = true,
                },
                didChangeConfiguration = new { dynamicRegistration = true },
                didChangeWatchedFiles = new { dynamicRegistration = true },
                symbol = new
                {
                    dynamicRegistration = true,
                    symbolKind = new
                    {
                        valueSet = Enumerable.Range(1, 26).ToArray(),
                    },
                },
                executeCommand = new { dynamicRegistration = true },
                workspaceFolders = true,
                configuration = true,
                workDoneProgress = true,
            },
            window = new
            {
                workDoneProgress = true,
                showMessage = new
                {
                    messageActionItem = new { additionalPropertiesSupport = true },
                },
                showDocument = new { support = true },
            },
            general = new
            {
                positionEncodings = new[] { "utf-16" },
            },
        };
    }
}
