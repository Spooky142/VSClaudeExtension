using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// Claude Code's skills in the repository, each of which can be turned off there. Like Claude Code's /skills, the
/// choice is saved straight away to skillOverrides in .claude/settings.local.json, so a skill turned off isn't listed
/// to Claude at all; Claude Code picks it up from the next message on.
/// </summary>
public partial class SkillsDialog : DialogWindow
{
    private readonly string repoRoot;

    internal SkillsDialog(string repoRoot, ChatEngineKind engine)
    {
        this.repoRoot = repoRoot;
        this.InitializeComponent();
        HighContrast.Follow(this);

        // Skills turned off aren't in what Claude Code reports, so the ones with an override are listed regardless.
        IReadOnlyDictionary<string, SkillOverride> overrides = SkillOverrides.Load(repoRoot);
        List<SkillRow> rows = SkillCatalog.List(repoRoot, alsoInclude: overrides.Keys)
            .Select(s => new SkillRow(s, overrides.TryGetValue(s.Name, out SkillOverride? o) ? o : null))
            .ToList();
        foreach (SkillRow row in rows)
        {
            row.EnabledChanged += this.OnEnabledChanged;
        }

        this.SkillList.ItemsSource = rows;
        this.EmptyNote.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.RepoText.Text = "In " + Path.GetFileName(repoRoot.TrimEnd('\\', '/')) + " · " + repoRoot;
        this.EngineNote.Text = engine == ChatEngineKind.ClaudeCode
            ? "Skills are instructions Claude Code loads when a task calls for them: your own in ~/.claude/skills, the repository's in .claude/skills, and those built into Claude Code or its plugins. Ones you turn off aren't shown to Claude at all. This is the same setting as /skills in Claude Code, saved in the repository's .claude/settings.local.json."
            : "Skills are a Claude Code feature, so these apply only with the Claude Code engine (choose it in Settings).";
        this.ApplyNote.Text = "Changes are saved straight away and apply from your next message.";
        if (!SkillCatalog.HasReported(repoRoot))
        {
            this.ListNote.Text = "Skills built into Claude Code and its plugins are listed once you've sent a message in a Claude Code chat in this repository.";
            this.ListNote.Visibility = Visibility.Visible;
        }
    }

    private void OnEnabledChanged(object sender, EventArgs e)
    {
        if (sender is not SkillRow row)
        {
            return;
        }

        try
        {
            SkillOverrides.Set(this.repoRoot, row.Name, row.IsEnabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            this.ListNote.Text = "Couldn't save the change to .claude/settings.local.json: " + ex.Message;
            this.ListNote.Visibility = Visibility.Visible;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();
}
