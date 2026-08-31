using System.Linq;
using NUnit.Framework;
using UniCli.Server.Editor;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// The event stream replaces polling Editor.Status in a loop, which is lossy: a compile that
    /// started and finished between two polls is indistinguishable from nothing happening. These
    /// cover the properties that make catching-up trustworthy — ordering, the cursor contract, and
    /// telling a caller when it fell too far behind instead of handing it a silent gap.
    /// </summary>
    [TestFixture]
    public class EditorEventStreamTests
    {
        [SetUp]
        public void Reset() => EditorEventStream.ResetForTesting();

        [TearDown]
        public void Clean() => EditorEventStream.ResetForTesting();

        [Test]
        public void Publish_AssignsIncreasingSequenceNumbers()
        {
            EditorEventStream.Publish("compile.started", "one");
            EditorEventStream.Publish("compile.finished", "two");

            var events = EditorEventStream.Since(0, null, 0, out var cursor, out _);

            Assert.That(events.Select(e => e.message), Is.EqualTo(new[] { "one", "two" }));
            Assert.That(events[0].seq, Is.LessThan(events[1].seq));
            Assert.That(cursor, Is.EqualTo(events[1].seq));
        }

        [Test]
        public void Since_ReturnsOnlyWhatIsNew()
        {
            EditorEventStream.Publish("a.one", "first");
            var afterFirst = EditorEventStream.CurrentSequence;
            EditorEventStream.Publish("a.two", "second");

            var events = EditorEventStream.Since(afterFirst, null, 0, out _, out _);

            Assert.That(events.Length, Is.EqualTo(1));
            Assert.That(events[0].message, Is.EqualTo("second"));
        }

        [Test]
        public void Cursor_RoundTripsToNothingNew()
        {
            EditorEventStream.Publish("a.one", "x");
            EditorEventStream.Since(0, null, 0, out var cursor, out _);

            Assert.That(EditorEventStream.Since(cursor, null, 0, out _, out _), Is.Empty,
                "polling again with the returned cursor must not repeat what was already seen");
        }

        [Test]
        public void Limit_StopsAtTheLastEventReturned()
        {
            for (var i = 0; i < 5; i++)
                EditorEventStream.Publish("a.kind", "e" + i);

            var first = EditorEventStream.Since(0, null, 2, out var cursor, out _);
            var second = EditorEventStream.Since(cursor, null, 2, out _, out _);

            Assert.That(first.Select(e => e.message), Is.EqualTo(new[] { "e0", "e1" }));
            Assert.That(second.Select(e => e.message), Is.EqualTo(new[] { "e2", "e3" }),
                "a limited read must resume exactly where it stopped, with nothing skipped");
        }

        [Test]
        public void OverflowingTheBuffer_ReportsWhatWasDropped()
        {
            // A caller that fell behind is told so; a silent gap would let it conclude the editor
            // had been idle.
            for (var i = 0; i < EditorEventStream.Capacity + 10; i++)
                EditorEventStream.Publish("a.kind", "e" + i);

            EditorEventStream.Since(1, null, 0, out _, out var dropped);

            Assert.That(dropped, Is.GreaterThan(0));
        }

        [Test]
        public void NothingDropped_WhenTheCallerKeptUp()
        {
            EditorEventStream.Publish("a.one", "x");
            EditorEventStream.Since(0, null, 0, out var cursor, out _);
            EditorEventStream.Publish("a.two", "y");

            EditorEventStream.Since(cursor, null, 0, out _, out var dropped);

            Assert.That(dropped, Is.Zero);
        }

        [Test]
        public void BufferIsBounded()
        {
            for (var i = 0; i < EditorEventStream.Capacity * 2; i++)
                EditorEventStream.Publish("a.kind", "e" + i);

            Assert.That(EditorEventStream.Since(0, null, 0, out _, out _).Length,
                Is.LessThanOrEqualTo(EditorEventStream.Capacity));
        }

        [TestCase("compile", "compile.started", true)]
        [TestCase("compile", "compile.finished", true)]
        [TestCase("compile", "domain.reloading", false)]
        [TestCase("compile.started", "compile.started", true)]
        [TestCase("compile.started", "compile.finished", false)]
        [TestCase("domain", "domain.reloaded", true)]
        public void KindFilter_MatchesOnDottedPrefix(string filter, string kind, bool expected)
        {
            Assert.That(EditorEventStream.MatchesKind(kind, new[] { filter }), Is.EqualTo(expected));
        }

        [Test]
        public void NoFilter_MatchesEverything()
        {
            Assert.That(EditorEventStream.MatchesKind("anything.at.all", null), Is.True);
            Assert.That(EditorEventStream.MatchesKind("anything.at.all", new string[0]), Is.True);
        }

        [Test]
        public void Filtering_DoesNotDisturbTheCursor()
        {
            EditorEventStream.Publish("compile.started", "c");
            EditorEventStream.Publish("log.error", "boom");

            var events = EditorEventStream.Since(0, new[] { "compile" }, 0, out var cursor, out _);

            Assert.That(events.Length, Is.EqualTo(1));
            Assert.That(cursor, Is.EqualTo(EditorEventStream.CurrentSequence),
                "a filtered read still advances past the events it chose not to return, " +
                "otherwise the next call replays them");
        }

        [Test]
        public void Published_RaisesForSubscribers()
        {
            EditorEvent seen = null;
            void Handler(EditorEvent e) => seen = e;

            EditorEventStream.Published += Handler;
            try
            {
                EditorEventStream.Publish("playmode.changed", "entered");
            }
            finally
            {
                EditorEventStream.Published -= Handler;
            }

            Assert.That(seen, Is.Not.Null, "the push transport depends on this");
            Assert.That(seen.kind, Is.EqualTo("playmode.changed"));
        }

        [Test]
        public void ASubscriberThatThrows_DoesNotBreakPublishing()
        {
            void Bad(EditorEvent _) => throw new System.InvalidOperationException("boom");

            EditorEventStream.Published += Bad;
            try
            {
                Assert.DoesNotThrow(() => EditorEventStream.Publish("a.kind", "still recorded"));
            }
            finally
            {
                EditorEventStream.Published -= Bad;
            }

            Assert.That(EditorEventStream.Since(0, null, 0, out _, out _).Length, Is.EqualTo(1));
        }

        [Test]
        public void EmptyKind_IsIgnored()
        {
            EditorEventStream.Publish("", "nowhere");
            EditorEventStream.Publish(null, "nowhere either");

            Assert.That(EditorEventStream.Since(0, null, 0, out _, out _), Is.Empty);
        }

        [Test]
        public void EventsCarryATimestamp()
        {
            EditorEventStream.Publish("a.kind", "x");

            Assert.That(EditorEventStream.Since(0, null, 0, out _, out _)[0].timestamp,
                Is.GreaterThan(0), "a client needs to know how stale an event is");
        }
    }
}
