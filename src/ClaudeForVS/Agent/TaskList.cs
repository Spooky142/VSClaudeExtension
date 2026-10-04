using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClaudeForVS.Agent;

/// <summary>
/// Claude's task list: Claude Code's TodoWrite tool, and the Claude API engine's <c>todo_write</c>, which takes the same
/// input (<c>{"todos": [{"content", "status", "activeForm"}]}</c>).
/// </summary>
internal static class TaskList
{
    /// <summary>The tasks in a TodoWrite/todo_write input; empty if there are none or the input isn't valid.</summary>
    public static IReadOnlyList<TaskItem> Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out JsonElement todos) || todos.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tasks = new List<TaskItem>();
        foreach (JsonElement todo in todos.EnumerateArray())
        {
            string content = Str(todo, "content").Trim();
            if (content.Length == 0)
            {
                continue;
            }

            string status = Str(todo, "status") switch
            {
                "completed" => "completed",
                "in_progress" => "in_progress",
                _ => "pending",
            };
            tasks.Add(new TaskItem(content, status, Str(todo, "activeForm").Trim()));
        }

        return tasks;
    }

    /// <summary>The list as text, one task per line with a checkbox: "[x] done", "[~] in progress", "[ ] to do".</summary>
    public static string Format(IReadOnlyList<TaskItem> tasks) => string.Join("\n", tasks.Select(t =>
        (t.Status switch { "completed" => "[x] ", "in_progress" => "[~] ", _ => "[ ] " }) + t.Content));

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
