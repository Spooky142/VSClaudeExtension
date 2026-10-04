using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>One block of changes on the approval card, with the checkbox that keeps it or leaves it out.</summary>
internal sealed class ChangeBlockToggle(int index, Action changed) : INotifyPropertyChanged
{
    private bool isKept = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; } = index;

    public bool IsKept
    {
        get => this.isKept;
        set
        {
            if (this.isKept != value)
            {
                this.isKept = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsKept)));
                changed();
            }
        }
    }
}

/// <summary>A line of the approval card's diff when changes can be kept one block at a time.</summary>
/// <param name="Block">The block a changed line belongs to; null for headers and context.</param>
/// <param name="ShowToggle">True on a block's first line, where its checkbox goes.</param>
internal sealed record ReviewLineItem(string Text, DiffLineKind Kind, ChangeBlockToggle? Block, bool ShowToggle);

/// <summary>A file change on the approval card, split into blocks the user can keep or leave out.</summary>
internal sealed class ChangeReview
{
    private readonly ChangeBlocks blocks;

    private ChangeReview(ChangeBlocks blocks, Action changed)
    {
        this.blocks = blocks;
        this.Toggles = Enumerable.Range(0, blocks.Count).Select(i => new ChangeBlockToggle(i, changed)).ToList();
        var lines = new List<ReviewLineItem>();
        int last = -1;
        foreach (ReviewLine line in blocks.Lines)
        {
            ChangeBlockToggle? toggle = line.Block >= 0 ? this.Toggles[line.Block] : null;
            DiffLineKind kind = line.Op switch { '@' => DiffLineKind.Hunk, '+' => DiffLineKind.Added, '-' => DiffLineKind.Removed, _ => DiffLineKind.Context };
            lines.Add(new ReviewLineItem(line.Display, kind, toggle, toggle != null && line.Block != last));
            if (line.Block >= 0)
            {
                last = line.Block;
            }
        }

        this.Lines = lines;
    }

    public IReadOnlyList<ChangeBlockToggle> Toggles { get; }

    public IReadOnlyList<ReviewLineItem> Lines { get; }

    public int KeptCount => this.Toggles.Count(t => t.IsKept);

    /// <summary>"2 of 3 changes selected", or a hint while all are.</summary>
    public string Summary => this.KeptCount == this.Toggles.Count
        ? $"{this.Toggles.Count} changes · untick any you don't want"
        : $"{this.KeptCount} of {this.Toggles.Count} changes selected";

    /// <summary>A review for a change with at least two blocks, or null if there's nothing to choose between.</summary>
    public static ChangeReview? For(FileChange change, Action changed) =>
        ChangeBlocks.Compute(change.Before, change.After) is { Count: >= 2 } blocks ? new ChangeReview(blocks, changed) : null;

    /// <summary>The text to write: null if every block is kept (the change as proposed).</summary>
    public string? PartialAfter()
    {
        List<int> kept = this.Toggles.Where(t => t.IsKept).Select(t => t.Index).ToList();
        return kept.Count == this.Toggles.Count ? null : this.blocks.Apply(kept);
    }
}
