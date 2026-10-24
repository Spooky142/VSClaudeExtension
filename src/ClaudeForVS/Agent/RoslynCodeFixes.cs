using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ClaudeForVS.Agent;

/// <summary>The light bulb's code fixes: the errors and warnings on a line, the fixes offered for each, and what one would change.</summary>
internal sealed partial class RoslynCodeIntelligence
{
    private const int MaxFixesPerDiagnostic = 15;

    // The fixers each analyzer package contains, by the package's assembly; found once per assembly.
    private static readonly ConcurrentDictionary<Assembly, ImmutableArray<CodeFixProvider>> PackageFixers = new();

    public async Task<string> GetCodeFixesAsync(string fullPath, int line, string diagnosticId, string repoRoot, CancellationToken cancellationToken)
    {
        Document document = this.FindDocument(fullPath, repoRoot);
        string shown = $"{DisplayPath(repoRoot, fullPath)}:{line}";
        IReadOnlyList<Diagnostic> diagnostics = await DiagnosticsOnLineAsync(document, line, diagnosticId, repoRoot, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Count == 0)
        {
            return diagnosticId.Length > 0
                ? $"There's no {diagnosticId} on {shown}. (The code model may not have caught up with a change made a moment ago; check get_diagnostics.)"
                : $"There are no errors or warnings on {shown}.";
        }

        var sb = new StringBuilder($"{shown} has {(diagnostics.Count == 1 ? "1 diagnostic" : $"{diagnostics.Count} diagnostics")}:\n");
        bool anyFix = false;
        foreach (Diagnostic diagnostic in diagnostics)
        {
            sb.Append($"{Severity(diagnostic)} {diagnostic.Id}: {OneLine(diagnostic.GetMessage())}\n");
            IReadOnlyList<(string Title, CodeAction Action)> fixes = await this.FixesAsync(document, diagnostic, cancellationToken).ConfigureAwait(false);
            if (fixes.Count == 0)
            {
                sb.Append("  (no fixes offered)\n");
                continue;
            }

            anyFix = true;
            foreach ((string title, _) in fixes.Take(MaxFixesPerDiagnostic))
            {
                sb.Append($"  - {title}\n");
            }

            if (fixes.Count > MaxFixesPerDiagnostic)
            {
                sb.Append($"  [{fixes.Count - MaxFixesPerDiagnostic} more not shown]\n");
            }
        }

        if (anyFix)
        {
            sb.Append("Apply one with apply_code_fix, giving the diagnostic ID and the fix's title exactly as listed.");
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<CodeFixPlan> PlanCodeFixAsync(string fullPath, int line, string diagnosticId, string fixTitle, string repoRoot, CancellationToken cancellationToken)
    {
        Document document = this.FindDocument(fullPath, repoRoot);
        string shown = $"{DisplayPath(repoRoot, fullPath)}:{line}";
        IReadOnlyList<Diagnostic> diagnostics = await DiagnosticsOnLineAsync(document, line, diagnosticId, repoRoot, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Count == 0)
        {
            throw new ToolInputException($"There's no {diagnosticId} on {shown} any more, so nothing was changed. Check get_diagnostics, then get_code_fixes again.");
        }

        var offered = new List<string>();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            foreach ((string title, CodeAction action) in await this.FixesAsync(document, diagnostic, cancellationToken).ConfigureAwait(false))
            {
                if (SameTitle(title, fixTitle))
                {
                    IReadOnlyList<RenamedFile> files = await ChangesAsync(document.Project.Solution, action, cancellationToken).ConfigureAwait(false);
                    return new CodeFixPlan(diagnostic.Id, title, files);
                }

                offered.Add(title);
            }
        }

        throw new ToolInputException(offered.Count == 0
            ? $"No fixes are offered for {diagnosticId} on {shown}, so nothing was changed."
            : $"No fix called \"{fixTitle}\" is offered for {diagnosticId} on {shown}, so nothing was changed. The fixes are: {string.Join("; ", offered.Distinct().Select(t => $"\"{t}\""))}.");
    }

    /// <summary>The compiler's and the project's analyzers' errors, warnings and suggestions that touch a line, worst first.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> DiagnosticsOnLineAsync(Document document, int line, string diagnosticId, string repoRoot, CancellationToken cancellationToken)
    {
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new ToolInputException($"{DisplayPath(repoRoot, document.FilePath ?? document.Name)} has {text.Lines.Count} lines, so line {line} doesn't exist.");
        }

        SemanticModel model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ToolInputException("This file has no code model (it isn't C# or Visual Basic), so there are no code fixes for it.");
        TextSpan span = text.Lines[line - 1].Span;
        var found = new List<Diagnostic>(model.GetDiagnostics(span, cancellationToken));

        ImmutableArray<DiagnosticAnalyzer> analyzers = document.Project.AnalyzerReferences
            .SelectMany(r => Safe(() => r.GetAnalyzers(document.Project.Language), ImmutableArray<DiagnosticAnalyzer>.Empty))
            .ToImmutableArray();
        if (!analyzers.IsEmpty)
        {
            CompilationWithAnalyzers withAnalyzers = model.Compilation.WithAnalyzers(analyzers, document.Project.AnalyzerOptions);
            found.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancellationToken).ConfigureAwait(false));
            found.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, span, cancellationToken).ConfigureAwait(false));
        }

        return found
            .Where(d => d.Location.IsInSource && d.Location.SourceTree == model.SyntaxTree && Touches(d.Location.SourceSpan, span))
            .Where(d => d.Id != "AD0001") // an analyzer that crashed
            .Where(d => diagnosticId.Length == 0 ? d.Severity != DiagnosticSeverity.Hidden : d.Id.Equals(diagnosticId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => (d.Id, d.Location.SourceSpan, d.GetMessage()))
            .Select(g => g.First())
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ToList();
    }

    private static bool Touches(TextSpan diagnostic, TextSpan line) =>
        diagnostic.IntersectsWith(line) || (diagnostic.IsEmpty && diagnostic.Start >= line.Start && diagnostic.Start <= line.End);

    /// <summary>The fixes offered for a diagnostic, with nested ones (e.g. "Generate type › Generate class in new file") flattened.</summary>
    private async Task<IReadOnlyList<(string Title, CodeAction Action)>> FixesAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var actions = new List<CodeAction>();
        foreach (CodeFixProvider fixer in this.FixersFor(document.Project))
        {
            if (!Safe(() => fixer.FixableDiagnosticIds, ImmutableArray<string>.Empty).Contains(diagnostic.Id))
            {
                continue;
            }

            var found = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic, (action, _) => { lock (found) found.Add(action); }, cancellationToken);
            try
            {
                await fixer.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // a fixer that fails is left out, as the light bulb does
            }

            actions.AddRange(found);
        }

        var result = new List<(string, CodeAction)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(CodeAction action, string? parent)
        {
            string title = parent is null ? OneLine(action.Title) : $"{parent} › {OneLine(action.Title)}";
            if (!action.NestedActions.IsDefaultOrEmpty)
            {
                foreach (CodeAction nested in action.NestedActions)
                {
                    Add(nested, title);
                }
            }
            else if (seen.Add(title))
            {
                result.Add((title, action));
            }
        }

        foreach (CodeAction action in actions)
        {
            Add(action, null);
        }

        return result;
    }

    /// <summary>The fixers for a project's language: the host's, and those in the project's analyzer packages.</summary>
    private IEnumerable<CodeFixProvider> FixersFor(Project project)
    {
        foreach (CodeFixProvider fixer in this.HostFixers(project.Language))
        {
            yield return fixer;
        }

        foreach (AnalyzerReference reference in project.AnalyzerReferences)
        {
            if (LoadedAssembly(reference) is not Assembly assembly)
            {
                continue;
            }

            foreach (CodeFixProvider fixer in PackageFixers.GetOrAdd(assembly, FindFixers))
            {
                if (fixer.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } export && export.Languages.Contains(project.Language))
                {
                    yield return fixer;
                }
            }
        }
    }

    /// <summary>
    /// The assembly behind an analyzer reference, if it's already loaded (the analyzers were run from it). It's never
    /// loaded here: Visual Studio loads analyzer packages its own way, and a second copy would be a different assembly.
    /// </summary>
    private static Assembly? LoadedAssembly(AnalyzerReference reference)
    {
        if (reference is AnalyzerFileReference file)
        {
            return Safe(file.GetAssembly, null);
        }

        if (reference.FullPath is not string path || !File.Exists(path))
        {
            return null;
        }

        string? name = Safe(() => AssemblyName.GetAssemblyName(path).FullName, null);
        return name is null ? null : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == name);
    }

    private static ImmutableArray<CodeFixProvider> FindFixers(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        var fixers = ImmutableArray.CreateBuilder<CodeFixProvider>();
        foreach (Type type in types)
        {
            if (!type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type) && type.GetCustomAttribute<ExportCodeFixProviderAttribute>() != null
                && type.GetConstructor(Type.EmptyTypes) != null
                && Safe(() => (CodeFixProvider?)Activator.CreateInstance(type), null) is CodeFixProvider fixer)
            {
                fixers.Add(fixer);
            }
        }

        return fixers.ToImmutable();
    }

    /// <summary>The files a code fix changes or creates. Refuses fixes that need Visual Studio's own UI or change more than files.</summary>
    private static async Task<IReadOnlyList<RenamedFile>> ChangesAsync(Solution original, CodeAction action, CancellationToken cancellationToken)
    {
        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        List<ApplyChangesOperation> applies = operations.OfType<ApplyChangesOperation>().ToList();
        if (applies.Count != 1)
        {
            throw new ToolInputException(
                $"\"{action.Title}\" needs Visual Studio's own windows to finish (e.g. a dialog), so it can't be applied from the chat. Ask the user to choose it from the light bulb (Ctrl+.), or edit the code instead.");
        }

        Solution changed = applies[0].ChangedSolution;
        SolutionChanges changes = changed.GetChanges(original);
        if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
        {
            throw new ToolInputException($"\"{action.Title}\" adds or removes projects, so it can't be applied from the chat. Ask the user to choose it from the light bulb (Ctrl+.).");
        }

        var files = new List<RenamedFile>();
        foreach (ProjectChanges project in changes.GetProjectChanges())
        {
            if (project.GetAddedProjectReferences().Any() || project.GetRemovedProjectReferences().Any()
                || project.GetAddedMetadataReferences().Any() || project.GetRemovedMetadataReferences().Any()
                || project.GetAddedAnalyzerReferences().Any() || project.GetRemovedAnalyzerReferences().Any())
            {
                throw new ToolInputException(
                    $"\"{action.Title}\" changes {project.NewProject.Name}'s references, so it can't be applied from the chat. Ask the user to choose it from the light bulb (Ctrl+.), or use add_package for NuGet packages.");
            }

            if (project.GetRemovedDocuments().Any() || project.GetRemovedAdditionalDocuments().Any())
            {
                throw new ToolInputException($"\"{action.Title}\" deletes files, so it can't be applied from the chat. Ask the user to choose it from the light bulb (Ctrl+.).");
            }

            foreach (DocumentId id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                Document before = original.GetDocument(id)!;
                Document after = changed.GetDocument(id)!;
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

            foreach (DocumentId id in project.GetAddedDocuments())
            {
                Document added = changed.GetDocument(id)!;
                string path = added.FilePath ?? NewFilePath(project.NewProject, added)
                    ?? throw new ToolInputException($"\"{action.Title}\" creates a file Visual Studio hasn't given a place yet, so it can't be applied from the chat. Ask the user to choose it from the light bulb (Ctrl+.).");
                if (!files.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    files.Add(new RenamedFile(path, string.Empty, (await added.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString(), IsNew: true));
                }
            }

            if (project.GetChangedAdditionalDocuments().Any() || project.GetAddedAdditionalDocuments().Any())
            {
                throw new ToolInputException($"\"{action.Title}\" changes files that aren't code, so it can't be applied from the chat. Ask the user to choose it from the light bulb (Ctrl+.).");
            }
        }

        return files.Count > 0 ? files : throw new ToolInputException($"\"{action.Title}\" would change nothing.");
    }

    /// <summary>Where a new document goes: its folders under the project's folder, as Visual Studio puts it.</summary>
    private static string? NewFilePath(Project project, Document document)
    {
        if (Path.GetDirectoryName(project.FilePath ?? string.Empty) is not { Length: > 0 } folder)
        {
            return null;
        }

        string name = Path.GetFileName(document.Name);
        return name.Length == 0 ? null : Path.Combine(new[] { folder }.Concat(document.Folders).Concat([name]).ToArray());
    }

    private static string Severity(Diagnostic diagnostic) => diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info => "suggestion",
        _ => "hidden",
    };

    private static bool SameTitle(string a, string b) =>
        string.Equals(Regex.Replace(a, @"\s+", " ").Trim(), Regex.Replace(b, @"\s+", " ").Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(a.Replace(" › ", " > "), b.Replace(" › ", " > ").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>Calls into code from an analyzer package or the host, which may throw anything.</summary>
    private static T Safe<T>(Func<T> get, T fallback)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback;
        }
    }
}
