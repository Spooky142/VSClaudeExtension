using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
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

    private static RoslynCodeIntelligence Create(IComponentModel components)
    {
        VisualStudioWorkspace workspace = components.GetService<VisualStudioWorkspace>();
        return new RoslynCodeIntelligence(() => workspace.CurrentSolution);
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
