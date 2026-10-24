using System.Windows;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeForVS.ToolWindows;

/// <summary>Offers to carry on at lower priority past the 5-hour limit. ShowModal() returns true if the user accepts.</summary>
public partial class LowPriorityDialog : DialogWindow
{
    internal LowPriorityDialog(LowPriorityOffer offer)
    {
        this.InitializeComponent();
        this.Message.Text = ClaudeChatViewModel.DescribeOffer(offer);
    }

    private void OnContinue(object sender, RoutedEventArgs e) => this.DialogResult = true;
}
