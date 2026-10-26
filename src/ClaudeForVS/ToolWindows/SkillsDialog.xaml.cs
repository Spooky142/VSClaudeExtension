using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ClaudeForVS.Agent;
using ClaudeForVS.Agent.Mcp;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// Claude Code's skills in the repository, each of which can be turned off there. Turning one off is saved straight
/// away; Claude Code is then told not to use it (a Skill(name) deny rule) from the next message on.
/// </summary>
public partial class SkillsDialog : DialogWindow
{
    private readonly string repoRoot;

    internal SkillsDialog(string repoRoot, ChatEngineKind engine)
    {
        this.repoRoot = repoRoot;
        this.InitializeComponent();
        HighContrast.Follow(this);

        IReadOnlyCollection<string> disabled = DisabledNames.Skills.For(repoRoot);
        List<SkillRow> rows = SkillCatalog.List(repoRoot, alsoInclude: disabled)
            .Select(s => new SkillRow(s, !disabled.Contains(s.Name)))
            .ToList();
        foreach (SkillRow row in rows)
        {
            row.EnabledChanged += this.OnEnabledChanged;
        }

        this.SkillList.ItemsSource = rows;
        this.EmptyNote.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.RepoText.Text = "In " + Path.GetFileName(repoRoot.TrimEnd('\\', '/')) + " · " + repoRoot;
        this.EngineNote.Text = engine == ChatEngineKind.ClaudeCode
            ? "Skills are instructions Claude Code loads when a task calls for them: your own in ~/.claude/skills, the repository's in .claude/skills, and those built into Claude Code or its plugins. Claude can't use the ones you turn off."
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
        if (sender is SkillRow row)
        {
            DisabledNames.Skills.Set(this.repoRoot, row.Name, row.IsEnabled);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();
}
