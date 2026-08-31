using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniCli.Protocol;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// A loopback HTTP transport, so a client that is not the UniCli CLI — an MCP server, a CI
    /// script, another language — can drive the editor by POSTing a command. It exists to prove
    /// the transport seam: it feeds the same handler the named pipe does, and therefore inherits
    /// the single command slot, the preconditions and the undo grouping without knowing they are
    /// there.
    ///
    /// Scope: loopback only, one command per request, no authentication. That last point is a
    /// deliberate limit, not an oversight — anything reachable on 127.0.0.1 can run a command, so
    /// this is off by default and a bearer token is the obvious next step before it is anything
    /// more than a local convenience. See UniCliSettings.
    ///
    /// POST any path with a CommandRequest as the JSON body; the response is a CommandResponse.
    /// The port is written to Library/UniCli/http-port so a client can find it.
    /// </summary>
    public sealed class HttpLoopbackServer : ICommandTransport
    {
        // A small range so a stale listener from a crashed session does not wander far.
        private const int PortRangeStart = 17900;
        private const int PortRangeEnd = 17949;

        private readonly CommandReceivedHandler _onCommandReceived;
        private readonly Action<string> _logger;
        private readonly HttpListener _listener;
        private readonly TaskCompletionSource<bool> _shutdown = new();
        private readonly string _portFilePath;
        private int _disposed;

        public int Port { get; }

        public HttpLoopbackServer(CommandReceivedHandler onCommandReceived, Action<string> logger)
        {
            _onCommandReceived = onCommandReceived ?? throw new ArgumentNullException(nameof(onCommandReceived));
            _logger = logger ?? (_ => { });

            _listener = new HttpListener();
            Port = BindToFreePort(_listener);

            _portFilePath = Path.Combine("Library", "UniCli", "http-port");
            WritePortFile();

            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        /// <summary>
        /// Binds a loopback prefix on the first free port. Binds both the 127.0.0.1 and the
        /// localhost hostname rather than a wildcard: the Mono that ships with Unity 2022 throws
        /// for the "+" wildcard, and matches the request Host literally per prefix, so a client
        /// resolving "localhost" would be rejected by a "127.0.0.1"-only prefix. Two explicit
        /// loopback prefixes cover both without opening the listener to non-loopback callers.
        /// </summary>
        private static int BindToFreePort(HttpListener listener)
        {
            for (var port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    // Start/stop probe: Prefixes accepts anything; only Start reveals a taken port.
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch (HttpListenerException)
                {
                    // Port taken or not bindable; try the next.
                }
                catch (SocketException)
                {
                    // ditto
                }
            }

            throw new InvalidOperationException(
                $"HTTP transport: no free port in {PortRangeStart}-{PortRangeEnd}.");
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
                    // Listener stopped (dispose) or a transient accept error; either way, stop.
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
                    Respond(context, 403, ErrorResponse("This transport accepts loopback connections only."));
                    return;
                }

                // GET /events is the push side of the event stream: a client holds this open
                // instead of polling Events.Poll. It reads from the stream directly rather than
                // going through the command slot, so a subscriber cannot starve command traffic.
                if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                    && context.Request.Url != null
                    && context.Request.Url.AbsolutePath.TrimEnd('/').EndsWith("/events", StringComparison.Ordinal))
                {
                    await StreamEventsAsync(context);
                    return;
                }

                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Respond(context, 405, ErrorResponse("POST a CommandRequest as the JSON body, or GET /events."));
                    return;
                }

                string body;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    body = await reader.ReadToEndAsync();

                CommandRequest request;
                try
                {
                    request = JsonUtility.FromJson<CommandRequest>(body);
                }
                catch (Exception)
                {
                    request = null;
                }

                if (request == null || string.IsNullOrEmpty(request.command))
                {
                    Respond(context, 400, ErrorResponse("Body must be a CommandRequest with a 'command'."));
                    return;
                }

                // Bridge the callback-style handler to a Task the request can await, so the HTTP
                // response is the command's actual result rather than an acknowledgement.
                var completion = new TaskCompletionSource<CommandResponse>();
                _onCommandReceived(request, CancellationToken.None, r => completion.TrySetResult(r));
                var response = await completion.Task;

                Respond(context, 200, JsonUtility.ToJson(response));
            }
            catch (Exception ex)
            {
                _logger($"[UniCli] HTTP transport error: {ex.Message}");
                try { Respond(context, 500, ErrorResponse("Internal error handling the request.")); }
                catch { /* client already gone */ }
            }
        }

        /// <summary>
        /// Server-sent events. Anything already buffered after the client's cursor is delivered
        /// first, so a reconnecting client does not miss what happened while it was away, and new
        /// events follow as they are published.
        /// </summary>
        private async Task StreamEventsAsync(HttpListenerContext context)
        {
            long.TryParse(context.Request.QueryString["since"], out var since);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Add("Cache-Control", "no-cache");
            context.Response.SendChunked = true;

            var pending = new System.Collections.Concurrent.ConcurrentQueue<EditorEvent>();
            var signal = new SemaphoreSlim(0);

            void OnPublished(EditorEvent published)
            {
                pending.Enqueue(published);
                try { signal.Release(); } catch (ObjectDisposedException) { }
            }

            // Subscribe before draining the backlog: an event published in between is queued
            // rather than lost, and the sequence number lets the client ignore a duplicate.
            EditorEventStream.Published += OnPublished;

            try
            {
                foreach (var buffered in EditorEventStream.Since(since, null, 0, out _, out _))
                    await WriteEventAsync(context, buffered);

                while (true)
                {
                    // The wait doubles as a keep-alive tick: a comment frame both keeps
                    // intermediaries from closing an idle stream and reveals a client that left.
                    if (await signal.WaitAsync(TimeSpan.FromSeconds(15)))
                    {
                        while (pending.TryDequeue(out var published))
                            await WriteEventAsync(context, published);
                    }
                    else
                    {
                        await WriteRawAsync(context, ": keep-alive\n\n");
                    }
                }
            }
            catch (Exception)
            {
                // The client went away, or the listener stopped. Either way this stream is over.
            }
            finally
            {
                EditorEventStream.Published -= OnPublished;
                signal.Dispose();
                try { context.Response.OutputStream.Close(); } catch { }
            }
        }

        private static Task WriteEventAsync(HttpListenerContext context, EditorEvent published)
        {
            var payload = "{\"seq\":" + published.seq +
                          ",\"kind\":" + McpJson.Quote(published.kind) +
                          ",\"timestamp\":" + published.timestamp +
                          ",\"message\":" + McpJson.Quote(published.message) +
                          ",\"data\":" + McpJson.Quote(published.data ?? "") + "}";

            return WriteRawAsync(context, $"id: {published.seq}\nevent: {published.kind}\ndata: {payload}\n\n");
        }

        private static async Task WriteRawAsync(HttpListenerContext context, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            await context.Response.OutputStream.FlushAsync();
        }

        private static void Respond(HttpListenerContext context, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static string ErrorResponse(string message)
            => JsonUtility.ToJson(new CommandResponse { success = false, message = message, data = "" });

        private void WritePortFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_portFilePath));
                File.WriteAllText(_portFilePath, Port.ToString());
            }
            catch (IOException ex)
            {
                _logger($"[UniCli] HTTP transport could not write its port file: {ex.Message}");
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

            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _listener.Close(); } catch { /* already closed */ }

            try
            {
                if (File.Exists(_portFilePath))
                    File.Delete(_portFilePath);
            }
            catch (IOException)
            {
                // harmless; Library/ is disposable
            }

            _shutdown.TrySetResult(true);
        }
    }

    /// <summary>Creates <see cref="HttpLoopbackServer"/> instances for the server's transport list.</summary>
    public sealed class HttpTransportFactory : ICommandTransportFactory
    {
        private readonly Action<string> _logger;

        public HttpTransportFactory(Action<string> logger) => _logger = logger;

        public string Name => "http";

        public ICommandTransport Create(CommandReceivedHandler onCommandReceived)
            => new HttpLoopbackServer(onCommandReceived, _logger);
    }
}
