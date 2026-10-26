using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>One skill in the Skills window: its name, what it's for, where it comes from and whether Claude can use it.</summary>
internal sealed class SkillRow(SkillInfo skill, SkillOverride? setting) : INotifyPropertyChanged
{
    private SkillOverride? setting = setting;

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

    /// <summary>False when managed settings decide it.</summary>
    public bool CanChange => this.setting?.LockedBy == null;

    /// <summary>A line about a setting the On box doesn't show: a lock, or one of skillOverrides' in-between values.</summary>
    public string SettingNote => this.setting switch
    {
        { LockedBy: string by } => $"Set to \"{this.setting.Value}\" by {by}, so it can't be changed here.",
        { Value: "name-only" } => "Listed to Claude without its description (set in Claude Code's settings).",
        { Value: "user-invocable-only" } => $"Hidden from Claude; you can still run it as /{skill.Name} (set in Claude Code's settings).",
        _ => string.Empty,
    };

    public bool HasSettingNote => this.SettingNote.Length > 0;

    public bool IsEnabled
    {
        get => this.setting?.ClaudeCanUse ?? true;
        set
        {
            if (this.IsEnabled == value || !this.CanChange)
            {
                return;
            }

            this.setting = value ? null : new SkillOverride("off", null);
            this.OnChanged();
            this.OnChanged(nameof(this.SettingNote));
            this.OnChanged(nameof(this.HasSettingNote));
            this.EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
