using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;

namespace ClaudeForVS;

/// <summary>
/// <see cref="ICodeIntelligence"/> on Visual Studio's live Roslyn workspace. The workspace is looked up when it's first
/// needed. This extension is built against one version of Roslyn and runs on whichever one the user's Visual Studio
/// has, so a mismatch is reported to Claude as a message rather than breaking the chat.
/// </summary>
internal sealed class VsCodeIntelligence(IComponentModel components) : ICodeIntelligence
{
    private readonly Lazy<RoslynCodeIntelligence> inner = new(() => Create(components), LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<string> GoToDefinitionAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken)
        => this.RunAsync(code => code.GoToDefinitionAsync(site, repoRoot, canRead, cancellationToken));

    public Task<string> FindReferencesAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken)
        => this.RunAsync(code => code.FindReferencesAsync(site, repoRoot, canRead, maxResults, cancellationToken));

    public Task<string> FindSymbolsAsync(string query, string kind, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken)
        => this.RunAsync(code => code.FindSymbolsAsync(query, kind, repoRoot, canRead, maxResults, cancellationToken));

    public Task<string> OutlineAsync(string fullPath, string repoRoot, CancellationToken cancellationToken)
        => this.RunAsync(code => code.OutlineAsync(fullPath, repoRoot, cancellationToken));

    public Task<RenamePlan> PlanRenameAsync(CodeSite site, string newName, CancellationToken cancellationToken)
        => this.RunAsync(code => code.PlanRenameAsync(site, newName, cancellationToken));

    public Task<FormatResult> FormatAsync(string fullPath, string expectedText, IReadOnlyList<(int Start, int End)> lines, CancellationToken cancellationToken)
        => this.RunAsync(code => code.FormatAsync(fullPath, expectedText, lines, cancellationToken));

    public Task<string> GetCodeFixesAsync(string fullPath, int line, string diagnosticId, string repoRoot, CancellationToken cancellationToken)
        => this.RunAsync(code => code.GetCodeFixesAsync(fullPath, line, diagnosticId, repoRoot, cancellationToken));

    public Task<CodeFixPlan> PlanCodeFixAsync(string fullPath, int line, string diagnosticId, string fixTitle, string repoRoot, CancellationToken cancellationToken)
        => this.RunAsync(code => code.PlanCodeFixAsync(fullPath, line, diagnosticId, fixTitle, repoRoot, cancellationToken));

    private static RoslynCodeIntelligence Create(IComponentModel components)
    {
        VisualStudioWorkspace workspace = components.GetService<VisualStudioWorkspace>();
        var fixers = new ConcurrentDictionary<string, Lazy<IReadOnlyList<CodeFixProvider>>>(StringComparer.Ordinal);
        return new RoslynCodeIntelligence(
            () => workspace.CurrentSolution,
            language => fixers.GetOrAdd(language, l => new Lazy<IReadOnlyList<CodeFixProvider>>(() => HostFixers(components, l), LazyThreadSafetyMode.ExecutionAndPublication)).Value);
    }

    /// <summary>
    /// The code fixes Visual Studio's light bulb offers for a language, as MEF exports them. Each is created on first
    /// use; one that can't be created (or can't be read on this version of Visual Studio) is left out.
    /// </summary>
    private static IReadOnlyList<CodeFixProvider> HostFixers(IComponentModel components, string language)
    {
        var result = new List<CodeFixProvider>();
        IEnumerable<Lazy<CodeFixProvider, IDictionary<string, object>>> exports;
        try
        {
            exports = components.DefaultExportProvider.GetExports<CodeFixProvider, IDictionary<string, object>>().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result;
        }

        foreach (Lazy<CodeFixProvider, IDictionary<string, object>> export in exports)
        {
            bool forLanguage = export.Metadata.TryGetValue("Languages", out object? languages)
                ? languages is IEnumerable<string> names ? names.Contains(language) : Equals(languages, language)
                : true;
            if (!forLanguage)
            {
                continue;
            }

            try
            {
                result.Add(export.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // left out, as the light bulb does with a fixer that fails
            }
        }

        return result;
    }

    private async Task<T> RunAsync<T>(Func<RoslynCodeIntelligence, Task<T>> run)
    {
        try
        {
            return await run(this.inner.Value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or MissingMemberException or TypeLoadException)
        {
            throw new ToolInputException($"This version of Visual Studio's code model isn't compatible with the extension, so code navigation isn't available: {ex.Message}");
        }
    }
}
