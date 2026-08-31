using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniCli.Server.Editor.Handlers
{
    /// <summary>
    /// What the editor did since the caller last looked.
    ///
    /// Replaces polling <c>Editor.Status</c> in a loop, which is both wasteful and lossy: a compile
    /// that started and finished between two polls looks identical to nothing having happened.
    /// Here the caller passes back the cursor from its previous call and gets the events in
    /// between, or is told it fell behind.
    ///
    /// This returns immediately rather than waiting for something to happen. The server runs one
    /// command at a time, so a long poll would hold that slot and starve every other caller — a
    /// client that wants to be pushed to should use the event stream on the HTTP transport instead.
    /// </summary>
    [CommandPrecondition(Cancellable = true)]
    public sealed class EventsPollHandler : CommandHandler<EventsPollRequest, EventsPollResponse>
    {
        public override string CommandName => "Events.Poll";

        public override string Description =>
            "Editor events since a cursor: compilation, domain reloads, play mode, errors";

        protected override bool TryWriteFormatted(EventsPollResponse response, bool success, IFormatWriter writer)
        {
            if (!success)
                return false;

            if (response.dropped > 0)
                writer.WriteLine($"({response.dropped} older event(s) aged out before this call)");

            foreach (var editorEvent in response.events)
                writer.WriteLine($"{editorEvent.seq,6}  {editorEvent.kind,-18} {editorEvent.message}");

            writer.WriteLine(response.events.Length == 0
                ? $"No new events. cursor={response.cursor}"
                : $"cursor={response.cursor}");

            return true;
        }

        protected override ValueTask<EventsPollResponse> ExecuteAsync(
            EventsPollRequest request, CancellationToken cancellationToken)
        {
            var events = EditorEventStream.Since(
                request.since, request.kinds, request.limit, out var cursor, out var dropped);

            return new ValueTask<EventsPollResponse>(new EventsPollResponse
            {
                events = events,
                cursor = cursor,
                dropped = dropped
            });
        }
    }

    [Serializable]
    public class EventsPollRequest
    {
        /// <summary>Cursor from the previous call; 0 (the default) starts from what is buffered.</summary>
        public long since;

        /// <summary>
        /// Kind prefixes to include, e.g. ["compile", "domain"]. Empty means everything.
        /// Kinds: compile.started, compile.failed, compile.finished, domain.reloading,
        /// domain.reloaded, playmode.changed, log.error.
        /// </summary>
        public string[] kinds;

        /// <summary>Maximum events to return; 0 for no limit.</summary>
        public int limit;
    }

    [Serializable]
    public class EventsPollResponse
    {
        public EditorEvent[] events;

        /// <summary>Pass as <c>since</c> next time.</summary>
        public long cursor;

        /// <summary>Events that aged out of the buffer before this call reached them.</summary>
        public long dropped;
    }
}
