#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly IReadOnlyList<ICommandTransportFactory> _transportFactories;
        private CommandDispatcher _dispatcher;
        private readonly ConcurrentQueue<(CommandRequest request, CancellationToken cancellationToken, Action<CommandResponse> callback)> _commandQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Action<string> _logger;
        private readonly Action<string> _errorLogger;
        private readonly Task[] _serverLoops;
        private Task? _currentCommand;
        private CancellationTokenSource? _currentCommandCts;

        // The command that has been accepted but not yet picked up by the editor update pump.
        // Without this, a request arriving in that window is refused by a command nobody can
        // name — the queue is non-empty while CurrentCommandName is still null.
        private string? _queuedCommandName;

        // Set once per command after cancellation is requested but the command keeps running,
        // so a command that ignores its token is reported instead of just appearing to hang.
        private bool _reportedUncooperativeCancel;

        // One live transport per factory, replaced when a transport reconnects. Guarded because
        // Stop() reads them from the main thread while the loops write them from the pool.
        private readonly object _transportsLock = new();
        private readonly ICommandTransport?[] _currentTransports;

        public string? CurrentCommandName { get; private set; }
        public DateTime? CurrentCommandStartTime { get; private set; }
        public string[] QueuedCommandNames => _commandQueue.ToArray().Select(item => item.request.command).ToArray();

        public UniCliServer(
            IReadOnlyList<ICommandTransportFactory> transportFactories,
            CommandDispatcher dispatcher,
            Action<string> logger,
            Action<string> errorLogger)
        {
            if (transportFactories == null)
                throw new ArgumentNullException(nameof(transportFactories));
            if (transportFactories.Count == 0)
                throw new ArgumentException("At least one transport is required.", nameof(transportFactories));

            _transportFactories = transportFactories;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorLogger = errorLogger ?? throw new ArgumentNullException(nameof(errorLogger));

            _commandQueue = new ConcurrentQueue<(CommandRequest, CancellationToken, Action<CommandResponse>)>();
            _cts = new CancellationTokenSource();
            _currentTransports = new ICommandTransport?[transportFactories.Count];

            // One reconnect loop per transport. They all feed the same OnCommandReceived, so they
            // share the single command slot rather than racing each other.
            _serverLoops = new Task[transportFactories.Count];
            for (var i = 0; i < transportFactories.Count; i++)
            {
                var index = i;
                _serverLoops[i] = Task.Run(
                    async () => await RunTransportLoopAsync(_transportFactories[index], index, _cts.Token),
                    _cts.Token);
            }
        }

        /// <summary>Convenience for the common single-transport case (the named pipe).</summary>
        public UniCliServer(
            string pipeName,
            CommandDispatcher dispatcher,
            Action<string> logger,
            Action<string> errorLogger)
            : this(new ICommandTransportFactory[] { new PipeTransportFactory(pipeName) },
                   dispatcher, logger, errorLogger)
        {
        }

        private void Stop()
        {
            _cts.Cancel();
            _currentCommandCts?.Cancel();

            // Directly dispose the transports to ensure all ThreadPool tasks are stopped before
            // domain reload proceeds. This is critical because the indirect disposal via the
            // finally block in RunTransportLoopAsync may not complete in time.
            ICommandTransport?[] transports;
            lock (_transportsLock)
            {
                transports = (ICommandTransport?[])_currentTransports.Clone();
                for (var i = 0; i < _currentTransports.Length; i++)
                    _currentTransports[i] = null;
            }
            foreach (var transport in transports)
                transport?.Dispose();

            try
            {
                var tasks = new List<Task>(_serverLoops);
                if (_currentCommand is { IsCompleted: false })
                    tasks.Add(_currentCommand);
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromMilliseconds(500));
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

        private async Task RunTransportLoopAsync(
            ICommandTransportFactory factory, int index, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var transport = factory.Create(OnCommandReceived);

                    lock (_transportsLock)
                        _currentTransports[index] = transport;

                    try
                    {
                        await transport.WaitForShutdownAsync(cancellationToken);
                    }
                    finally
                    {
                        lock (_transportsLock)
                        {
                            if (ReferenceEquals(_currentTransports[index], transport))
                                _currentTransports[index] = null;
                        }
                        transport.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _errorLogger($"[UniCli] Transport '{factory.Name}' error: {ex.Message}");
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
