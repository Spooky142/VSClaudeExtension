using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>
/// A minimal MCP server (Streamable HTTP, JSON responses only) for Claude Code. Its <c>approve</c> tool is what Claude
/// Code calls through <c>--permission-prompt-tool</c> whenever it needs permission; its other tools give Claude
/// things only Visual Studio knows (see <see cref="VisualStudioTools"/>). Bound to localhost under a random path so
/// other local processes can't find it by guessing.
/// </summary>
internal sealed class ApprovalServer : IDisposable
{
    public const string ServerName = "visualstudio";
    public const string ToolName = "approve";

    /// <summary>The name Claude Code knows the tool by.</summary>
    public const string QualifiedToolName = "mcp__" + ServerName + "__" + ToolName;

    /// <summary>The Visual Studio tools offered alongside <c>approve</c>, as Claude Code names them.</summary>
    public const string DiagnosticsTool = "mcp__" + ServerName + "__get_diagnostics";

    public const string BuildTool = "mcp__" + ServerName + "__build_solution";

    public const string DebugTool = "mcp__" + ServerName + "__get_debug_state";

    private readonly HttpListener listener;
    private readonly Func<string, JsonElement, string?, Task<PermissionDecision>> decide;
    private readonly Func<string, JsonElement, Task<(string Text, bool IsError)>> runTool;

    /// <param name="decide">Called with Claude Code's tool name, input and tool call ID (if given); returns the user's decision.</param>
    /// <param name="runTool">Runs one of the Visual Studio tools by its short name (e.g. "get_diagnostics").</param>
    public ApprovalServer(Func<string, JsonElement, string?, Task<PermissionDecision>> decide, Func<string, JsonElement, Task<(string Text, bool IsError)>> runTool)
    {
        this.decide = decide;
        this.runTool = runTool;
        byte[] secret = new byte[18];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(secret);
        }

        string token = Convert.ToBase64String(secret).Replace('+', '-').Replace('/', '_');
        var random = new Random();
        for (int attempt = 0; ; attempt++)
        {
            int port = random.Next(49152, 65000);
            string prefix = $"http://localhost:{port}/{token}/";

            // A listener that failed to start can't be used again, so each attempt gets its own.
            var candidate = new HttpListener();
            try
            {
                candidate.Prefixes.Add(prefix);
                candidate.Start();
                this.listener = candidate;
                this.Url = prefix + "mcp";
                break;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                // Port in use; try another.
                candidate.Close();
            }
        }

        _ = Task.Run(this.AcceptLoopAsync);
    }

    public string Url { get; } = string.Empty;

    public void Dispose()
    {
        try
        {
            this.listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (this.listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // listener closed
            }

            _ = Task.Run(() => this.HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            if (context.Request.HttpMethod != "POST")
            {
                // No server-initiated streams; DELETE (session end) just succeeds.
                response.StatusCode = context.Request.HttpMethod == "DELETE" ? 200 : 405;
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            JsonNode? request = JsonNode.Parse(body);
            JsonNode? id = request?["id"]?.DeepClone();
            string method = request?["method"]?.GetValue<string>() ?? string.Empty;

            if (id is null)
            {
                response.StatusCode = 202; // notification, e.g. notifications/initialized
                return;
            }

            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = request?["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                },
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray([ToolDefinition(), .. VisualStudioTools.Definitions()]) },
                "tools/call" => await this.CallToolAsync(request?["params"]).ConfigureAwait(false),
                _ => null!,
            };

            JsonObject reply = result is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

            byte[] bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpListenerException or IOException or InvalidOperationException)
        {
            try { response.StatusCode = 400; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
    }

    private async Task<JsonNode> CallToolAsync(JsonNode? parameters)
    {
        JsonNode? args = parameters?["arguments"];
        string called = parameters?["name"] is JsonValue n && n.TryGetValue(out string? calledName) ? calledName! : ToolName;
        if (called != ToolName)
        {
            (string text, bool isError) result;
            try
            {
                using JsonDocument argsDoc = JsonDocument.Parse(args?.ToJsonString() ?? "{}");
                result = await this.runTool(called, argsDoc.RootElement.Clone()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ToolInputException or IOException or OperationCanceledException or System.Runtime.InteropServices.COMException)
            {
                result = (ex.Message, true);
            }

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.text }),
                ["isError"] = result.isError,
            };
        }

        string toolName = args?["tool_name"]?.GetValue<string>() ?? "unknown";
        JsonNode input = args?["input"]?.DeepClone() ?? new JsonObject();
        string? toolUseId = args?["tool_use_id"] is JsonValue id && id.TryGetValue(out string? value) ? value : null;

        PermissionDecision decision;
        using (JsonDocument doc = JsonDocument.Parse(input.ToJsonString()))
        {
            decision = await this.decide(toolName, doc.RootElement.Clone(), toolUseId).ConfigureAwait(false);
        }

        // Answered questions come back as a modified input (the original questions plus "answers").
        JsonNode updatedInput = decision.UpdatedInputJson is string json ? JsonNode.Parse(json) ?? input : input;
        JsonObject payload = decision.Allow
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updatedInput }
            : new JsonObject { ["behavior"] = "deny", ["message"] = decision.Message ?? "The user declined this action." };

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() }),
        };
    }

    private static JsonObject ToolDefinition() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Asks the Visual Studio user to approve or deny a tool call.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tool_name"] = new JsonObject { ["type"] = "string" },
                ["input"] = new JsonObject { ["type"] = "object" },
                ["tool_use_id"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("tool_name", "input"),
        },
    };
}

/// <param name="UpdatedInputJson">Replacement tool input to run with, as JSON; null runs the original input.</param>
internal readonly record struct PermissionDecision(bool Allow, string? Message = null, string? UpdatedInputJson = null);

/// <summary>The Visual Studio tools the extension's MCP server offers Claude Code: the Error List, and building the solution.</summary>
internal static class VisualStudioTools
{
    public static JsonNode[] Definitions() =>
    [
        new JsonObject
        {
            ["name"] = "get_diagnostics",
            ["description"] =
                "List the errors and warnings in Visual Studio's Error List: errors from the last build, and the live (IntelliSense) " +
                "errors in files open in the editor. Use it after editing code to check for mistakes, before or instead of a full build.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Only list problems in this file or folder (relative to the repository root)." },
                    ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("error", "warning"), ["description"] = "\"error\" for errors only; \"warning\" (default) for errors and warnings." },
                },
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "build_solution",
            ["description"] =
                "Build the solution open in Visual Studio (Build > Build Solution), wait for it to finish, and return the result with the " +
                "Error List's errors and warnings. Works for projects `dotnet build` can't build, such as C++ or older .NET Framework ones.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        },
        new JsonObject
        {
            ["name"] = "get_debug_state",
            ["description"] =
                "Read Visual Studio's debugger: whether a program is being debugged and, when it's paused at a breakpoint or an exception, " +
                "the exception, the call stack and the current frame's arguments and locals. Use it when the user is stuck on a bug they're " +
                "debugging; then read the source files in the stack with the usual tools.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["frames"] = new JsonObject { ["type"] = "integer", ["description"] = "How many stack frames to list (default 15, max 50)." },
                },
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
    ];
}
