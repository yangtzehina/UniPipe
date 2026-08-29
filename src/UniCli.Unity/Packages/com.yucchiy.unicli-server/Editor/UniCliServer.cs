#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UniCli.Protocol;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// UniCli Server (pure C# implementation)
    /// Unity-independent server logic
    /// </summary>
    public sealed class UniCliServer : IDisposable
    {
        private readonly string _pipeName;
        private CommandDispatcher _dispatcher;
        private readonly ConcurrentQueue<(CommandRequest request, CancellationToken cancellationToken, Action<CommandResponse> callback)> _commandQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Action<string> _logger;
        private readonly Action<string> _errorLogger;
        private readonly Task _serverLoop;
        private Task? _currentCommand;
        private CancellationTokenSource? _currentCommandCts;

        // The command that has been accepted but not yet picked up by the editor update pump.
        // Without this, a request arriving in that window is refused by a command nobody can
        // name — the queue is non-empty while CurrentCommandName is still null.
        private string? _queuedCommandName;

        // Set once per command after cancellation is requested but the command keeps running,
        // so a command that ignores its token is reported instead of just appearing to hang.
        private bool _reportedUncooperativeCancel;

        private readonly object _pipeServerLock = new();
        private PipeServer? _currentPipeServer;

        public string? CurrentCommandName { get; private set; }
        public DateTime? CurrentCommandStartTime { get; private set; }
        public string[] QueuedCommandNames => _commandQueue.ToArray().Select(item => item.request.command).ToArray();

        public UniCliServer(
            string pipeName,
            CommandDispatcher dispatcher,
            Action<string> logger,
            Action<string> errorLogger)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorLogger = errorLogger ?? throw new ArgumentNullException(nameof(errorLogger));

            _commandQueue = new ConcurrentQueue<(CommandRequest, CancellationToken, Action<CommandResponse>)>();
            _cts = new CancellationTokenSource();

            _serverLoop = Task.Run(
                async () => await RunServerLoopAsync(_cts.Token),
                _cts.Token);
        }

        private void Stop()
        {
            _cts.Cancel();
            _currentCommandCts?.Cancel();

            // Directly dispose the PipeServer to ensure all ThreadPool tasks are stopped
            // before domain reload proceeds. This is critical because the indirect disposal
            // via the using block in RunServerLoopAsync may not complete in time.
            PipeServer? pipeServer;
            lock (_pipeServerLock)
            {
                pipeServer = _currentPipeServer;
                _currentPipeServer = null;
            }
            pipeServer?.Dispose();

            try
            {
                var tasks = _currentCommand is { IsCompleted: false }
                    ? new[] { _serverLoop, _currentCommand }
                    : new[] { _serverLoop };
                Task.WaitAll(tasks, TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException)
            {
                // Expected during shutdown (OperationCanceledException etc.)
            }
        }

        public void ReplaceDispatcher(CommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public void ProcessCommands()
        {
            if (_currentCommand is { IsCompleted: false })
            {
                ReportUncooperativeCancellation();
                return;
            }

            _currentCommandCts?.Dispose();
            _currentCommandCts = null;
            _currentCommand = null;
            // Cleared on completion: leaving the last command's name behind makes a later
            // "server is busy" refusal name a command that already finished.
            CurrentCommandName = null;
            _reportedUncooperativeCancel = false;

            if (_commandQueue.TryDequeue(out var item))
            {
                var (request, cancellationToken, callback) = item;
                var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
                _currentCommandCts = commandCts;
                CurrentCommandName = request.command;
                _queuedCommandName = null;
                CurrentCommandStartTime = DateTime.UtcNow;
                _currentCommand = ProcessCommandAsync(request, commandCts.Token, callback);
            }
        }

        private async Task RunServerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var pipeServer = new PipeServer(
                        _pipeName,
                        OnCommandReceived);

                    lock (_pipeServerLock)
                        _currentPipeServer = pipeServer;

                    try
                    {
                        await pipeServer.WaitForShutdownAsync(cancellationToken);
                    }
                    finally
                    {
                        lock (_pipeServerLock)
                        {
                            if (_currentPipeServer == pipeServer)
                                _currentPipeServer = null;
                        }
                        pipeServer.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _errorLogger($"[UniCli] Server error: {ex.Message}");
                }

                try
                {
                    if (!cancellationToken.IsCancellationRequested)
                        await Task.Delay(2000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// How long a cancelled command may keep running before we say so. Cancellation in .NET
        /// is cooperative: a handler that never checks its token cannot be stopped, and the
        /// server's own timeouts only bound how long a *caller* waits. Reporting is what turns
        /// that from an unexplained hang into a named command.
        /// </summary>
        internal static readonly TimeSpan UncooperativeCancelGrace = TimeSpan.FromSeconds(5);

        private void ReportUncooperativeCancellation()
        {
            if (_reportedUncooperativeCancel)
                return;
            if (_currentCommandCts is not { IsCancellationRequested: true })
                return;

            if (CurrentCommandStartTime is not { } startedUtc)
                return;

            var elapsed = DateTime.UtcNow - startedUtc;
            if (elapsed < UncooperativeCancelGrace)
                return;

            _reportedUncooperativeCancel = true;
            _logger($"[UniCli] Command '{CurrentCommandName}' was cancelled but is still running " +
                    $"after {elapsed.TotalSeconds:F1}s — it does not observe its CancellationToken. " +
                    "The editor stays occupied until it returns on its own.");
        }

        /// <summary>
        /// Builds the refusal for a request that arrives while the single command slot is taken.
        /// Separated out because the slot has two occupied states and the interesting one is the
        /// short window where a command is queued but the editor update pump has not picked it
        /// up yet — reporting "unknown" there tells the caller nothing.
        /// </summary>
        internal static string DescribeBusyState(string? runningCommand, string? queuedCommand, DateTime? startedUtc, DateTime nowUtc)
        {
            if (runningCommand != null)
            {
                var elapsed = startedUtc is { } started ? nowUtc - started : TimeSpan.Zero;
                var howLong = elapsed > TimeSpan.Zero ? $" (running for {elapsed.TotalSeconds:F1}s)" : "";
                return $"Server is busy executing '{runningCommand}'{howLong}. " +
                       "Please retry after the current command completes.";
            }

            if (queuedCommand != null)
                return $"Server is busy: '{queuedCommand}' is queued and about to start. " +
                       "Please retry after the current command completes.";

            return "Server is busy with another command. Please retry after the current command completes.";
        }

        private void OnCommandReceived(CommandRequest request, CancellationToken cancellationToken, Action<CommandResponse> callback)
        {
            if (_currentCommand is { IsCompleted: false } || !_commandQueue.IsEmpty)
            {
                callback(new CommandResponse
                {
                    success = false,
                    message = DescribeBusyState(CurrentCommandName, _queuedCommandName, CurrentCommandStartTime, DateTime.UtcNow),
                    data = ""
                });
                return;
            }

            _queuedCommandName = request.command;
            _commandQueue.Enqueue((request, cancellationToken, callback));
        }

        private async Task ProcessCommandAsync(CommandRequest request, CancellationToken cancellationToken, Action<CommandResponse> callback)
        {
            try
            {
                var response = await _dispatcher.DispatchAsync(request, cancellationToken);
                callback(response);
            }
            catch (OperationCanceledException)
            {
                _logger($"[UniCli] Command '{request.command}' cancelled (client disconnected)");
                callback(new CommandResponse
                {
                    success = false,
                    message = "Command cancelled: client disconnected",
                    data = ""
                });
            }
            catch (Exception ex)
            {
                _errorLogger($"[UniCli] Command processing error: {ex.Message}");
                callback(new CommandResponse
                {
                    success = false,
                    message = $"Internal error: {ex.Message}",
                    data = ""
                });
            }
            finally
            {
                CurrentCommandName = null;
                CurrentCommandStartTime = null;
            }
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }
}
