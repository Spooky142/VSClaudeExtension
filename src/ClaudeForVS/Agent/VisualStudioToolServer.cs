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
/// A minimal MCP server (Streamable HTTP, JSON responses only) that gives Claude Code things only Visual Studio knows
/// (see <see cref="VisualStudioTools"/>). Only started while the Visual Studio tools are turned on; Claude Code's
/// permission requests don't come through it (see <see cref="ClaudeCodeEngine"/>). Bound to localhost under a random
/// path so other local processes can't find it by guessing.
/// </summary>
internal sealed class VisualStudioToolServer : IDisposable
{
    public const string ServerName = "visualstudio";

    /// <summary>How long Claude Code waits for one call (a build, or a rename waiting for the user's approval): 24 hours.</summary>
    public const int CallTimeoutMilliseconds = 24 * 60 * 60 * 1000;

    /// <summary>The Visual Studio tools, as Claude Code names them.</summary>
    public const string DiagnosticsTool = "mcp__" + ServerName + "__get_diagnostics";

    public const string BuildTool = "mcp__" + ServerName + "__build_solution";

    public const string DebugTool = "mcp__" + ServerName + "__get_debug_state";

    public const string ActiveDocumentTool = "mcp__" + ServerName + "__get_active_document";

    public const string CodeFixesTool = "mcp__" + ServerName + "__get_code_fixes";

    public const string ApplyCodeFixTool = "mcp__" + ServerName + "__apply_code_fix";

    public const string DefinitionTool = "mcp__" + ServerName + "__go_to_definition";

    public const string ReferencesTool = "mcp__" + ServerName + "__find_references";

    public const string SymbolsTool = "mcp__" + ServerName + "__find_symbols";

    public const string RenameTool = "mcp__" + ServerName + "__rename_symbol";

    public const string ListPackagesTool = "mcp__" + ServerName + "__list_packages";

    public const string AddPackageTool = "mcp__" + ServerName + "__add_package";

    public const string RemovePackageTool = "mcp__" + ServerName + "__remove_package";

    private readonly HttpListener listener;
    private readonly Func<string, JsonElement, Task<(string Text, bool IsError)>> runTool;

    /// <param name="runTool">Runs one of the Visual Studio tools by its short name (e.g. "get_diagnostics").</param>
    public VisualStudioToolServer(Func<string, JsonElement, Task<(string Text, bool IsError)>> runTool)
    {
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

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                await WriteReplyAsync(response, Failure(null, -32700, "Parse error")).ConfigureAwait(false);
                return;
            }

            JsonNode? id = request?["id"]?.DeepClone();
            if (id is null)
            {
                response.StatusCode = 202; // notification, e.g. notifications/initialized
                return;
            }

            // Whatever goes wrong from here (a cancelled build, a COM error from Visual Studio, an odd request), Claude
            // Code gets a JSON-RPC error for this id, not an empty reply it can't make sense of.
            JsonObject reply;
            try
            {
                string method = request?["method"]?.GetValue<string>() ?? string.Empty;
                JsonNode? result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = request?["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                    },
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = new JsonArray(VisualStudioTools.Definitions()) },
                    "tools/call" => await this.CallToolAsync(request?["params"]).ConfigureAwait(false),
                    _ => null,
                };

                reply = result is null
                    ? Failure(id, -32601, "Method not found")
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (Exception ex)
            {
                reply = Failure(id.DeepClone(), -32603, ex is OperationCanceledException ? "The request was cancelled." : ex.Message);
            }

