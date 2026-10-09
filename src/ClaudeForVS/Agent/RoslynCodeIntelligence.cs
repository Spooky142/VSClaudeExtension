using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace ClaudeForVS.Agent;

/// <summary>The type or member the caret is in.</summary>
/// <param name="Kind">e.g. "method" or "class".</param>
/// <param name="StartLine">1-based.</param>
internal sealed record EnclosingMember(string Name, string Kind, int StartLine, int EndLine, SourceText Source, TextSpan Span, bool HasDocumentation)
{
    /// <summary>The member's code. Only copied out when asked for, since the light bulb looks for a member on every caret move.</summary>
    public string Text => this.Source.ToString(this.Span);
}

/// <summary>
/// <see cref="ICodeIntelligence"/> on Roslyn. Takes the solution from a function, so Visual Studio supplies its live
/// workspace and tests supply an in-memory one.
/// </summary>
internal sealed class RoslynCodeIntelligence(Func<Solution> solution) : ICodeIntelligence
{
    private const int MaxLineChars = 200;

    private static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    // "Order.Total()": enough to say which symbol, short enough for a title.
    private static readonly SymbolDisplayFormat ShortName = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType);

    private static readonly SymbolDisplayFormat OutlineSignature = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public async Task<string> GoToDefinitionAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken)
    {
        (ISymbol symbol, _) = await this.ResolveAsync(site, repoRoot, cancellationToken).ConfigureAwait(false);
        symbol = symbol.OriginalDefinition;

        var sb = new StringBuilder();
        sb.Append($"{KindOf(symbol)} {symbol.ToDisplayString(Signature)}\n");
        List<Location> sources = symbol.Locations.Where(l => l.IsInSource).ToList();
        if (sources.Count == 0)
        {
            string assembly = symbol.ContainingAssembly?.Name ?? "a library";
            sb.Append($"Defined in {assembly} (compiled code; there's no source in this solution).\n");
        }

        int shown = 0;
        foreach (Location location in sources)
        {
            FileLinePositionSpan span = location.GetLineSpan();
            if (!canRead(span.Path))
            {
                continue;
            }

            int line = span.StartLinePosition.Line + 1;
            sb.Append($"Defined at {DisplayPath(repoRoot, span.Path)}:{line}\n");
            sb.Append($"  {line}: {LineText(await location.SourceTree!.GetTextAsync(cancellationToken).ConfigureAwait(false), line)}\n");
            shown++;
        }

        if (sources.Count > 0 && shown == 0)
        {
            sb.Append("The source files are restricted by your permission settings.\n");
        }

        string? summary = Summary(symbol, cancellationToken);
        if (summary != null)
        {
            sb.Append($"Documentation: {summary}\n");
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> FindReferencesAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken)
    {
        (ISymbol symbol, _) = await this.ResolveAsync(site, repoRoot, cancellationToken).ConfigureAwait(false);
        Solution current = solution();
        IEnumerable<ReferencedSymbol> found = await SymbolFinder.FindReferencesAsync(symbol, current, cancellationToken).ConfigureAwait(false);

        var rows = new SortedDictionary<(string Path, int Line), string>(Comparer<(string Path, int Line)>.Create(
            (a, b) => a.Path != b.Path ? string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) : a.Line.CompareTo(b.Line)));
        int hidden = 0;
        foreach (ReferencedSymbol referenced in found)
        {
            foreach (ReferenceLocation reference in referenced.Locations)
            {
                FileLinePositionSpan span = reference.Location.GetLineSpan();
                if (reference.Document.FilePath is not string path || !canRead(path))
                {
                    hidden++;
                    continue;
                }

                int line = span.StartLinePosition.Line + 1;
                if (!rows.ContainsKey((path, line)))
                {
                    SourceText text = await reference.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    rows[(path, line)] = LineText(text, line);
                }
            }
        }

        string name = symbol.ToDisplayString(Signature);
        if (rows.Count == 0)
        {
            return $"No references to {name} found" + (hidden > 0 ? $" in files you can read ({hidden} are in files restricted by your permission settings)." : ".");
        }

        int files = rows.Keys.Select(k => k.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var sb = new StringBuilder();
        sb.Append($"{(rows.Count == 1 ? "1 line" : $"{rows.Count} lines")} in {(files == 1 ? "1 file" : $"{files} files")} refer to {KindOf(symbol)} {name}:\n");
        foreach (KeyValuePair<(string Path, int Line), string> row in rows.Take(maxResults))
        {
            sb.Append($"{DisplayPath(repoRoot, row.Key.Path)}:{row.Key.Line}: {row.Value}\n");
        }

        if (rows.Count > maxResults)
        {
            sb.Append($"[{rows.Count - maxResults} more not shown; raise max_results or ask about a narrower symbol.]\n");
        }

        if (hidden > 0)
        {
            sb.Append($"[{hidden} references in files restricted by your permission settings are left out.]\n");
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> FindSymbolsAsync(string query, string kind, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken)
    {
        var found = new List<(ISymbol Symbol, string Path, int Line)>();
        var seen = new HashSet<(string, int, string)>();
        foreach (Project project in solution().Projects)
        {
            IEnumerable<ISymbol> symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, query, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false);
            foreach (ISymbol symbol in symbols)
            {
                if (!MatchesKind(symbol, kind) || symbol.Locations.FirstOrDefault(l => l.IsInSource) is not Location location)
                {
                    continue;
                }

                FileLinePositionSpan span = location.GetLineSpan();
                int line = span.StartLinePosition.Line + 1;

                // A project that targets several frameworks declares each symbol once per target.
                if (canRead(span.Path) && seen.Add((span.Path, line, symbol.Name)))
                {
                    found.Add((symbol, span.Path, line));
                }
            }
        }

        if (found.Count == 0)
        {
            return $"No {(kind.Length > 0 ? kind + " " : string.Empty)}symbols match \"{query}\".";
        }

        var ordered = found
            .OrderBy(f => f.Symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new StringBuilder();
        sb.Append($"{(ordered.Count == 1 ? "1 symbol matches" : $"{ordered.Count} symbols match")} \"{query}\":\n");
        foreach ((ISymbol symbol, string path, int line) in ordered.Take(maxResults))
        {
            sb.Append($"{KindOf(symbol)} {symbol.ToDisplayString(Signature)}  {DisplayPath(repoRoot, path)}:{line}\n");
        }

        if (ordered.Count > maxResults)
        {
            sb.Append($"[{ordered.Count - maxResults} more not shown; use a more specific query or raise max_results.]\n");
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> OutlineAsync(string fullPath, string repoRoot, CancellationToken cancellationToken)
    {
        Document document = this.FindDocument(fullPath, repoRoot);
        SemanticModel model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolInputException("This file has no semantic model (it isn't C# or Visual Basic).");
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) ?? throw new ToolInputException("This file couldn't be parsed.");
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder($"{DisplayPath(repoRoot, fullPath)}:\n");
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        int count = 0;
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            ISymbol? symbol = model.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is null || symbol.IsImplicitlyDeclared || !IsOutlined(symbol) || !seen.Add(symbol))
            {
                continue;
            }

            int depth = 0;
            for (INamedTypeSymbol? outer = symbol.ContainingType; outer != null; outer = outer.ContainingType)
            {
                depth++;
            }

            int line = text.Lines.GetLinePosition(node.SpanStart).Line + 1;
            sb.Append($"{line}: {new string(' ', depth * 2)}{symbol.ToDisplayString(OutlineSignature)}\n");
            count++;
        }

        return count == 0 ? $"{DisplayPath(repoRoot, fullPath)} declares no types or members." : sb.ToString().TrimEnd();
    }

    public async Task<RenamePlan> PlanRenameAsync(CodeSite site, string newName, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(newName, @"^@?[\p{L}_][\p{L}\p{N}_]*$"))
        {
            throw new ToolInputException($"\"{newName}\" isn't a valid identifier.");
        }

        (ISymbol symbol, _) = await this.ResolveAsync(site, null, cancellationToken).ConfigureAwait(false);
        symbol = symbol.OriginalDefinition;
        if (symbol.Name == newName.TrimStart('@'))
        {
            throw new ToolInputException($"{symbol.Name} already has that name.");
        }

        if (symbol.IsImplicitlyDeclared || !symbol.Locations.Any(l => l.IsInSource) || symbol.Locations.Any(l => !l.IsInSource))
        {
            throw new ToolInputException($"{symbol.Name} can't be renamed: it's declared in a library or generated by the compiler, not in this solution's own source.");
        }

        Solution original = solution();
        Solution renamed = await Renamer.RenameSymbolAsync(original, symbol, new SymbolRenameOptions(), newName, cancellationToken).ConfigureAwait(false);

        var files = new List<RenamedFile>();
        foreach (DocumentId id in renamed.GetChanges(original).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()).Distinct())
        {
            Document before = original.GetDocument(id)!;
            Document after = renamed.GetDocument(id)!;
            if (before.FilePath is not string path || files.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // a file shared by several targets of one project is changed once
            }

            string oldText = (await before.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            string newText = (await after.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            if (oldText != newText)
            {
                files.Add(new RenamedFile(path, oldText, newText));
            }
        }

        if (files.Count == 0)
        {
            throw new ToolInputException($"Renaming {symbol.Name} would change nothing.");
        }

        return new RenamePlan(symbol.ToDisplayString(ShortName), newName, files);
    }

    public async Task<FormatResult> FormatAsync(string fullPath, string expectedText, IReadOnlyList<(int Start, int End)> lines, CancellationToken cancellationToken)
    {
        Solution current = solution();
        if (lines.Count == 0 || FindId(current, fullPath) is not DocumentId id || current.GetDocument(id) is not Document document)
        {
            return new FormatResult(FormatStatus.NotInSolution);
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        string original = text.ToString();
        if (!string.Equals(original.Replace("\r\n", "\n"), expectedText.Replace("\r\n", "\n"), StringComparison.Ordinal))
        {
            return new FormatResult(FormatStatus.OutOfDate);
        }

        var spans = new List<TextSpan>();
        foreach ((int start, int end) in lines)
        {
            if (start < 1 || start > text.Lines.Count)
            {
                continue;
            }

            spans.Add(TextSpan.FromBounds(text.Lines[start - 1].Start, text.Lines[Math.Min(end, text.Lines.Count) - 1].End));
        }

        if (spans.Count == 0)
        {
            return new FormatResult(FormatStatus.AlreadyFormatted);
        }

        // The formatter writes the line ending it's configured for wherever it reflows a line, so a file that uses the
        // other kind would end up with both. A file that already mixes them is left alone.
        bool crlf = original.Contains("\r\n");
        if (crlf && Regex.IsMatch(original, "(?<!\r)\n"))
        {
            return new FormatResult(FormatStatus.Skipped);
        }

        Document formatted = await Formatter.FormatAsync(document, spans, null, cancellationToken).ConfigureAwait(false);
        string result = Regex.Replace((await formatted.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString(), "\r?\n", crlf ? "\r\n" : "\n");
        return result == original ? new FormatResult(FormatStatus.AlreadyFormatted)
            : StripWhitespace(result) != StripWhitespace(original) ? new FormatResult(FormatStatus.Skipped)
            : new FormatResult(FormatStatus.Formatted, result);
    }

    /// <summary>The type or member containing <paramref name="position"/>, or null if the caret isn't in one.</summary>
    public static async Task<EnclosingMember?> FindEnclosingMemberAsync(Document document, int position, CancellationToken cancellationToken)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        for (SyntaxNode? node = root.FindToken(Math.Min(position, root.FullSpan.End)).Parent; node != null; node = node.Parent)
        {
            ISymbol? symbol = model.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is null || symbol.IsImplicitlyDeclared || !IsOutlined(symbol))
            {
                continue;
            }

            int start = text.Lines.GetLinePosition(node.Span.Start).Line + 1;
            int end = text.Lines.GetLinePosition(node.Span.End).Line + 1;
            bool documented = !string.IsNullOrWhiteSpace(symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken));
            return new EnclosingMember(symbol.Name, KindOf(symbol), start, end, text, node.Span, documented);
        }

        return null;
    }

    private async Task<(ISymbol Symbol, Document Document)> ResolveAsync(CodeSite site, string? repoRoot, CancellationToken cancellationToken)
    {
        Document document = this.FindDocument(site.FullPath, repoRoot);
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        string shown = DisplayPath(repoRoot, site.FullPath);
        if (site.Line < 1 || site.Line > text.Lines.Count)
        {
            throw new ToolInputException($"{shown} has {text.Lines.Count} lines, so line {site.Line} doesn't exist.");
        }

        TextLine line = text.Lines[site.Line - 1];
        string name = Regex.Replace(site.Symbol, @"[(<].*$", string.Empty).Trim();
        name = name.Substring(name.LastIndexOf('.') + 1);
        foreach (Match match in Regex.Matches(line.ToString(), $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])"))
        {
            ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, line.Start + match.Index, cancellationToken).ConfigureAwait(false);
            if (symbol != null)
            {
                return (symbol, document);
            }
        }

        throw new ToolInputException($"Couldn't find a symbol named \"{name}\" on line {site.Line} of {shown}. That line reads: {LineText(text, site.Line)}");
    }

    private Document FindDocument(string fullPath, string? repoRoot)
    {
        Solution current = solution();
        return FindId(current, fullPath) is DocumentId id && current.GetDocument(id) is Document document
            ? document
            : throw new ToolInputException($"{DisplayPath(repoRoot, fullPath)} isn't part of the solution open in Visual Studio (or the solution is still loading), so there's no code model for it.");
    }

    /// <summary>The document for a file, matching the path the way Windows does (the case a caller typed may differ from the project's).</summary>
    private static DocumentId? FindId(Solution current, string fullPath)
    {
        ImmutableArray<DocumentId> ids = current.GetDocumentIdsWithFilePath(fullPath);
        if (!ids.IsEmpty)
        {
            return ids[0];
        }

        return current.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static bool IsOutlined(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => true,
        IMethodSymbol m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor or MethodKind.UserDefinedOperator or MethodKind.Conversion,
        IPropertySymbol or IFieldSymbol or IEventSymbol => true,
        _ => false,
    };

    private static bool MatchesKind(ISymbol symbol, string kind) => kind switch
    {
        "type" => symbol is INamedTypeSymbol,
        "method" => symbol is IMethodSymbol,
        "property" => symbol is IPropertySymbol,
        "field" => symbol is IFieldSymbol,
        "event" => symbol is IEventSymbol,
        _ => true,
    };

    internal static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
        IMethodSymbol => "method",
        IPropertySymbol property => property.IsIndexer ? "indexer" : "property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum member",
        IFieldSymbol field => field.IsConst ? "constant" : "field",
        IEventSymbol => "event",
        ILocalSymbol => "local variable",
        IParameterSymbol => "parameter",
        INamespaceSymbol => "namespace",
        ITypeParameterSymbol => "type parameter",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The first sentence or two of the symbol's XML documentation summary, or null.</summary>
    private static string? Summary(ISymbol symbol, CancellationToken cancellationToken)
    {
        string xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? string.Empty;
        Match match = Regex.Match(xml, @"<summary>(.*?)</summary>", RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        string text = Regex.Replace(Regex.Replace(match.Groups[1].Value, @"<[^>]+>", string.Empty), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text.Length > 400 ? text.Substring(0, 400) + "…" : text;
    }

    private static string StripWhitespace(string text) => Regex.Replace(text, @"\s+", string.Empty);

    private static string LineText(SourceText text, int line)
    {
        string value = line >= 1 && line <= text.Lines.Count ? text.Lines[line - 1].ToString().Trim() : string.Empty;
        return value.Length > MaxLineChars ? value.Substring(0, MaxLineChars) + "…" : value;
    }

    /// <summary>A path relative to the repository with forward slashes, or the full path for files outside it.</summary>
    private static string DisplayPath(string? repoRoot, string fullPath)
    {
        if (repoRoot != null)
        {
            string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length).Replace('\\', '/');
            }
        }

        return fullPath.Replace('\\', '/');
    }
}
