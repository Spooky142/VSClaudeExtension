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
/// A minimal MCP server (Streamable HTTP, JSON responses only) exposing one tool, <c>approve</c>, that Claude Code
/// calls through <c>--permission-prompt-tool</c> whenever it needs permission. Bound to localhost under a random
/// path so other local processes can't find it by guessing.
/// </summary>
internal sealed class ApprovalServer : IDisposable
{
    public const string ServerName = "vsapproval";
    public const string ToolName = "approve";

    /// <summary>The name Claude Code knows the tool by.</summary>
    public const string QualifiedToolName = "mcp__" + ServerName + "__" + ToolName;

    private readonly HttpListener listener = new();
    private readonly Func<string, JsonElement, Task<PermissionDecision>> decide;

    /// <param name="decide">Called with Claude Code's tool name and input; returns the user's decision.</param>
    public ApprovalServer(Func<string, JsonElement, Task<PermissionDecision>> decide)
    {
        this.decide = decide;
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
            try
            {
                this.listener.Prefixes.Clear();
                this.listener.Prefixes.Add(prefix);
                this.listener.Start();
                this.Url = prefix + "mcp";
                break;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                // Port in use; try another.
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
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(ToolDefinition()) },
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
        string toolName = args?["tool_name"]?.GetValue<string>() ?? "unknown";
        JsonNode input = args?["input"]?.DeepClone() ?? new JsonObject();

        PermissionDecision decision;
        using (JsonDocument doc = JsonDocument.Parse(input.ToJsonString()))
        {
            decision = await this.decide(toolName, doc.RootElement.Clone()).ConfigureAwait(false);
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
