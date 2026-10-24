using System;
using System.IO;
using System.Reflection;
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

    // Medium thinks noticeably less than High and is enough for most coding; settings saved before keep their own.
    public EffortLevel Effort { get; init; } = EffortLevel.Medium;

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
    /// On the Claude API engine, the context size (in tokens) at which the conversation is summarized and restarted
    /// from the summary. Every request re-reads the whole conversation, so summarizing well before the window is full
    /// keeps long chats cheaper. 0 means only when the window is nearly full.
    /// </summary>
    public long CompactAtTokens { get; init; } = DefaultCompactAtTokens;

    public const long DefaultCompactAtTokens = 300_000;

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

    /// <summary>
    /// Show a Windows notification when Claude needs an approval or an answer, or finishes, while Visual Studio isn't
    /// in front or the chat window is hidden.
    /// </summary>
    public bool ShowNotifications { get; init; } = true;

    /// <summary>Check this extension's GitHub releases once a day, and offer to install a newer version.</summary>
    public bool CheckForExtensionUpdates { get; init; } = true;

    /// <summary>
    /// The model subagents run on. Null leaves it to the engine: <see cref="DefaultApiSubagentModel"/> on the Claude
    /// API, and Claude Code's own choice (or its CLAUDE_CODE_SUBAGENT_MODEL setting) on Claude Code.
    /// </summary>
    public ClaudeModel? SubagentModel { get; init; }

    /// <summary>What the Claude API engine's subagents use when <see cref="SubagentModel"/> isn't set: they only read and search, which Haiku does well for a fraction of the cost.</summary>
    public const ClaudeModel DefaultApiSubagentModel = ClaudeModel.Haiku;

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

    /// <summary>
    /// Saves only the settings that differ from <paramref name="before"/>, on top of what's saved now. Each chat window
    /// keeps its own copy, so saving it whole would undo what another window changed since this one loaded.
    /// </summary>
    public void SaveChangesFrom(AgentSettings before) => this.MergeInto(before, Load()).Save();

    /// <summary>The settings that differ from <paramref name="before"/>, applied to a copy of <paramref name="saved"/>.</summary>
    internal AgentSettings MergeInto(AgentSettings before, AgentSettings saved)
    {
        AgentSettings merged = saved with { };
        foreach (PropertyInfo property in typeof(AgentSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && !Equals(property.GetValue(this), property.GetValue(before)))
            {
                property.SetValue(merged, property.GetValue(this));
            }
        }

        return merged;
    }

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

    /// <summary>The effort's name in the API and on Claude Code's command line, e.g. "xhigh".</summary>
    public static string EffortApiName(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => "low",
        EffortLevel.Medium => "medium",
        EffortLevel.XHigh => "xhigh",
        EffortLevel.Max => "max",
        _ => "high",
    };

    /// <summary>The effort an API name stands for; null if it isn't one.</summary>
    public static EffortLevel? ParseEffortApiName(string name) => name switch
    {
        "low" => EffortLevel.Low,
        "medium" => EffortLevel.Medium,
        "high" => EffortLevel.High,
        "xhigh" => EffortLevel.XHigh,
        "max" => EffortLevel.Max,
        _ => null,
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
