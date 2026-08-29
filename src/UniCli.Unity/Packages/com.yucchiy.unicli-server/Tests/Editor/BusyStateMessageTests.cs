using System;
using NUnit.Framework;
using UniCli.Server.Editor;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// The server runs one command at a time and refuses the rest rather than queueing them, so
    /// the refusal text is the client's only account of what it collided with. A stress run of
    /// ten concurrent requests produced nine refusals that all blamed a command called "unknown":
    /// the slot was taken by a request that had been accepted but not yet picked up by the editor
    /// update pump, a state the old message could not describe.
    /// </summary>
    [TestFixture]
    public class BusyStateMessageTests
    {
        private static readonly DateTime Started = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void Running_NamesTheCommandAndHowLongItHasRun()
        {
            var message = UniCliServer.DescribeBusyState(
                runningCommand: "BuildPlayer.Build",
                queuedCommand: null,
                startedUtc: Started,
                nowUtc: Started.AddSeconds(12.5));

            Assert.That(message, Does.Contain("BuildPlayer.Build"));
            Assert.That(message, Does.Contain("12.5s"), "elapsed time tells the caller whether waiting is worthwhile");
            Assert.That(message, Does.Not.Contain("unknown"));
        }

        [Test]
        public void Queued_NamesTheQueuedCommand()
        {
            // The window that produced the "unknown" refusals: accepted, not yet started.
            var message = UniCliServer.DescribeBusyState(
                runningCommand: null,
                queuedCommand: "Editor.Status",
                startedUtc: null,
                nowUtc: Started);

            Assert.That(message, Does.Contain("Editor.Status"));
            Assert.That(message, Does.Contain("queued"));
            Assert.That(message, Does.Not.Contain("unknown"));
        }

        [Test]
        public void RunningTakesPrecedenceOverQueued()
        {
            var message = UniCliServer.DescribeBusyState("Compile", "Editor.Status", Started, Started.AddSeconds(1));

            Assert.That(message, Does.Contain("Compile"));
            Assert.That(message, Does.Not.Contain("Editor.Status"));
        }

        [Test]
        public void NeitherKnown_StillReadsAsARefusal_WithoutInventingAName()
        {
            var message = UniCliServer.DescribeBusyState(null, null, null, Started);

            Assert.That(message, Does.Contain("busy"));
            Assert.That(message, Does.Not.Contain("unknown"),
                "an unnamed command is better described than given a fake name");
        }

        [Test]
        public void ElapsedIsOmittedWhenTheClockGivesNothingUseful()
        {
            // Start time is only set when the pump picks the command up; a refusal can race it.
            var message = UniCliServer.DescribeBusyState("Compile", null, Started, Started);

            Assert.That(message, Does.Contain("Compile"));
            Assert.That(message, Does.Not.Contain("running for"));
        }

        [Test]
        public void CancelGraceIsLongEnoughToNotFireOnOrdinaryCommands()
        {
            // The watchdog reports a command that ignores its token. Ordinary commands finish
            // well inside this window, so the report means something when it appears.
            Assert.That(UniCliServer.UncooperativeCancelGrace, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
        }
    }
}
