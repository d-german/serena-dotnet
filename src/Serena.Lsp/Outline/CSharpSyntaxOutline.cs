// Outlines a C# file from its syntax tree alone: no workspace, no language server,
// no project load. Produces the same symbol shape Roslyn's language server returns
// for textDocument/documentSymbol (names with type parameters, "name : type" and
// "name(params) : return" details, constructors as methods, fields as their
// declarators, overload indices by document order), so an index built this way is
// interchangeable with one built through the server. Parsing a file takes
// milliseconds and never has a Loading or Partial state, which is what lets an
// edit refresh its outline synchronously and `project index` run without Roslyn's
// workspace. What a syntax tree cannot know (which overload a call resolves to,
// true references) stays with the language server.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;
using LspLocation = Serena.Lsp.Protocol.Types.Location;
using SymbolKind = Serena.Lsp.Protocol.Types.SymbolKind;

namespace Serena.Lsp.Outline;

public static class CSharpSyntaxOutline
{
    /// <summary>
    /// The symbols a modern .NET build defines, which is how Roslyn's server outlines a
    /// file inside a loaded net10 project: DEBUG, the framework family and the
    /// "or greater" ladder.
    /// </summary>
    public static readonly IReadOnlyList<string> ModernSymbols = [.. BuildModernSymbols()];

    /// <summary>The symbols a .NET Framework 4.8 build defines, for the second outline of a multi-targeting file.</summary>
    public static readonly IReadOnlyList<string> FrameworkSymbols = [.. BuildFrameworkSymbols()];

    private static readonly CSharpParseOptions ModernOptions = Options(ModernSymbols);
    private static readonly CSharpParseOptions FrameworkOptions = Options(FrameworkSymbols);

    /// <summary>
    /// Outlines <paramref name="text"/> as the file at <paramref name="absolutePath"/>.
    /// A file whose conditionals test target-framework symbols is outlined under both
    /// symbol sets and the two are merged, as the server path does.
    /// </summary>
    public static IReadOnlyList<UnifiedSymbolInformation> Build(string absolutePath, string text)
    {
        string uri = LspClient.PathToUri(absolutePath);
        var primary = Parse(text, uri, ModernOptions);
        if (!FrameworkConditionalSymbols.HasFrameworkConditional(text))
        {
            return primary;
        }
        var framework = Parse(text, uri, FrameworkOptions);
        return FrameworkConditionalSymbols.Merge(primary, framework);
    }

