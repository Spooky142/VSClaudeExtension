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

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>API model ID.</summary>
    public static string ModelId(ClaudeModel model) => model switch
    {
        ClaudeModel.Sonnet => "claude-sonnet-5-5",
        ClaudeModel.Haiku => "claude-haiku-4-5",
        _ => "claude-opus-5-5",
    };

    /// <summary>Context window in tokens.</summary>
    public static long ContextWindow(ClaudeModel model) => model == ClaudeModel.Haiku ? 200_000 : 1_000_000;

    /// <summary>Short display name, e.g. "Opus 5.5".</summary>
    public static string ModelName(ClaudeModel model) => model switch
    {
        ClaudeModel.Sonnet => "Sonnet 5.5",
        ClaudeModel.Haiku => "Haiku 4.5",
        _ => "Opus 5.5",
    };

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

    /// <summary>Appended to the system prompt so Claude knows what it's allowed to do.</summary>
    public static string ModeInstructions(AgentMode mode) => mode switch
    {
        AgentMode.Plan =>
            "You are in Plan mode: review the code and suggest changes, but don't edit files or run commands that " +
            "change the repository. Describe proposed changes (with code snippets where useful) for the user to apply.",
        AgentMode.Auto => "You are in Auto mode: edits and commands run without asking the user first, so be careful with destructive actions.",
        _ => "You are in Coding mode: the user approves each edit and command before it runs.",
    };

    /// <summary>
    /// Haiku 4.5 has no effort parameter; it takes an explicit thinking budget instead. Low means no thinking.
    /// Every budget stays below the 64K max_tokens used for Haiku.
    /// </summary>
    public static int? HaikuThinkingBudget(EffortLevel effort) => effort switch
    {
        EffortLevel.Low => null,
        EffortLevel.Medium => 4_000,
        EffortLevel.High => 10_000,
        EffortLevel.XHigh => 20_000,
        _ => 32_000,
    };
}