            await WriteReplyAsync(response, reply).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The connection went; there's no one to answer.
            try { response.StatusCode = 400; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static JsonObject Failure(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteReplyAsync(HttpListenerResponse response, JsonObject reply)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    private async Task<JsonNode> CallToolAsync(JsonNode? parameters)
    {
        string called = parameters?["name"] is JsonValue n && n.TryGetValue(out string? calledName) ? calledName! : string.Empty;
        (string text, bool isError) result;
        try
        {
            using JsonDocument argsDoc = JsonDocument.Parse(parameters?["arguments"]?.ToJsonString() ?? "{}");
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
}

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
                "errors in files open in the editor. Use it once you've finished a set of edits to check for mistakes, before or instead of a full build. " +
                "Files with uncommitted changes are listed first.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Only list problems in this file or folder (relative to the repository root)." },
                    ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("error", "warning"), ["description"] = "\"error\" (default) for errors only; \"warning\" for errors and warnings." },
                },
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "build_solution",
            ["description"] =
                "Build the solution open in Visual Studio (Build > Build Solution), wait for it to finish, and return the result with the " +
                "Error List's errors (and a count of its warnings; list them with get_diagnostics). Works for projects `dotnet build` can't build, such as C++ or older .NET Framework ones.",
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
        new JsonObject
        {
            ["name"] = "get_active_document",
            ["description"] =
                "See where the user is in Visual Studio's editor: the file they last worked in (and whether it has unsaved changes), the caret's " +
                "line and column, the lines in view, the selected text, and the other open files. Use it when the user says \"this\", \"here\" or " +
                "\"the selected code\" without naming a file.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "get_code_fixes",
            ["description"] =
                "List the compiler errors and warnings (and those from the project's analyzer packages) on a line of a C# or Visual Basic file, " +
                "with the fixes Visual Studio's light bulb offers for each, e.g. adding a missing using, implementing an interface or generating a " +
                "method. Then apply one with apply_code_fix instead of editing by hand.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file, relative to the repository root." },
                    ["line"] = new JsonObject { ["type"] = "integer", ["description"] = "The line (1-based) with the problem." },
                    ["diagnostic"] = new JsonObject { ["type"] = "string", ["description"] = "Only this diagnostic ID, e.g. \"CS0246\". Default: every one on the line." },
                },
                ["required"] = new JsonArray("path", "line"),
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "apply_code_fix",
            ["description"] =
                "Apply one of the fixes get_code_fixes listed, the way choosing it from Visual Studio's light bulb does. The user approves the " +
                "changes to every file at once.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The file, relative to the repository root." },
                    ["line"] = new JsonObject { ["type"] = "integer", ["description"] = "The line (1-based) with the problem." },
                    ["diagnostic"] = new JsonObject { ["type"] = "string", ["description"] = "The diagnostic ID the fix is for, e.g. \"CS0246\"." },
                    ["fix"] = new JsonObject { ["type"] = "string", ["description"] = "The fix's title exactly as get_code_fixes listed it." },
                },
                ["required"] = new JsonArray("path", "line", "diagnostic", "fix"),
            },
        },
        new JsonObject
        {
            ["name"] = "go_to_definition",
            ["description"] =
                "Find where a symbol is declared, using Visual Studio's code model (Roslyn) for C# and Visual Basic: it understands overloads, " +
                "namespaces and inheritance, which a text search doesn't. Point at the symbol with a file, a line and the name as written on that " +
                "line. Returns the symbol's kind and signature, where it's declared (file and line) and its documentation summary.",
            ["inputSchema"] = SymbolSchema(),
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "find_references",
            ["description"] =
                "List every line that refers to a symbol across the whole solution, using Visual Studio's code model (Roslyn): usages of this exact " +
                "method, property or type, not other things that share its name. Use it before changing a signature or deleting code.",
            ["inputSchema"] = SymbolSchema(withLimit: true),
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "find_symbols",
            ["description"] =
                "Search the solution's declarations by name using Visual Studio's code model (Roslyn), like Go To All: types, methods, properties, " +
                "fields and events whose names match `query` (fuzzy). Or give a `path` without a query to list the types and members declared in one file.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Part of the name to search for." },
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("any", "type", "method", "property", "field", "event") },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Without a query: the file to outline, relative to the repository root." },
                    ["max_results"] = new JsonObject { ["type"] = "integer" },
                },
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "rename_symbol",
            ["description"] =
                "Rename a symbol everywhere it's used, using Visual Studio's code model (Roslyn): overloads, interface implementations and references " +
                "in other files are updated and unrelated names aren't touched. Prefer it to editing each file when renaming. Point at the symbol with a " +
                "file, a line and the name as written on that line. The user approves the changes to every file at once.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A file that mentions the symbol, relative to the repository root." },
                    ["line"] = new JsonObject { ["type"] = "integer", ["description"] = "The line (1-based) where the symbol appears." },
                    ["symbol"] = new JsonObject { ["type"] = "string", ["description"] = "The symbol's current name as written on that line." },
                    ["new_name"] = new JsonObject { ["type"] = "string", ["description"] = "The new name: an identifier." },
                },
                ["required"] = new JsonArray("path", "line", "symbol", "new_name"),
            },
        },
        new JsonObject
        {
            ["name"] = "list_packages",
            ["description"] = "List the NuGet packages each project in the solution open in Visual Studio references directly, with versions.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project"] = new JsonObject { ["type"] = "string", ["description"] = "Only this project. Default: all of them." },
                },
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        },
        new JsonObject
        {
            ["name"] = "add_package",
            ["description"] =
                "Add a NuGet package to a project, or change the version of one it already references, through Visual Studio's NuGet so the project is " +
                "restored the way the Manage NuGet Packages window does it. Prefer it to editing the project file or running `dotnet add package`. " +
                "Leave the version out for the latest stable release.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project"] = new JsonObject { ["type"] = "string", ["description"] = "The project's name in the solution (see list_packages)." },
                    ["package"] = new JsonObject { ["type"] = "string", ["description"] = "The NuGet package ID, e.g. \"Newtonsoft.Json\"." },
                    ["version"] = new JsonObject { ["type"] = "string", ["description"] = "An exact version. Default: the latest stable release." },
                },
                ["required"] = new JsonArray("project", "package"),
            },
        },
        new JsonObject
        {
            ["name"] = "remove_package",
            ["description"] = "Remove a NuGet package from a project through Visual Studio's NuGet.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["project"] = new JsonObject { ["type"] = "string", ["description"] = "The project's name in the solution (see list_packages)." },
                    ["package"] = new JsonObject { ["type"] = "string", ["description"] = "The NuGet package ID to remove." },
                },
                ["required"] = new JsonArray("project", "package"),
            },
        },
    ];

    /// <summary>The input of a tool that points at a symbol: a file, a line and the name written on it.</summary>
    private static JsonObject SymbolSchema(bool withLimit = false)
    {
        var properties = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "A file that mentions the symbol, relative to the repository root." },
            ["line"] = new JsonObject { ["type"] = "integer", ["description"] = "The line (1-based) where the symbol appears." },
            ["symbol"] = new JsonObject { ["type"] = "string", ["description"] = "The symbol's name as written on that line, e.g. \"GetTotal\"." },
        };
        if (withLimit)
        {
            properties["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "Most lines to list. Default 100." };
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("path", "line", "symbol") };
    }
}
