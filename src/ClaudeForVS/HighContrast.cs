using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS;

/// <summary>
/// Colours for Windows' high-contrast themes. There the theme's own system colours are used, rather than the accent
/// and translucent tints, which can be hard to see or vanish altogether.
/// </summary>
internal static class HighContrast
{
    public static bool IsOn => SystemParameters.HighContrast;

    // Theme.xaml's palette and what each becomes in a high-contrast theme. Translucent tints go: the line's own text
    // (a diff's "+" and "-", say) says what it is.
    private static readonly (string Key, Func<Brush> Brush)[] Palette =
    [
        ("Accent", () => SystemColors.HighlightBrush),
        ("AccentHover", () => SystemColors.HighlightBrush),
        ("AccentPressed", () => SystemColors.HighlightBrush),
        ("OnAccent", () => SystemColors.HighlightTextBrush),
        ("UserBubble", () => SystemColors.WindowBrush),
        ("UserBubbleBorder", () => SystemColors.WindowTextBrush),
        ("SubtleFill", () => Brushes.Transparent),
        ("SubtleBorder", () => SystemColors.WindowTextBrush),
        ("AddedTint", () => Brushes.Transparent),
        ("RemovedTint", () => Brushes.Transparent),
        ("AmberTint", () => Brushes.Transparent),
        ("ErrorTint", () => Brushes.Transparent),
        ("AddedGreen", () => SystemColors.WindowTextBrush),
        ("RemovedRed", () => SystemColors.WindowTextBrush),
        ("Amber", () => SystemColors.WindowTextBrush),
        ("ErrorRed", () => SystemColors.WindowTextBrush),
    ];

    /// <summary>
    /// Puts the high-contrast colours in place of Theme.xaml's palette in <paramref name="root"/>'s resources while a
    /// high-contrast theme is on, and takes them out again when it goes off.
    /// </summary>
    public static void Follow(FrameworkElement root)
    {
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                ApplyOnUIThreadAsync(root).FileAndForget("ClaudeForVS/HighContrast");
            }
        }

        Apply(root);
        root.Loaded += (_, _) =>
        {
            Apply(root);
            SystemParameters.StaticPropertyChanged -= OnChanged;
            SystemParameters.StaticPropertyChanged += OnChanged;
        };
        root.Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= OnChanged;
    }

    // SystemParameters raises its changes on whichever thread noticed them.
    private static async System.Threading.Tasks.Task ApplyOnUIThreadAsync(FrameworkElement root)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Apply(root);
    }

    private static void Apply(FrameworkElement root)
    {
        bool on = IsOn;
        foreach ((string key, Func<Brush> brush) in Palette)
        {
            if (on)
            {
                root.Resources[key] = brush();
            }
            else
            {
                root.Resources.Remove(key); // back to Theme.xaml's
            }
        }
    }

    /// <summary>The accent for marks drawn in the editor.</summary>
    public static Brush EditorAccent => IsOn ? SystemColors.HighlightBrush : ClaudeAccent;

    private static readonly Brush ClaudeAccent = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

    /// <summary>Gives a button in the editor (outside the chat's own styles) Visual Studio's themed look.</summary>
    public static void UseVsStyle(Button button) => button.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ButtonStyleKey);

    /// <summary>Gives a context menu in the editor Visual Studio's themed look.</summary>
    public static void UseVsStyle(ContextMenu menu) => menu.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ContextMenuStyleKey);

    public static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
