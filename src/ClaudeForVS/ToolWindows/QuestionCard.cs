using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>One option button in the question card.</summary>
internal sealed class OptionViewModel : INotifyPropertyChanged
{
    private readonly QuestionViewModel owner;
    private bool isSelected;

    public OptionViewModel(QuestionViewModel owner, QuestionOption option)
    {
        this.owner = owner;
        this.Label = option.Label;
        this.Description = option.Description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; }

    public string Description { get; }

    public bool HasDescription => this.Description.Length > 0;

    /// <summary>Drives the indicator shape: a circle for single choice, a square for multiple choice.</summary>
    public bool IsMulti => this.owner.MultiSelect;

    public bool IsSelected
    {
        get => this.isSelected;
        set
        {
            if (this.isSelected == value)
            {
                return;
            }

            this.isSelected = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsSelected)));
            this.owner.OnOptionChanged(this);
        }
    }

    /// <summary>Sets the selection without notifying the owner (used when it clears other options).</summary>
    internal void Deselect()
    {
        this.isSelected = false;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsSelected)));
    }
}

/// <summary>One question in the question card, with its options and an optional typed answer.</summary>
internal sealed class QuestionViewModel : INotifyPropertyChanged
{
    private readonly Action changed;
    private string otherText = string.Empty;

    public QuestionViewModel(UserQuestion question, Action changed)
    {
        this.changed = changed;
        this.Question = question.Question;
        this.Header = question.Header;
        this.MultiSelect = question.MultiSelect;
        this.Options = question.Options.Select(o => new OptionViewModel(this, o)).ToList();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Question { get; }

    public string Header { get; }

    public bool HasHeader => this.Header.Length > 0;

    public bool MultiSelect { get; }

    public string Hint => this.MultiSelect ? "Choose any that apply, or type your own answer." : "Choose one, or type your own answer.";

    public IReadOnlyList<OptionViewModel> Options { get; }

    /// <summary>The user's own answer. For single choice, typing clears the selected option (and vice versa).</summary>
    public string OtherText
    {
        get => this.otherText;
        set
        {
            this.otherText = value;
            this.OnPropertyChanged();
            if (!this.MultiSelect && value.Trim().Length > 0)
            {
                foreach (OptionViewModel option in this.Options.Where(o => o.IsSelected))
                {
                    option.Deselect();
                }
            }

            this.changed();
        }
    }

    public bool IsAnswered => this.Answer is not null;

    /// <summary>Selected labels (joined with ", ") plus any typed text, or null if nothing is chosen yet.</summary>
    public string? Answer
    {
        get
        {
            List<string> parts = this.Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (this.otherText.Trim() is { Length: > 0 } typed)
            {
                parts.Add(typed);
            }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
    }

    internal void OnOptionChanged(OptionViewModel option)
    {
        if (!this.MultiSelect && option.IsSelected)
        {
            foreach (OptionViewModel other in this.Options.Where(o => o != option && o.IsSelected))
            {
                other.Deselect();
            }

            if (this.otherText.Length > 0)
            {
                this.otherText = string.Empty;
                this.OnPropertyChanged(nameof(this.OtherText));
            }
        }

        this.changed();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
