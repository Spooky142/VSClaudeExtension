using ClaudeForVS.ToolWindows;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace ClaudeForVS;

/// <summary>Tools &gt; Claude Chat: opens the chat tool window.</summary>
[VisualStudioContribution]
internal class ShowClaudeWindowCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%ClaudeForVS.ShowClaudeWindowCommand.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ToolsMenu],
        Icon = new(ImageMoniker.KnownValues.StatusInformation, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        => this.Extensibility.Shell().ShowToolWindowAsync<ClaudeToolWindow>(activate: true, cancellationToken);
}
