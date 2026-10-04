using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Git;

/// <summary>The repository's files (tracked and untracked, minus ignored ones) for @-mention completion, refreshed at most once a minute.</summary>
internal sealed class RepoFileIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private string? repoRoot;
    private DateTime loadedAt;
    private IReadOnlyList<string> files = [];
    private Task? loading;

    /// <summary>The files as last loaded; starts a background refresh if they're stale or for another repository.</summary>
    public IReadOnlyList<string> Get(string root)
    {
        if (!string.Equals(root, this.repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            this.repoRoot = root;
            this.files = [];
            this.loadedAt = DateTime.MinValue;
        }

        if (DateTime.UtcNow - this.loadedAt > MaxAge && this.loading is null)
        {
            this.loading = this.LoadAsync(root);
        }

        return this.files;
    }

    /// <summary>Waits for a refresh in progress, if any.</summary>
    public Task WhenLoadedAsync() => this.loading ?? Task.CompletedTask;

    private async Task LoadAsync(string root)
    {
        try
        {
            (int code, string output) = await GitRunner.RunAsync(
                root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], CancellationToken.None, includeStderr: false).ConfigureAwait(true);
            if (code == 0 && string.Equals(root, this.repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                this.files = output.Split(['\0'], StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            this.loadedAt = DateTime.UtcNow;
            this.loading = null;
        }
    }

    /// <summary>
    /// Best matches for what was typed after "@": file names starting with it first, then paths containing it,
    /// shortest first.
    /// </summary>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> files, string query, int max)
    {
        if (query.Length == 0)
        {
            return files.OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }

        string q = query.Replace('\\', '/');
        return files
            .Select(f => (File: f, Rank: Rank(f, q)))
            .Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.File.Length)
            .Take(max)
            .Select(m => m.File)
            .ToList();
    }

    private static int Rank(string path, string query)
    {
        int slash = path.LastIndexOf('/');
        string name = path.Substring(slash + 1);
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 2;
        }

        return path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : -1;
    }
}
