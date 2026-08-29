using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniCli.Protocol;
using UnityEngine;

namespace UniCli.Server.Editor
{
    public sealed class PipeServer : ICommandTransport
    {
        private const byte AckByte = 0x01;
        private static readonly byte[] AckBuffer = { AckByte };

        private readonly string _pipeName;
        private readonly CommandReceivedHandler _onCommandReceived;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<bool> _shutdownTcs = new();
        private readonly Task _serverLoop;

        private readonly object _activeClientsLock = new();
        private readonly HashSet<Task> _activeClientTasks = new();

        private readonly object _acceptingServerLock = new();
        private NamedPipeServerStream _acceptingServer;

        private int _disposed;

        public PipeServer(string pipeName, CommandReceivedHandler onCommandReceived)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _onCommandReceived = onCommandReceived ?? throw new ArgumentNullException(nameof(onCommandReceived));

            _serverLoop = Task.Run(async () => await RunLoopAsync(_cts.Token));
        }

        public async Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => _shutdownTcs.TrySetCanceled());
            await _shutdownTcs.Task;
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await AcceptClientAsync(cancellationToken);
                }
                _shutdownTcs.TrySetResult(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _shutdownTcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _shutdownTcs.TrySetException(ex);
            }
        }

        private async Task AcceptClientAsync(CancellationToken cancellationToken)
        {
            var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            lock (_acceptingServerLock)
                _acceptingServer = server;

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
            }
            finally
            {
                lock (_acceptingServerLock)
                    _acceptingServer = null;
            }

            var clientTask = HandleClientAsync(server, cancellationToken);
            TrackClientTask(clientTask);
        }

        private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            await using (server)
            {
                try
                {
                    if (!await PerformHandshakeAsync(server, cancellationToken))
                        return;

                    while (!cancellationToken.IsCancellationRequested && server.IsConnected)
                    {
                        var request = await ReadRequestAsync(server, cancellationToken);
                        if (request == null)
                            break;

                        if (!await ProcessCommandAsync(server, request, cancellationToken))
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    UniCliEditorLog.LogError($"[UniCli] Client handling error: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private static async Task<CommandRequest> ReadRequestAsync(
            NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            int length;
            var lengthBuffer = ArrayPool<byte>.Shared.Rent(4);
            try
            {
                if (!await ReadExactAsync(server, lengthBuffer, 4, cancellationToken))
                    return null;

                length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > ProtocolConstants.MaxMessageSize)
                {
                    UniCliEditorLog.LogWarning($"[UniCli] Invalid request length: {length} bytes, closing connection");
                    return null;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(lengthBuffer);
            }

            var jsonBuffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (!await ReadExactAsync(server, jsonBuffer, length, cancellationToken))
                {
                    UniCliEditorLog.LogWarning($"[UniCli] Client disconnected while reading request body ({length} bytes)");
                    return null;
                }

                var json = Encoding.UTF8.GetString(jsonBuffer, 0, length);
                return JsonUtility.FromJson<CommandRequest>(json);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(jsonBuffer);
            }
        }

        private async Task<bool> ProcessCommandAsync(
            NamedPipeServerStream server, CommandRequest request, CancellationToken cancellationToken)
        {
            // Do NOT use 'using' here. The linked CTS must stay alive until all I/O
            // completion callbacks referencing its token have finished. Disposing it
            // while a native pipe I/O callback is in-flight causes
            // ObjectDisposedException on the ThreadPool I/O thread, which can crash
            // the Mono runtime during domain reload.
            var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var responseTcs = new TaskCompletionSource<CommandResponse>();
                _onCommandReceived(request, commandCts.Token, response => responseTcs.TrySetResult(response));

                await server.WriteAsync(AckBuffer, 0, 1, cancellationToken);
                await server.FlushAsync(cancellationToken);

                var monitorTask = MonitorDisconnectAsync(server, commandCts);
                await Task.WhenAny(responseTcs.Task, monitorTask);
                if (!responseTcs.Task.IsCompleted)
                {
                    commandCts.Cancel();
                    // Wait for monitor to finish so its I/O callback completes
                    // before we leave this scope.
                    try { await monitorTask; } catch { /* expected */ }
                    commandCts.Dispose();
                    return false;
                }

                commandCts.Cancel();
                // Wait for monitor to finish so its I/O callback completes.
                try { await monitorTask; } catch { /* expected */ }
                commandCts.Dispose();
                var commandResponse = await responseTcs.Task;

                await WriteResponseAsync(server, commandResponse, cancellationToken);
                return true;
            }
            catch (IOException ex)
            {
                UniCliEditorLog.LogWarning($"[UniCli] Client disconnected during response write for '{request.command}': {ex.Message}");
                return false;
            }
        }

        private static async Task WriteResponseAsync(
            NamedPipeServerStream server, CommandResponse response, CancellationToken cancellationToken)
        {
            var responseJson = JsonUtility.ToJson(response);
            var responseByteCount = Encoding.UTF8.GetByteCount(responseJson);
            var responseBuffer = ArrayPool<byte>.Shared.Rent(responseByteCount);
            try
            {
                Encoding.UTF8.GetBytes(responseJson, 0, responseJson.Length, responseBuffer, 0);
                var responseLengthBytes = BitConverter.GetBytes(responseByteCount);

                await server.WriteAsync(responseLengthBytes, 0, 4, cancellationToken);
                await server.WriteAsync(responseBuffer, 0, responseByteCount, cancellationToken);
                await server.FlushAsync(cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(responseBuffer);
            }
        }

        private static async Task<bool> PerformHandshakeAsync(
            NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            var recvBuffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.HandshakeSize);
            try
            {
                if (!await ReadExactAsync(server, recvBuffer, ProtocolConstants.HandshakeSize, cancellationToken))
                {
                    UniCliEditorLog.LogWarning("[UniCli] Client disconnected during handshake");
                    return false;
                }

                if (!ProtocolConstants.ValidateMagicBytes(recvBuffer))
                {
                    UniCliEditorLog.LogWarning("[UniCli] Handshake failed: invalid magic bytes from client");
                    return false;
                }

                var clientVersion = BitConverter.ToUInt16(recvBuffer, 4);
                if (clientVersion != ProtocolConstants.ProtocolVersion)
                {
                    UniCliEditorLog.LogWarning(
                        $"[UniCli] Protocol version mismatch (server: {ProtocolConstants.ProtocolVersion}, client: {clientVersion}). "
                        + "Please update unicli or the Unity server package.");
                    return false;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recvBuffer);
            }

            var sendBuffer = ProtocolConstants.BuildHandshakeBuffer();

            await server.WriteAsync(sendBuffer, 0, ProtocolConstants.HandshakeSize, cancellationToken);
            await server.FlushAsync(cancellationToken);

            return true;
        }

        private static async Task<bool> ReadExactAsync(
            NamedPipeServerStream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var bytesRead = await stream.ReadAsync(buffer, totalRead, count - totalRead, cancellationToken);
                if (bytesRead == 0)
                    return false;
                totalRead += bytesRead;
            }
            return true;
        }

        private static async Task MonitorDisconnectAsync(NamedPipeServerStream server, CancellationTokenSource commandCts)
        {
            try
            {
                var buffer = new byte[1];
                var bytesRead = await server.ReadAsync(buffer, 0, 1, commandCts.Token);
                if (bytesRead == 0)
                    commandCts.Cancel();
            }
            catch (OperationCanceledException) { }
            catch (Exception) { commandCts.Cancel(); }
        }

        private void TrackClientTask(Task task)
        {
            lock (_activeClientsLock)
                _activeClientTasks.Add(task);

            task.ContinueWith(_ =>
            {
                lock (_activeClientsLock)
                    _activeClientTasks.Remove(task);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private Task[] GetActiveClientTasks()
        {
            lock (_activeClientsLock)
                return _activeClientTasks.ToArray();
        }

        private void DisposeAcceptingServer()
        {
            lock (_acceptingServerLock)
            {
                try
                {
                    _acceptingServer?.Dispose();
                }
                catch
                {
                    // Disposing the accepting server to unblock WaitForConnectionAsync
                }
                _acceptingServer = null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _cts.Cancel();
            DisposeAcceptingServer();

            var tasks = GetActiveClientTasks();
            var allTasks = new Task[tasks.Length + 1];
            allTasks[0] = _serverLoop;
            Array.Copy(tasks, 0, allTasks, 1, tasks.Length);

            try
            {
                Task.WaitAll(allTasks, TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
            {
                // Expected during shutdown
            }

            _cts.Dispose();
        }
    }
}
