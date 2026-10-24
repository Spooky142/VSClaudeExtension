using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeForVS.ToolWindows;

/// <summary>
/// The box Alt+/ opens at the caret, asking what Claude should change. ShowModal() returns true with the
/// <see cref="Instruction"/> when the user presses Enter. A dialog rather than a box in the editor, so the keys typed
/// into it aren't taken by the editor's own commands (Backspace, the arrows, Ctrl+Z…).
/// </summary>
public partial class InlineEditDialog : DialogWindow
{
    internal InlineEditDialog(string heading, Point screenPosition)
    {
        this.InitializeComponent();
        this.Heading.Text = heading;
        this.Left = screenPosition.X;
        this.Top = screenPosition.Y;
        this.Loaded += (_, _) =>
        {
            this.KeepOnScreen();
            this.InstructionBox.Focus();
        };

        // Clicking back into Visual Studio cancels, as Esc does.
        this.Deactivated += (_, _) =>
        {
            if (this.IsVisible && this.DialogResult is null)
            {
                this.Close();
            }
        };
    }

    internal string Instruction => this.InstructionBox.Text.Trim();

    private void OnInstructionKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                int caret = this.InstructionBox.CaretIndex;
                this.InstructionBox.Text = this.InstructionBox.Text.Insert(caret, Environment.NewLine);
                this.InstructionBox.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
                break;
            case Key.Enter:
                if (this.Instruction.Length > 0)
                {
                    this.DialogResult = true;
                }

                e.Handled = true;
                break;
            case Key.Escape:
                this.DialogResult = false;
                e.Handled = true;
                break;
        }
    }

    private void OnInstructionChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => this.KeepOnScreen();

    /// <summary>Moves the box up or left if it would run off the screen the caret is on.</summary>
    private void KeepOnScreen()
    {
        Rect area = SystemParameters.WorkArea;
        double width = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
        double height = this.ActualHeight;
        this.Left = Math.Max(area.Left, Math.Min(this.Left, area.Right - width));
        if (height > 0 && this.Top + height > area.Bottom)
        {
            this.Top = Math.Max(area.Top, area.Bottom - height);
        }
    }
}
