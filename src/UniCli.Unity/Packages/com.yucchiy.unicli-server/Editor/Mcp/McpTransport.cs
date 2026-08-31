using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniCli.Protocol;
using UnityEngine;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Speaks MCP, so an AI client can drive the editor directly instead of through the CLI.
    ///
    /// It is a transport like any other: it decodes a tool call into a <see cref="CommandRequest"/>
    /// and hands it to the same handler the named pipe feeds, which means it inherits the single
    /// command slot, the declared preconditions and the undo grouping without knowing they exist.
    /// The command surface is not reimplemented for MCP; it is described from the same metadata
    /// <c>Commands.List</c> returns.
    ///
    /// Implements the subset a tool server needs — <c>initialize</c>, <c>tools/list</c>,
    /// <c>tools/call</c> — over JSON-RPC on loopback HTTP. Not the official SDK: that would add a
    /// dependency tree to a package that has none, to gain protocol surface this does not use.
    ///
    /// <b>Scope and compliance.</b> Loopback only, off unless <c>UNICLI_MCP=1</c>, and it never
    /// reaches Unity's cloud services — it drives the local editor and nothing else. Unity's Terms
    /// of Service §17.2(ff) names MCP servers specifically among the automated callers that need
    /// Authorized Agentic Access to reach Unity *offerings*; keeping this strictly local is a
    /// deliberate boundary, not an accident of scope. There is no authentication yet, so anything
    /// already on the machine can call it while it is enabled.
    /// </summary>
    public sealed class McpTransport : ICommandTransport
    {
        private const int PortRangeStart = 17960;
        private const int PortRangeEnd = 17989;

        /// <summary>Echoed back to a client that asks for something we do not know.</summary>
        private const string FallbackProtocolVersion = "2024-11-05";

        private readonly CommandReceivedHandler _onCommandReceived;
        private readonly Action<string> _logger;
        private readonly HttpListener _listener;
        private readonly TaskCompletionSource<bool> _shutdown = new();
        private readonly string _portFilePath;
        private int _disposed;

        public int Port { get; }

        public McpTransport(CommandReceivedHandler onCommandReceived, Action<string> logger)
        {
            _onCommandReceived = onCommandReceived ?? throw new ArgumentNullException(nameof(onCommandReceived));
            _logger = logger ?? (_ => { });

            _listener = new HttpListener();
            Port = BindToFreePort(_listener);

            _portFilePath = Path.Combine("Library", "UniCli", "mcp-port");
            WritePortFile();

            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        // Both loopback hostnames: Mono matches the Host header per prefix, so binding only
        // 127.0.0.1 would reject a client that resolved "localhost", and the "+" wildcard throws
        // outright on the Mono Unity 2022 ships.
        private static int BindToFreePort(HttpListener listener)
        {
            for (var port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch (HttpListenerException) { }
                catch (SocketException) { }
            }

            throw new InvalidOperationException($"MCP transport: no free port in {PortRangeStart}-{PortRangeEnd}.");
        }

        private async Task AcceptLoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(context));
            }

            _shutdown.TrySetResult(true);
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (!IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
                {
                    Respond(context, 403, "");
                    return;
                }

                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Respond(context, 405, "");
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream,
                           context.Request.ContentEncoding ?? Encoding.UTF8))
                    body = await reader.ReadToEndAsync();

                var response = await HandleRpcAsync(body);

                // Notifications carry no id and get no reply; MCP clients send at least one.
                if (response == null)
                {
                    Respond(context, 202, "");
                    return;
                }

                Respond(context, 200, response);
            }
            catch (Exception ex)
            {
                _logger($"[UniCli] MCP transport error: {ex.Message}");
                try { Respond(context, 500, ""); } catch { }
            }
        }

        /// <summary>Routes one JSON-RPC message. Returns null for notifications.</summary>
        internal async Task<string> HandleRpcAsync(string body)
        {
            var method = McpJson.ExtractString(body, "method");
            if (method == null)
                return Error(null, -32700, "Parse error: no method.");

            var id = McpJson.ExtractRaw(body, "id");
            if (id == null)
                return null;   // notification, e.g. notifications/initialized

            switch (method)
            {
                case "initialize":
                    return Result(id, BuildInitializeResult(body));

                case "tools/list":
                    return Result(id, await BuildToolListAsync());

                case "tools/call":
                    return await HandleToolCallAsync(id, body);

                case "ping":
                    return Result(id, "{}");

                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }

        private string BuildInitializeResult(string body)
        {
            // Echo the client's protocol version when it names one: the negotiated version should
            // be theirs if we can speak it, and this server's surface is version-stable.
            var requested = McpJson.ExtractString(McpJson.ExtractRaw(body, "params") ?? "", "protocolVersion");
            var version = string.IsNullOrEmpty(requested) ? FallbackProtocolVersion : requested;

            return "{\"protocolVersion\":" + McpJson.Quote(version) +
                   ",\"capabilities\":{\"tools\":{}}" +
                   ",\"serverInfo\":{\"name\":\"unipipe\",\"version\":\"1.0\"}}";
        }

        private async Task<string> BuildToolListAsync()
        {
            var listing = await RunCommandAsync("Commands.List", null);
            var commands = ParseCommandList(listing);
            return McpToolSurface.BuildToolList(commands);
        }

        private static CommandInfo[] ParseCommandList(CommandResponse response)
        {
            if (response == null || !response.success || string.IsNullOrEmpty(response.data))
                return Array.Empty<CommandInfo>();

            try
            {
                // Commands.List already returns {"commands":[...]}, which is exactly the shape
                // CommandListResponse describes.
                return JsonUtility.FromJson<CommandListResponse>(response.data)?.commands
                       ?? Array.Empty<CommandInfo>();
            }
            catch (Exception)
            {
                return Array.Empty<CommandInfo>();
            }
        }

        private async Task<string> HandleToolCallAsync(string id, string body)
        {
            var parameters = McpJson.ExtractRaw(body, "params") ?? "";
            var toolName = McpJson.ExtractString(parameters, "name");
            if (string.IsNullOrEmpty(toolName))
                return Error(id, -32602, "tools/call needs a tool name.");

            var arguments = McpJson.ExtractRaw(parameters, "arguments");

            string commandName;
            if (toolName == McpToolSurface.RunCommandTool)
            {
                commandName = McpJson.ExtractString(arguments ?? "", "command");
                if (string.IsNullOrEmpty(commandName))
                    return Error(id, -32602, $"{McpToolSurface.RunCommandTool} needs a 'command'.");

                arguments = McpJson.ExtractRaw(arguments, "arguments");
            }
            else if (!McpToolSurface.IsCoreTool(toolName, out commandName))
            {
                return Error(id, -32602, $"Unknown tool: {toolName}");
            }

            var response = await RunCommandAsync(commandName, arguments);

            // A refused or failed command is a tool result with isError, not a protocol error:
            // the call reached the server and the model should see why it was refused.
            var text = response == null
                ? "No response from the editor."
                : response.success
                    ? (string.IsNullOrEmpty(response.data) ? response.message : response.data)
                    : response.message;

            return Result(id,
                "{\"content\":[{\"type\":\"text\",\"text\":" + McpJson.Quote(text ?? "") + "}]" +
                ",\"isError\":" + (response is { success: true } ? "false" : "true") + "}");
        }

        private Task<CommandResponse> RunCommandAsync(string command, string argumentsJson)
        {
            var completion = new TaskCompletionSource<CommandResponse>();
            var request = new CommandRequest
            {
                command = command,
                data = string.IsNullOrEmpty(argumentsJson) ? "" : argumentsJson,
                format = "json"
            };

            _onCommandReceived(request, CancellationToken.None, r => completion.TrySetResult(r));
            return completion.Task;
        }

        private static string Result(string id, string resultJson)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}";

        private static string Error(string id, int code, string message)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") +
               ",\"error\":{\"code\":" + code + ",\"message\":" + McpJson.Quote(message) + "}}";

        private static void Respond(HttpListenerContext context, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "");
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private void WritePortFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_portFilePath));
                File.WriteAllText(_portFilePath, Port.ToString());
            }
            catch (IOException ex)
            {
                _logger($"[UniCli] MCP transport could not write its port file: {ex.Message}");
            }
        }

        public Task WaitForShutdownAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => _shutdown.TrySetCanceled());
            return _shutdown.Task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }

            try
            {
                if (File.Exists(_portFilePath))
                    File.Delete(_portFilePath);
            }
            catch (IOException) { }

            _shutdown.TrySetResult(true);
        }
    }

    /// <summary>Creates <see cref="McpTransport"/> instances for the server's transport list.</summary>
    public sealed class McpTransportFactory : ICommandTransportFactory
    {
        private readonly Action<string> _logger;

        public McpTransportFactory(Action<string> logger) => _logger = logger;

        public string Name => "mcp";

        public ICommandTransport Create(CommandReceivedHandler onCommandReceived)
            => new McpTransport(onCommandReceived, _logger);
    }
}
