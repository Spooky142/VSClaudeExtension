using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClaudeForVS.Agent;

internal sealed record QuestionOption(string Label, string Description);

/// <summary>
/// One multiple-choice question from Claude: Claude Code's AskUserQuestion tool, or the API engine's ask_user tool,
/// which uses the same shape.
/// </summary>
internal sealed record UserQuestion(string Question, string Header, IReadOnlyList<QuestionOption> Options, bool MultiSelect)
{
    /// <summary>Reads the <c>questions</c> array from a tool input. Malformed entries are skipped.</summary>
    public static IReadOnlyList<UserQuestion> ParseAll(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("questions", out JsonElement questions)
            || questions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<UserQuestion>();
        foreach (JsonElement q in questions.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object || Str(q, "question") is not string text)
            {
                continue;
            }

            var options = new List<QuestionOption>();
            if (q.TryGetProperty("options", out JsonElement opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement o in opts.EnumerateArray())
                {
                    if (Str(o, "label") is string label)
                    {
                        options.Add(new QuestionOption(label, Str(o, "description") ?? string.Empty));
                    }
                }
            }

            bool multi = q.TryGetProperty("multiSelect", out JsonElement m) && m.ValueKind == JsonValueKind.True;
            result.Add(new UserQuestion(text, Str(q, "header") ?? string.Empty, options, multi));
        }

        return result;
    }

    /// <summary>A readable summary of the answers, for tool results and the transcript.</summary>
    public static string Describe(IReadOnlyList<UserQuestion> questions, IReadOnlyDictionary<string, string> answers) =>
        string.Join("\n", questions.Select(q => $"{q.Question} → {(answers.TryGetValue(q.Question, out string? a) ? a : "(no answer)")}"));

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
