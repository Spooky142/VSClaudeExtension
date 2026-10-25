using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Git;

namespace ClaudeForVS.Agent.Mcp;

/// <summary>A tool offered by an MCP server.</summary>
/// <param name="ReadOnly">The server says it only reads (annotations.readOnlyHint).</param>
internal sealed record McpTool(string Name, string Description, IReadOnlyDictionary<string, JsonElement> InputSchema, bool ReadOnly);

/// <summary>What an MCP tool call returned, as text for Claude.</summary>
internal sealed record McpCallResult(string Text, bool IsError);

/// <summary>
/// A connection to one MCP server over stdio (newline-delimited JSON-RPC) or Streamable HTTP: initializes it, lists its
/// tools and calls them.
/// </summary>
internal sealed class McpClient : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const int MaxResultChars = 30_000;

    // How long the server gets to answer: a tool can do real work; setting up and listing tools shouldn't take long.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);

    private readonly string repoRoot;
    private readonly SemaphoreSlim restartGate = new(1, 1);
    private IMcpTransport transport;

    private McpClient(McpServerConfig config, string repoRoot, IMcpTransport transport)
    {
        this.Config = config;
        this.repoRoot = repoRoot;
        this.transport = transport;
    }

    public McpServerConfig Config { get; }

    public IReadOnlyList<McpTool> Tools { get; private set; } = [];

    /// <summary>Starts (or connects to) the server, initializes it and lists its tools.</summary>
    public static async Task<McpClient> StartAsync(McpServerConfig config, string repoRoot, CancellationToken cancellationToken)
    {
        var client = new McpClient(config, repoRoot, OpenTransport(config, repoRoot));
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            client.Tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static IMcpTransport OpenTransport(McpServerConfig config, string repoRoot) => config.Type switch
    {
        "stdio" => StdioTransport.Start(config, repoRoot),
        "http" => new HttpTransport(config),
        _ => throw new InvalidOperationException($"The \"{config.Type}\" transport isn't supported; use stdio or http."),
    };

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await this.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject { ["roots"] = new JsonObject() },
            ["clientInfo"] = new JsonObject { ["name"] = "claude-for-visual-studio", ["version"] = "1.0" },
        }, SetupTimeout, cancellationToken).ConfigureAwait(false);
        await this.transport.NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpCallResult> CallToolAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        await this.RestartIfExitedAsync(cancellationToken).ConfigureAwait(false);
        JsonNode result = await this.RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = JsonNode.Parse(JsonSerializer.Serialize(arguments)),
        }, CallTimeout, cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    /// <summary>
    /// Starts a stdio server again if it has exited (crashed, or stopped itself) since the last call, and initializes
    /// it. Its tools were listed when the chat started and stay as they were.
    /// </summary>
    private async Task RestartIfExitedAsync(CancellationToken cancellationToken)
    {
        if (!this.transport.HasExited)
        {
            return;
        }

        await this.restartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!this.transport.HasExited)
            {
                return;
            }

            IMcpTransport fresh = OpenTransport(this.Config, this.repoRoot);
            this.transport.Dispose();
            this.transport = fresh;
            try
            {
                await this.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                fresh.Dispose(); // counts as exited, so the next call tries again
                throw;
            }
        }
        finally
        {
            this.restartGate.Release();
        }
    }

    /// <summary>A request that fails with a <see cref="TimeoutException"/> if the server doesn't answer in time.</summary>
    private async Task<JsonNode> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            return await this.transport.RequestAsync(method, parameters, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The MCP server \"{this.Config.Name}\" didn't answer {method} within {(timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} minutes" : $"{timeout.TotalSeconds:0} seconds")}.");
        }
    }

    public void Dispose() => this.transport.Dispose();

    /// <summary>Turns a tools/call result into text: text blocks as they are, other content summarized.</summary>
    internal static McpCallResult ToResult(JsonNode? result)
    {
        bool isError = result?["isError"]?.GetValueKind() == JsonValueKind.True;
        var parts = new List<string>();
        if (result?["content"] is JsonArray content)
        {
            foreach (JsonNode? block in content)
            {
                string type = block?["type"]?.GetValueKind() == JsonValueKind.String ? block!["type"]!.GetValue<string>() : string.Empty;
                switch (type)
                {
                    case "text":
                        parts.Add(block!["text"]?.GetValue<string>() ?? string.Empty);
                        break;
                    case "resource" when ResourceText(block!) is string resourceText:
                        parts.Add(resourceText);
                        break;
                    case "resource_link":
                        parts.Add("[resource: " + block!["uri"]?.ToString() + "]");
                        break;
                    default:
                        parts.Add($"[{(type.Length > 0 ? type : "unknown")} content not shown]");
                        break;
                }
            }
        }

        if (parts.Count == 0 && result?["structuredContent"] is JsonNode structured)
        {
            parts.Add(structured.ToJsonString());
        }

        string joined = parts.Count == 0 ? "(no output)" : string.Join("\n", parts);
        if (joined.Length > MaxResultChars)
        {
            joined = joined.Substring(0, MaxResultChars) + $"\n[output truncated: {joined.Length - MaxResultChars} more characters]";
        }

        return new McpCallResult(joined, isError);
    }

    /// <summary>The text of an embedded text resource, or null.</summary>
    private static string? ResourceText(JsonNode block)
    {
        JsonNode? text = block["resource"]?["text"];
        return text?.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : null;
    }

    private async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        for (int page = 0; page < 20; page++)
        {
            JsonObject parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            JsonNode result = await this.RequestAsync("tools/list", parameters, SetupTimeout, cancellationToken).ConfigureAwait(false);
            if (result["tools"] is JsonArray list)
            {
                foreach (JsonNode? tool in list)
                {
                    if (tool?["name"] is JsonValue name && name.GetValueKind() == JsonValueKind.String)
                    {
                        using JsonDocument schemaDoc = JsonDocument.Parse(tool["inputSchema"]?.ToJsonString() ?? "{}");
                        var schema = new Dictionary<string, JsonElement>();
                        if (schemaDoc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty p in schemaDoc.RootElement.EnumerateObject())
                            {
                                schema[p.Name] = p.Value.Clone();
                            }
                        }

                        schema["type"] = JsonSerializer.SerializeToElement("object");
                        bool readOnly = tool["annotations"]?["readOnlyHint"]?.GetValueKind() == JsonValueKind.True;
                        string description = tool["description"]?.GetValueKind() == JsonValueKind.String ? tool["description"]!.GetValue<string>() : string.Empty;
                        tools.Add(new McpTool(name.GetValue<string>(), description, schema, readOnly));
                    }
                }
            }

            cursor = result["nextCursor"]?.GetValueKind() == JsonValueKind.String ? result["nextCursor"]!.GetValue<string>() : null;
            if (cursor is null)
            {
                break;
            }
        }

        return tools;
    }

    private interface IMcpTransport : IDisposable
    {
        /// <summary>True once a server process has exited (or been stopped); always false over HTTP.</summary>
        bool HasExited { get; }

        Task<JsonNode> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken);

        Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Tells the server a request was given up on (by the user, or for taking too long), so it can stop working on it.
    /// Best effort: the server may have gone. Initialize is never cancelled this way, as the protocol asks.
    /// </summary>
    private static void SendCancelled(IMcpTransport transport, string method, long id)
    {
        if (method == "initialize")
        {
            return;
        }

        _ = transport.NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "The request was cancelled." }, CancellationToken.None)
            .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    /// <summary>Thrown when the server answers a request with a JSON-RPC error.</summary>
    internal sealed class McpException(string message) : Exception(message);

    private static JsonNode ResultOrThrow(JsonNode? response)
    {
        if (response?["error"] is JsonNode error)
        {
            throw new McpException(error["message"]?.ToString() ?? error.ToJsonString());
        }

        return response?["result"] ?? new JsonObject();
    }

    /// <summary>A server run as a child process, talking JSON-RPC one message per line on stdin and stdout.</summary>
    private sealed class StdioTransport : IMcpTransport
    {
        private readonly Process process;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private readonly string repoRoot;
        private readonly StringBuilder stderr = new();
        private long nextId;
        private volatile bool exited;

        private StdioTransport(Process process, string repoRoot)
        {
            this.process = process;
            this.repoRoot = repoRoot;
            _ = Task.Run(this.ReadLoopAsync);
            _ = Task.Run(this.ReadErrorsAsync);
        }

        public static StdioTransport Start(McpServerConfig config, string repoRoot)
        {
            (string file, string arguments) = CommandLine(config.Command!, config.Args);
            var psi = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (KeyValuePair<string, string> variable in config.Env)
            {
                psi.EnvironmentVariables[variable.Key] = variable.Value;
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException($"Couldn't run {config.Command}: {ex.Message}");
            }

            Processes.CloseWithVisualStudio(process);
            return new StdioTransport(process, repoRoot);
        }

        public bool HasExited => this.exited;

        /// <summary>
        /// The program to start and its arguments. Commands like npx and uvx are batch files on Windows, which only
        /// cmd.exe can run, so those go through it.
        /// </summary>
        internal static (string File, string Arguments) CommandLine(string command, IReadOnlyList<string> args)
        {
            string quoted = string.Join(" ", args.Select(GitRunner.QuoteArgument));
            string? resolved = Resolve(command);
            string extension = Path.GetExtension(resolved ?? command).ToLowerInvariant();
            if (resolved is null || extension is ".cmd" or ".bat")
            {
                string inner = $"{GitRunner.QuoteArgument(resolved ?? command)} {quoted}".Trim();
                return ("cmd.exe", $"/d /s /c \"{inner}\"");
            }

            return (resolved, quoted);
        }

        /// <summary>Finds a command on PATH, trying PATHEXT's extensions; null if it isn't found.</summary>
        private static string? Resolve(string command)
        {
            string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Length > 0).ToArray();
            IEnumerable<string> candidates(string basePath) =>
                Path.HasExtension(basePath) ? [basePath, .. extensions.Select(e => basePath + e)] : extensions.Select(e => basePath + e);

            if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
            {
                return candidates(command).FirstOrDefault(File.Exists);
            }

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';').Where(d => d.Trim().Length > 0))
            {
                try
                {
                    if (candidates(Path.Combine(dir.Trim().Trim('"'), command)).FirstOrDefault(File.Exists) is string found)
                    {
                        return found;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }

        public async Task<JsonNode> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
        {
            long id = Interlocked.Increment(ref this.nextId);
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.pending[id] = tcs;
            try
            {
                await this.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken).ConfigureAwait(false);
                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                {
                    try
                    {
                        return ResultOrThrow(await tcs.Task.ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        SendCancelled(this, method, id);
                        throw;
                    }
                }
            }
            finally
            {
                this.pending.TryRemove(id, out _);
            }
        }

        public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken) =>
            this.SendAsync(parameters is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }
                : new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, cancellationToken);

        public void Dispose()
        {
            this.exited = true;
            try
            {
                this.process.StandardInput.Close();
                if (!this.process.WaitForExit(2000))
                {
                    Processes.KillTree(this.process);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
            }

            this.process.Dispose();
            this.Fail("The MCP server was stopped.");
        }

        private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(message.ToJsonString() + "\n");
            await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Stream stdin = this.process.StandardInput.BaseStream;
                await stdin.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                throw new InvalidOperationException("The MCP server has exited." + this.ErrorTail());
            }
            finally
            {
                this.writeGate.Release();
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                string? line;
                while ((line = await this.process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    // One odd message (a number where a string belongs, say) is skipped, rather than ending the
                    // connection and leaving every call after it waiting.
                    try
                    {
                        await this.HandleMessageAsync(line).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }

            this.exited = true;
            this.Fail("The MCP server exited." + this.ErrorTail());
        }

        private async Task HandleMessageAsync(string line)
        {
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                return; // servers sometimes log to stdout
            }

            if (message is not JsonObject obj)
            {
                return;
            }

            bool hasMethod = obj["method"] is JsonValue;
            if (!hasMethod && obj["id"] is JsonValue idValue && idValue.TryGetValue(out long id) && this.pending.TryGetValue(id, out var tcs))
            {
                tcs.TrySetResult(obj);
            }
            else if (hasMethod && obj["id"] is JsonNode requestId)
            {
                await this.AnswerServerRequestAsync(obj["method"]!.GetValue<string>(), requestId.DeepClone()).ConfigureAwait(false);
            }
        }

        /// <summary>Answers the few requests a server may send: ping, and the workspace roots (the repository).</summary>
        private Task AnswerServerRequestAsync(string method, JsonNode id)
        {
            JsonObject reply = method switch
            {
                "ping" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() },
                "roots/list" => new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JsonObject
                    {
                        ["roots"] = new JsonArray(new JsonObject
                        {
                            ["uri"] = new Uri(this.repoRoot.TrimEnd('\\') + "\\").AbsoluteUri.TrimEnd('/'),
                            ["name"] = Path.GetFileName(this.repoRoot.TrimEnd('\\')),
                        }),
                    },
                },
                _ => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } },
            };
            return this.SendAsync(reply, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        private async Task ReadErrorsAsync()
        {
            try
            {
                string? line;
                while ((line = await this.process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (this.stderr)
                    {
                        this.stderr.AppendLine(line);
                        if (this.stderr.Length > 4000)
                        {
                            this.stderr.Remove(0, this.stderr.Length - 4000);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        private string ErrorTail()
        {
            lock (this.stderr)
            {
                string text = this.stderr.ToString().Trim();
                return text.Length == 0 ? string.Empty : "\n" + (text.Length > 1000 ? text.Substring(text.Length - 1000) : text);
            }
        }

        private void Fail(string message)
        {
            foreach (var tcs in this.pending.Values)
            {
                tcs.TrySetException(new InvalidOperationException(message));
            }
        }
    }

    /// <summary>A server reached over Streamable HTTP: each message is a POST, answered with JSON or an event stream.</summary>
    private sealed class HttpTransport : IMcpTransport
    {
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private readonly McpServerConfig config;
        private string? sessionId;
        private bool initialized;
        private long nextId;

        public HttpTransport(McpServerConfig config)
        {
            this.config = config;
        }

        public bool HasExited => false;

        public async Task<JsonNode> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
        {
            long id = Interlocked.Increment(ref this.nextId);
            try
            {
                return await this.SendRequestAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SendCancelled(this, method, id);
                throw;
            }
        }

        private async Task<JsonNode> SendRequestAsync(long id, string method, JsonNode? parameters, CancellationToken cancellationToken)
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            using HttpResponseMessage response = await this.PostAsync(message, cancellationToken).ConfigureAwait(false);
            if (method == "initialize")
            {
                this.sessionId = response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null;
                this.initialized = true;
            }

            // On .NET Framework neither the token nor HttpClient's timeout covers reading the body, so a server that goes
            // quiet mid-stream would hang the chat; disposing the response ends the read.
            using CancellationTokenRegistration stop = cancellationToken.Register(response.Dispose);
            try
            {
                return await ReadReplyAsync(response, id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is IOException or ObjectDisposedException or HttpRequestException) && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static async Task<JsonNode> ReadReplyAsync(HttpResponseMessage response, long id, CancellationToken cancellationToken)
        {
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var data = new StringBuilder();
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        data.Append(line.Substring(5).TrimStart()).Append('\n');
                    }
                    else if (line.Length == 0 && data.Length > 0)
                    {
                        JsonNode? ev = TryParse(data.ToString());
                        data.Clear();
                        if (ev?["id"] is JsonValue evId && evId.TryGetValue(out long got) && got == id && ev["method"] is null)
                        {
                            return ResultOrThrow(ev);
                        }
                    }
                }

                throw new InvalidOperationException("The MCP server closed the stream without answering.");
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonNode? reply = TryParse(body);
            if (reply is JsonArray batch)
            {
                reply = batch.FirstOrDefault(r => r?["id"] is JsonValue v && v.TryGetValue(out long got) && got == id);
            }

            return ResultOrThrow(reply ?? throw new InvalidOperationException("The MCP server sent a reply that isn't JSON."));
        }

        public async Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
        {
            JsonObject message = parameters is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }
                : new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
            using HttpResponseMessage _ = await this.PostAsync(message, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (this.sessionId != null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, this.config.Url);
                    this.AddHeaders(request);
                    _ = this.http.SendAsync(request).ContinueWith(t => this.http.Dispose(), TaskScheduler.Default);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                }
            }

            this.http.Dispose();
        }

        private async Task<HttpResponseMessage> PostAsync(JsonObject message, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, this.config.Url)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            this.AddHeaders(request);

            HttpResponseMessage response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                int code = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException(code == 401
                    ? "The MCP server needs you to sign in (HTTP 401). Add an Authorization header in its configuration, or sign in through Claude Code."
                    : $"The MCP server answered HTTP {code}. {(detail.Length > 300 ? detail.Substring(0, 300) : detail)}".Trim());
            }

            return response;
        }

        private void AddHeaders(HttpRequestMessage request)
        {
            foreach (KeyValuePair<string, string> header in this.config.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (this.sessionId != null)
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", this.sessionId);
            }

            if (this.initialized)
            {
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
            }
        }

        private static JsonNode? TryParse(string text)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
