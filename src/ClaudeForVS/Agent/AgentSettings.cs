using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeForVS.Agent;

/// <summary>Which backend talks to Claude.</summary>
public enum ChatEngineKind
{
    /// <summary>Calls the Claude API directly with an API key, using this extension's own tools.</summary>
    Api,

    /// <summary>Drives the Claude Code CLI, which signs in with the user's own Pro/Max plan.</summary>
    ClaudeCode,
}

public enum ClaudeModel
{
    Opus,
    Sonnet,
    Haiku,
}

public enum EffortLevel
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>How much Claude may do on its own.</summary>
public enum AgentMode
{
    /// <summary>Edits files and runs commands without asking.</summary>
    Auto,

    /// <summary>Read-only: reviews the code and suggests changes, but never edits files or changes the repository.</summary>
    Plan,

    /// <summary>Edits files and runs commands after the user approves each action.</summary>
    Coding,
}

/// <summary>User-chosen model and effort, persisted to %LOCALAPPDATA%\ClaudeForVS\settings.json.</summary>
internal sealed record AgentSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeForVS", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ChatEngineKind Engine { get; init; } = ChatEngineKind.Api;

    public ClaudeModel Model { get; init; } = ClaudeModel.Opus;

    public EffortLevel Effort { get; init; } = EffortLevel.High;

    public AgentMode Mode { get; init; } = AgentMode.Coding;

    /// <summary>
    /// A repository the user chose by hand. Null means "follow the open solution": the chat switches
    /// repository when a different solution or folder is opened.
    /// </summary>
    public string? PinnedRepoPath { get; init; }

    /// <summary>
    /// Run the hooks in Claude Code's settings files on the Claude API engine. Off by default, since hooks are commands.
    /// (Claude Code runs its own hooks regardless.)
    /// </summary>
    public bool RunHooks { get; init; }

    /// <summary>
    /// A cost, in US dollars, that one chat on the Claude API engine shouldn't pass: Claude stops and asks before
    /// going on. 0 means no limit.
    /// </summary>
    public double SpendLimitUsd { get; init; }

    /// <summary>
    /// After Claude changes C# or Visual Basic code, format the lines it wrote with the user's editor settings
    /// (.editorconfig), so they match the rest of the file.
    /// </summary>
    public bool FormatChangedFiles { get; init; } = true;

    /// <summary>
    /// On the Claude API engine, use the newest version of the chosen model the Models API lists, rather than the one
    /// this extension was built for. Off by default: a new version can change what requests it accepts. (Claude Code
    /// always runs the newest version it knows about.)
    /// </summary>
    public bool UseNewestApiModels { get; init; }

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(SettingsFile), JsonOptions) ?? new AgentSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new AgentSettings();
    }

    public void Save() => AtomicFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>The API model ID this extension was built and tested with.</summary>
    public static string ModelId(ClaudeModel model) => model switch
    {
        ClaudeModel.Sonnet => "claude-sonnet-5-5",
        ClaudeModel.Haiku => "claude-haiku-5-5",
        _ => "claude-opus-5-5",
    };

    /// <summary>The model ID the Claude API engine sends: the newest listed version if <see cref="UseNewestApiModels"/> is on.</summary>
    public string ApiModelId(ClaudeModel model) =>
        this.UseNewestApiModels && ModelCatalog.NewerApiModel(model) is ModelVersion newer ? newer.Id : ModelId(model);

    /// <summary>Claude Code's alias for the family, which it resolves to the newest version itself.</summary>
    public static string ClaudeCodeAlias(ClaudeModel model) => model switch
    {
        ClaudeModel.Sonnet => "sonnet",
        ClaudeModel.Haiku => "haiku",
        _ => "opus",
    };

    /// <summary>Context window in tokens, the same for every model.</summary>
    public const long ContextWindow = 1_000_000;

    /// <summary>
    /// Short display name of the version an engine runs, e.g. "Opus 5.5": on Claude Code, the one its alias last
    /// resolved to; on the API, the newest listed one if <paramref name="newestApi"/>, otherwise the built-in one.
    /// </summary>
    public static string ModelName(ClaudeModel model, ChatEngineKind engine, bool newestApi = false) =>
        (engine == ChatEngineKind.ClaudeCode ? ModelCatalog.ClaudeCodeModel(model)
            : newestApi ? ModelCatalog.NewerApiModel(model)
            : null)?.Name
        ?? ModelCatalog.Parse(ModelId(model))!.Name;

    /// <summary>Display name of a model with these settings' engine, e.g. "Opus 5.5".</summary>
    public string NameOf(ClaudeModel model) => ModelName(model, this.Engine, this.UseNewestApiModels);

    public static string EffortName(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => "Low",
        EffortLevel.Medium => "Medium",
        EffortLevel.XHigh => "Extra high",
        EffortLevel.Max => "Max",
        _ => "High",
    };

    public static string ModeName(AgentMode mode) => mode switch
    {
        AgentMode.Auto => "Auto",
        AgentMode.Plan => "Plan",
        _ => "Coding",
    };

    /// <summary>Tells Claude what it's allowed to do: in the conversation on the Claude API, in the system prompt on Claude Code.</summary>
    public static string ModeInstructions(AgentMode mode) => mode switch
    {
        AgentMode.Plan =>
            "You are in Plan mode: review the code and suggest changes, but don't edit files or run commands that " +
            "change the repository. Describe proposed changes (with code snippets where useful) for the user to apply.",
        AgentMode.Auto => "You are in Auto mode: edits and commands run without asking the user first, so be careful with destructive actions.",
        _ => "You are in Coding mode: the user approves each edit and command before it runs.",
    };
}
