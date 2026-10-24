using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using ClaudeForVS.Agent;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS;

/// <summary>
/// Tools &gt; Options &gt; Claude &gt; General. Shows the same settings as the chat window's Settings dialog (minus the
/// repository and API key), stored in the same settings.json rather than Visual Studio's registry.
/// </summary>
[Guid("b7a3c1d2-4e5f-4a6b-8c9d-0e1f2a3b4c5d")]
public sealed class OptionsPage : DialogPage
{
    /// <summary>Raised on the UI thread when the page saves different settings, so the open chat can apply them.</summary>
    internal static event EventHandler<AgentSettings>? Applied;

    [Category("Claude")]
    [DisplayName("Engine")]
    [Description("Claude API uses your Anthropic API key and is billed per token. Claude Code uses the Claude Code CLI signed in with your Pro or Max plan. Changing it starts a new chat.")]
    [TypeConverter(typeof(EngineConverter))]
    public ChatEngineKind Engine { get; set; }

    [Category("Claude")]
    [DisplayName("Model")]
    [Description("Changing the model starts a new chat.")]
    [TypeConverter(typeof(ModelConverter))]
    public ClaudeModel Model { get; set; }

    [Category("Claude")]
    [DisplayName("Effort")]
    [Description("How hard Claude thinks. Applies from the next message.")]
    [TypeConverter(typeof(EffortConverter))]
    public EffortLevel Effort { get; set; }

    [Category("Claude")]
    [DisplayName("Mode")]
    [Description("Coding asks before every change. Auto edits and runs commands without asking. Plan is read-only.")]
    [TypeConverter(typeof(ModeConverter))]
    public AgentMode Mode { get; set; }

    [Category("More settings")]
    [DisplayName("Repository and API key")]
    [Description("Set these in the chat window's Settings (the gear icon).")]
    public string Note => "In the chat window's Settings";

    public override void LoadSettingsFromStorage()
    {
        AgentSettings settings = AgentSettings.Load();
        this.Engine = settings.Engine;
        this.Model = settings.Model;
        this.Effort = settings.Effort;
        this.Mode = settings.Mode;
    }

    public override void SaveSettingsToStorage()
    {
        AgentSettings current = AgentSettings.Load();
        AgentSettings updated = current with { Engine = this.Engine, Model = this.Model, Effort = this.Effort, Mode = this.Mode };
        if (updated == current)
        {
            return;
        }

        updated.Save();
        Applied?.Invoke(this, updated);
    }

    /// <summary>The chat window may have changed the settings since the page was last shown.</summary>
    protected override void OnActivate(CancelEventArgs e)
    {
        this.LoadSettingsFromStorage();
        base.OnActivate(e);
    }

    /// <summary>Shows an enum's values by their display names in the property grid.</summary>
    internal abstract class NamedEnumConverter<T>(Func<T, string> name) : EnumConverter(typeof(T))
        where T : struct, Enum
    {
        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
            destinationType == typeof(string) && value is T item ? name(item) : base.ConvertTo(context, culture, value, destinationType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
            value is string text && Enum.GetValues(typeof(T)).Cast<T>().FirstOrDefault(v => name(v) == text) is T match && name(match) == text
                ? match
                : base.ConvertFrom(context, culture, value);
    }

    internal sealed class EngineConverter() : NamedEnumConverter<ChatEngineKind>(e => e == ChatEngineKind.ClaudeCode ? "Claude Code" : "Claude API");

    internal sealed class ModelConverter() : NamedEnumConverter<ClaudeModel>(m => AgentSettings.Load().NameOf(m));

    internal sealed class EffortConverter() : NamedEnumConverter<EffortLevel>(AgentSettings.EffortName);

    internal sealed class ModeConverter() : NamedEnumConverter<AgentMode>(AgentSettings.ModeName);
}
