using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace ClaudeForVS;

/// <summary>
/// Entry point for the out-of-process extension. Everything runs in a separate
/// .NET 8 host process, so uninstalling the VSIX removes it completely.
/// </summary>
[VisualStudioContribution]
internal class ExtensionEntrypoint : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "ClaudeForVS.6d1c2a9e-3f4b-4c8e-9a51-0b7e2f4d8c13",
            version: this.ExtensionAssemblyVersion,
            publisherName: "adamb",
            displayName: "Claude for Visual Studio",
            description: "Chat with Claude about the current git repository, directly from Visual Studio."),
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
    }
}
