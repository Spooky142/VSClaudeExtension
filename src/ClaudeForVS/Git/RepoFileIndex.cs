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
    private Dictionary<string, List<string>> byName = new(StringComparer.OrdinalIgnoreCase);
    private Task? loading;

    /// <summary>The files as last loaded; starts a background refresh if they're stale or for another repository.</summary>
    public IReadOnlyList<string> Get(string root)
    {
        if (!string.Equals(root, this.repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            this.repoRoot = root;
            this.files = [];
            this.byName = new(StringComparer.OrdinalIgnoreCase);
            this.loadedAt = DateTime.MinValue;
        }

        if (DateTime.UtcNow - this.loadedAt > MaxAge && this.loading is null)
        {
            this.loading = this.LoadAsync(root);
        }

        return this.files;
    }

    /// <summary>The files (as last loaded) whose file name is <paramref name="name"/>, ignoring case.</summary>
    public IReadOnlyList<string> Named(string root, string name)
    {
        this.Get(root);
        return this.byName.TryGetValue(name, out List<string>? named) ? named : [];
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
                (List<string> files, Dictionary<string, List<string>> byName) = await Task.Run(() =>
                {
                    List<string> list = output.Split(['\0'], StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                    var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in list)
                    {
                        string name = file.Substring(file.LastIndexOf('/') + 1);
                        if (!names.TryGetValue(name, out List<string>? named))
                        {
                            names[name] = named = [];
                        }

                        named.Add(file);
                    }

                    return (list, names);
                }).ConfigureAwait(true);
                if (string.Equals(root, this.repoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    (this.files, this.byName) = (files, byName);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            this.loading = null;
            if (string.Equals(root, this.repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                this.loadedAt = DateTime.UtcNow;
            }
            else if (this.repoRoot is string current)
            {
                // The repository changed while this one was loading, and its files were thrown away: load the new one's.
                this.loading = this.LoadAsync(current);
            }
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
