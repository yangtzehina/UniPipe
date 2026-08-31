using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UniCli.Server.Editor
{
    [Serializable]
    public sealed class EditorEvent
    {
        /// <summary>Monotonic, and the cursor a client passes back to continue from.</summary>
        public long seq;

        /// <summary>Dotted kind, e.g. <c>compile.finished</c>. Filterable by prefix.</summary>
        public string kind;

        /// <summary>Unix milliseconds, so a client can tell how stale something is.</summary>
        public long timestamp;

        /// <summary>Human-readable summary. Details, when any, are in <see cref="data"/>.</summary>
        public string message;

        /// <summary>Optional JSON object with the specifics, or empty.</summary>
        public string data;
    }

    [Serializable]
    internal sealed class EditorEventBuffer
    {
        public long nextSeq = 1;
        public List<EditorEvent> events = new();
    }

    /// <summary>
    /// What the editor did, as a sequence a client can catch up on.
    ///
    /// Without this, an agent that starts a compile has to poll <c>Editor.Status</c> until it looks
    /// settled — which is wasteful, and worse, lossy: two states that came and went between polls
    /// are indistinguishable from nothing having happened. Sequenced events answer "what happened
    /// since I last looked", which is the question a caller actually has.
    ///
    /// Deliberately not a log mirror. Ordinary console output would evict the state transitions
    /// this exists to carry, so only errors are recorded here; <c>Console.GetLog</c> remains the
    /// place to read logs.
    ///
    /// The buffer survives domain reloads through SessionState, because "the domain reloaded" is
    /// exactly the event a client most needs to be told about and would otherwise lose along with
    /// everything else in memory.
    /// </summary>
    public static class EditorEventStream
    {
        /// <summary>
        /// Enough to cover a compile-and-reload cycle several times over. Older events are dropped;
        /// a client that fell further behind than this is told so rather than silently shortchanged.
        /// </summary>
        internal const int Capacity = 256;

        private const string SessionKey = "UniCli.EditorEventStream";

        private static readonly object s_Lock = new();
        private static EditorEventBuffer s_Buffer;

        /// <summary>Raised on the thread that published. Used by push transports; may be null.</summary>
        public static event Action<EditorEvent> Published;

        private static EditorEventBuffer Buffer
        {
            get
            {
                if (s_Buffer != null)
                    return s_Buffer;

                var stored = SessionState.GetString(SessionKey, null);
                if (!string.IsNullOrEmpty(stored))
                {
                    try { s_Buffer = JsonUtility.FromJson<EditorEventBuffer>(stored); }
                    catch (Exception) { s_Buffer = null; }
                }

                return s_Buffer ??= new EditorEventBuffer();
            }
        }

        public static long CurrentSequence
        {
            get { lock (s_Lock) return Buffer.nextSeq - 1; }
        }

        public static void Publish(string kind, string message, string data = null)
        {
            if (string.IsNullOrEmpty(kind))
                return;

            EditorEvent published;
            lock (s_Lock)
            {
                var buffer = Buffer;
                published = new EditorEvent
                {
                    seq = buffer.nextSeq++,
                    kind = kind,
                    timestamp = UnixMilliseconds(),
                    message = message ?? "",
                    data = data ?? ""
                };

                buffer.events.Add(published);
                if (buffer.events.Count > Capacity)
                    buffer.events.RemoveRange(0, buffer.events.Count - Capacity);

                Persist(buffer);
            }

            try { Published?.Invoke(published); }
            catch (Exception) { /* a subscriber must not break publishing */ }
        }

        /// <summary>
        /// Events after <paramref name="since"/>, oldest first. <paramref name="kinds"/> filters by
        /// prefix, so "compile" matches compile.started and compile.finished.
        ///
        /// <paramref name="dropped"/> reports events that aged out before the caller got to them —
        /// the caller is told it fell behind rather than quietly handed a gap.
        /// </summary>
        public static EditorEvent[] Since(long since, string[] kinds, int limit, out long cursor, out long dropped)
        {
            lock (s_Lock)
            {
                var buffer = Buffer;
                cursor = buffer.nextSeq - 1;

                var oldest = buffer.events.Count > 0 ? buffer.events[0].seq : buffer.nextSeq;
                dropped = since > 0 && oldest > since + 1 ? oldest - since - 1 : 0;

                var matched = new List<EditorEvent>();
                foreach (var candidate in buffer.events)
                {
                    if (candidate.seq <= since) continue;
                    if (!MatchesKind(candidate.kind, kinds)) continue;

                    matched.Add(candidate);
                    if (limit > 0 && matched.Count >= limit)
                    {
                        // Stop at the last one returned so the caller resumes exactly here.
                        cursor = candidate.seq;
                        break;
                    }
                }

                return matched.ToArray();
            }
        }

        internal static bool MatchesKind(string kind, string[] filters)
        {
            if (filters == null || filters.Length == 0)
                return true;

            foreach (var filter in filters)
            {
                if (string.IsNullOrEmpty(filter))
                    continue;

                if (kind == filter || kind.StartsWith(filter + ".", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void Persist(EditorEventBuffer buffer)
        {
            try { SessionState.SetString(SessionKey, JsonUtility.ToJson(buffer)); }
            catch (Exception) { /* the stream is still usable in memory */ }
        }

        private static long UnixMilliseconds()
            => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        /// <summary>Escapes a value for the small JSON payloads events carry.</summary>
        internal static string Json(params (string key, string value)[] fields)
        {
            var json = new StringBuilder("{");
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(McpJson.Quote(fields[i].key)).Append(':').Append(McpJson.Quote(fields[i].value));
            }

            return json.Append('}').ToString();
        }

        internal static void ResetForTesting()
        {
            lock (s_Lock)
            {
                s_Buffer = new EditorEventBuffer();
                SessionState.EraseString(SessionKey);
            }
        }
    }
}
