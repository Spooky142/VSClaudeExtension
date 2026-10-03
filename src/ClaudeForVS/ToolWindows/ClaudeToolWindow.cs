using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace ClaudeForVS.ToolWindows;

[VisualStudioContribution]
internal class ClaudeToolWindow : ToolWindow
{
    private readonly ClaudeChatViewModel viewModel;

    public ClaudeToolWindow(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
        this.Title = "Claude";
        this.viewModel = new ClaudeChatViewModel(extensibility);
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DockedTo(new Guid("3ae79031-e1bc-11d0-8f78-00a0c9110057")), // Solution Explorer
        DockDirection = Dock.Right,
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await this.viewModel.RefreshRepoFromSolutionAsync(cancellationToken);
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
        => Task.FromResult<IRemoteUserControl>(new ClaudeChatControl(this.viewModel));
}

internal class ClaudeChatControl : Microsoft.VisualStudio.Extensibility.UI.RemoteUserControl
{
    public ClaudeChatControl(object? dataContext)
        : base(dataContext)
    {
    }
}
