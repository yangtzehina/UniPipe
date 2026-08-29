using System;
using System.Threading;
using System.Threading.Tasks;
using UniCli.Protocol;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Hands a decoded command to the core and takes back the response. This is the whole seam
    /// between "how a request arrived" and "what the server does with it" — a transport speaks
    /// bytes on one side and this signature on the other, and knows nothing about how the command
    /// is dispatched, queued, or gated.
    /// </summary>
    public delegate void CommandReceivedHandler(
        CommandRequest request, CancellationToken cancellationToken, Action<CommandResponse> respond);

    /// <summary>
    /// A way for a client to reach the editor. The named pipe is one; HTTP loopback is another.
    /// Every transport feeds the same <see cref="CommandReceivedHandler"/>, so they share the
    /// single command slot, the precondition checks and the undo grouping — the routing layer is
    /// what makes several front ends one server rather than several servers.
    /// </summary>
    public interface ICommandTransport : IDisposable
    {
        /// <summary>Completes when the transport has stopped, or the token is cancelled.</summary>
        Task WaitForShutdownAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Creates a transport bound to a given command handler. A factory rather than a ready-made
    /// transport because the server rebuilds a transport each time it needs to reconnect (a pipe
    /// client disconnects, a listener is recycled across a domain reload), and each rebuild must
    /// be wired to the same handler.
    /// </summary>
    public interface ICommandTransportFactory
    {
        /// <summary>Short stable name for logs and diagnostics, e.g. "pipe" or "http".</summary>
        string Name { get; }

        ICommandTransport Create(CommandReceivedHandler onCommandReceived);
    }
}