    /// <summary>Reads and outlines the file; null when it cannot be read.</summary>
    public static IReadOnlyList<UnifiedSymbolInformation>? TryBuild(string absolutePath)
    {
        try
        {
            return File.Exists(absolutePath) ? Build(absolutePath, File.ReadAllText(absolutePath)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static List<UnifiedSymbolInformation> Parse(string text, string uri, CSharpParseOptions options)
    {
        var tree = CSharpSyntaxTree.ParseText(text, options);
        var root = tree.GetCompilationUnitRoot();
        var builder = new Builder(tree, uri);
        var symbols = new List<UnifiedSymbolInformation>();
        foreach (var member in root.Members)
        {
            builder.Add(member, null, symbols);
        }
        UnifiedSymbolInformation.AssignOverloadIndices(symbols);
        return symbols;
    }

    private sealed class Builder(SyntaxTree tree, string uri)
    {
        public void Add(MemberDeclarationSyntax member, UnifiedSymbolInformation? parent, List<UnifiedSymbolInformation> into)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    {
                        string name = ns.Name.ToString();
                        var symbol = Symbol(name, SymbolKind.Namespace, name, ns.Span, ns.Name.Span, parent);
                        AddMembers(ns.Members, symbol);
                        into.Add(symbol);
                        break;
                    }
                case TypeDeclarationSyntax type:
                    {
                        string name = type.Identifier.ValueText + TypeParameters(type.TypeParameterList);
                        var kind = type switch
                        {
                            InterfaceDeclarationSyntax => SymbolKind.Interface,
                            StructDeclarationSyntax => SymbolKind.Struct,
                            RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => SymbolKind.Struct,
                            _ => SymbolKind.Class,
                        };
                        var symbol = Symbol(name, kind, name, type.Span, type.Identifier.Span, parent);
                        AddMembers(type.Members, symbol);
                        into.Add(symbol);
                        break;
                    }
                case EnumDeclarationSyntax @enum:
                    {
                        string name = @enum.Identifier.ValueText;
                        var symbol = Symbol(name, SymbolKind.Enum, name, @enum.Span, @enum.Identifier.Span, parent);
                        foreach (var value in @enum.Members)
                        {
                            symbol.Children.Add(Symbol(value.Identifier.ValueText, SymbolKind.EnumMember, value.Identifier.ValueText, value.Span, value.Identifier.Span, symbol));
                        }
                        into.Add(symbol);
                        break;
                    }
                case DelegateDeclarationSyntax @delegate:
                    {
                        string name = @delegate.Identifier.ValueText + TypeParameters(@delegate.TypeParameterList);
                        into.Add(Symbol(name, SymbolKind.Class, $"{name}({Parameters(@delegate.ParameterList)}) : {@delegate.ReturnType}", @delegate.Span, @delegate.Identifier.Span, parent));
                        break;
                    }
                case MethodDeclarationSyntax method:
                    {
                        string name = method.Identifier.ValueText + TypeParameters(method.TypeParameterList);
                        var symbol = Symbol(name, SymbolKind.Method, $"{name}({Parameters(method.ParameterList)}) : {method.ReturnType}", method.Span, method.Identifier.Span, parent);
                        AddLocalFunctions(method, symbol);
                        into.Add(symbol);
                        break;
                    }
                case ConstructorDeclarationSyntax ctor:
                    {
                        // Roslyn's server reports constructors as methods named after the type, with no return type.
                        string name = ctor.Identifier.ValueText;
                        var symbol = Symbol(name, SymbolKind.Method, $"{name}({Parameters(ctor.ParameterList)})", ctor.Span, ctor.Identifier.Span, parent);
                        AddLocalFunctions(ctor, symbol);
                        into.Add(symbol);
                        break;
                    }
                case DestructorDeclarationSyntax dtor:
                    {
                        string name = "~" + dtor.Identifier.ValueText;
                        var symbol = Symbol(name, SymbolKind.Method, $"{name}()", dtor.Span, dtor.Identifier.Span, parent);
                        AddLocalFunctions(dtor, symbol);
                        into.Add(symbol);
                        break;
                    }
                case PropertyDeclarationSyntax property:
                    {
                        string name = property.Identifier.ValueText;
                        var symbol = Symbol(name, SymbolKind.Property, $"{name} : {property.Type}", property.Span, property.Identifier.Span, parent);
                        AddLocalFunctions(property, symbol);
                        into.Add(symbol);
                        break;
                    }
                case IndexerDeclarationSyntax indexer:
                    {
                        string name = $"this[{Parameters(indexer.ParameterList)}]";
                        var symbol = Symbol(name, SymbolKind.Property, $"{name} : {indexer.Type}", indexer.Span, indexer.ThisKeyword.Span, parent);
                        AddLocalFunctions(indexer, symbol);
                        into.Add(symbol);
                        break;
                    }
                case EventDeclarationSyntax @event:
                    {
                        string name = @event.Identifier.ValueText;
                        into.Add(Symbol(name, SymbolKind.Event, $"{name} : {@event.Type}", @event.Span, @event.Identifier.Span, parent));
                        break;
                    }
                case EventFieldDeclarationSyntax eventField:
                    AddDeclarators(eventField.Declaration, SymbolKind.Event, parent, into);
                    break;
                case FieldDeclarationSyntax field:
                    {
                        var kind = field.Modifiers.Any(SyntaxKind.ConstKeyword) ? SymbolKind.Constant : SymbolKind.Field;
                        AddDeclarators(field.Declaration, kind, parent, into);
                        break;
                    }
                case OperatorDeclarationSyntax op:
                    {
                        string name = "operator " + op.OperatorToken.Text;
                        var symbol = Symbol(name, SymbolKind.Operator, $"{name}({Parameters(op.ParameterList)}) : {op.ReturnType}", op.Span, op.OperatorToken.Span, parent);
                        AddLocalFunctions(op, symbol);
                        into.Add(symbol);
                        break;
                    }
                case ConversionOperatorDeclarationSyntax conversion:
                    {
                        string name = $"{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}";
                        var symbol = Symbol(name, SymbolKind.Operator, $"{name}({Parameters(conversion.ParameterList)})", conversion.Span, conversion.Type.Span, parent);
                        AddLocalFunctions(conversion, symbol);
                        into.Add(symbol);
                        break;
                    }
                default:
                    // Global statements, incomplete members and anything new in the language stay out of the outline.
                    break;
            }
        }

        // The server lists a member's local functions as its children, nested local functions under theirs.
        private void AddLocalFunctions(SyntaxNode member, UnifiedSymbolInformation parent)
        {
            foreach (var local in member.DescendantNodes(node => node == member || node is not (LocalFunctionStatementSyntax or MemberDeclarationSyntax))
                         .OfType<LocalFunctionStatementSyntax>())
            {
                string name = local.Identifier.ValueText + TypeParameters(local.TypeParameterList);
                var symbol = Symbol(name, SymbolKind.Method, $"{name}({Parameters(local.ParameterList)}) : {local.ReturnType}", local.Span, local.Identifier.Span, parent);
                AddLocalFunctions(local, symbol);
                parent.Children.Add(symbol);
            }
            UnifiedSymbolInformation.AssignOverloadIndices(parent.Children);
        }

        private void AddMembers(SyntaxList<MemberDeclarationSyntax> members, UnifiedSymbolInformation parent)
        {
            foreach (var member in members)
            {
                Add(member, parent, parent.Children);
            }
            UnifiedSymbolInformation.AssignOverloadIndices(parent.Children);
        }

        // Roslyn's server reports each declarator of a field or event field as its own symbol
        // whose range is the declarator, not the whole declaration.
        private void AddDeclarators(VariableDeclarationSyntax declaration, SymbolKind kind, UnifiedSymbolInformation? parent, List<UnifiedSymbolInformation> into)
        {
            foreach (var declarator in declaration.Variables)
            {
                string name = declarator.Identifier.ValueText;
                into.Add(Symbol(name, kind, $"{name} : {declaration.Type}", declarator.Span, declarator.Identifier.Span, parent));
            }
        }

        private UnifiedSymbolInformation Symbol(string name, SymbolKind kind, string detail, TextSpan body, TextSpan selection, UnifiedSymbolInformation? parent)
        {
            var range = Range(body);
            return new UnifiedSymbolInformation
            {
                Name = name,
                Kind = kind,
                Detail = detail,
                BodyRange = range,
                SelectionRange = Range(selection),
                Location = new LspLocation(uri, range),
                Parent = parent,
            };
        }

        private LspRange Range(TextSpan span)
        {
            var lines = tree.GetLineSpan(span);
            return new LspRange(
                new Position(lines.StartLinePosition.Line, lines.StartLinePosition.Character),
                new Position(lines.EndLinePosition.Line, lines.EndLinePosition.Character));
        }
    }

    private static string TypeParameters(TypeParameterListSyntax? list) =>
        list is null || list.Parameters.Count == 0
            ? string.Empty
            : "<" + string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText)) + ">";

    // Parameter types as the server shows them: modifiers that change the signature are kept,
    // the extension-method `this` is not, and names are left out.
    private static string Parameters(BaseParameterListSyntax? list)
    {
        if (list is null)
        {
            return string.Empty;
        }
        return string.Join(", ", list.Parameters.Select(parameter =>
        {
            var modifiers = parameter.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.ThisKeyword) && !m.IsKind(SyntaxKind.ScopedKeyword))
                .Select(m => m.Text + " ");
            return string.Concat(modifiers) + (parameter.Type?.ToString() ?? "?");
        }));
    }

    private static CSharpParseOptions Options(IEnumerable<string> symbols) =>
        new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular, symbols);

    private static IEnumerable<string> BuildModernSymbols()
    {
        yield return "DEBUG";
        yield return "TRACE";
        yield return "NET";
        yield return "NETCOREAPP";
        yield return "NET10_0";
        foreach (string version in new[] { "1_0", "1_1", "2_0", "2_1", "2_2", "3_0", "3_1" })
        {
            yield return $"NETCOREAPP{version}_OR_GREATER";
        }
        for (int major = 5; major <= 10; major++)
        {
            yield return $"NET{major}_0_OR_GREATER";
        }
    }

    private static IEnumerable<string> BuildFrameworkSymbols()
    {
        yield return "DEBUG";
        yield return "TRACE";
        yield return "NETFRAMEWORK";
        yield return "NET48";
        foreach (string version in new[] { "20", "30", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48" })
        {
            yield return $"NET{version}_OR_GREATER";
        }
    }
}
