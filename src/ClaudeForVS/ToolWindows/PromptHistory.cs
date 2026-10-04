using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// Prompts sent recently, oldest first, for Up/Down recall in the prompt box. Saved to
/// %LOCALAPPDATA%\ClaudeForVS\prompt-history.json.
/// </summary>
internal sealed class PromptHistory
{
    private const int MaxEntries = 200;

    private static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "prompt-history.json");

    private readonly List<string> entries;
    private readonly string? file;

    // Position while browsing: entries.Count means "not browsing" (the draft).
    private int position;
    private string draft = string.Empty;

    /// <param name="file">Where to save; the default file if not given, or null (via <see cref="InMemory"/>) not to save.</param>
    public PromptHistory(string? file = null)
        : this(file ?? HistoryFile, load: true)
    {
    }

    private PromptHistory(string? file, bool load)
    {
        this.file = file;
        this.entries = load && file != null ? Load(file) : [];
        this.position = this.entries.Count;
    }

    public static PromptHistory InMemory() => new(null, load: false);

    public IReadOnlyList<string> Entries => this.entries;

    /// <summary>True while an older prompt is showing in place of the draft.</summary>
    public bool IsBrowsing => this.position < this.entries.Count;

    public void Add(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length > 0)
        {
            this.entries.Remove(prompt); // keep one copy, at the newest end
            this.entries.Add(prompt);
            if (this.entries.Count > MaxEntries)
            {
                this.entries.RemoveRange(0, this.entries.Count - MaxEntries);
            }

            this.Save();
        }

        this.position = this.entries.Count;
        this.draft = string.Empty;
    }

    /// <summary>The next older prompt, or null at the oldest. Remembers <paramref name="current"/> as the draft when browsing starts.</summary>
    public string? Older(string current)
    {
        if (this.position == 0)
        {
            return null;
        }

        if (!this.IsBrowsing)
        {
            this.draft = current;
        }

        return this.entries[--this.position];
    }

    /// <summary>The next newer prompt, then the draft; null when not browsing.</summary>
    public string? Newer()
    {
        if (!this.IsBrowsing)
        {
            return null;
        }

        this.position++;
        return this.IsBrowsing ? this.entries[this.position] : this.draft;
    }

    /// <summary>Stops browsing, e.g. once the recalled prompt has been edited.</summary>
    public void Reset() => this.position = this.entries.Count;

    private static List<string> Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return [];
    }

    private void Save()
    {
        if (this.file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.file)!);
            File.WriteAllText(this.file, JsonSerializer.Serialize(this.entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
