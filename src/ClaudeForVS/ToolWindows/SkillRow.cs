using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>One skill in the Skills window: its name, what it's for, where it comes from and whether it's on.</summary>
internal sealed class SkillRow(SkillInfo skill, bool isEnabled) : INotifyPropertyChanged
{
    private bool isEnabled = isEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user turns the skill on or off.</summary>
    public event EventHandler? EnabledChanged;

    public string Name => skill.Name;

    public string Description => skill.Description ?? string.Empty;

    public bool HasDescription => skill.Description != null;

    public string SourceText => skill.Source switch
    {
        SkillSource.Personal => "Personal · ~/.claude/skills",
        SkillSource.Project => "Project · .claude/skills",
        SkillSource.Plugin => "Plugin · " + skill.Name.Substring(0, skill.Name.IndexOf(':')),
        _ => "Claude Code",
    };

    public bool IsEnabled
    {
        get => this.isEnabled;
        set
        {
            if (this.isEnabled == value)
            {
                return;
            }

            this.isEnabled = value;
            this.OnChanged();
            this.EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
