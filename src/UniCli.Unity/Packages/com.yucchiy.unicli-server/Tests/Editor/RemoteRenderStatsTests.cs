using NUnit.Framework;
using UniCli.Remote.Commands;
using UnityEngine;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// The player-side rendering counters.
    ///
    /// This command exists because the editor cannot answer the question where it matters: batch
    /// mode runs no per-frame display render, so every render counter there reads zero. A running
    /// player renders every frame, which makes it the only place a rendering regression can be
    /// measured automatically.
    ///
    /// The live verification is against a real player — a scene alternating between 20 distinct
    /// materials and one shared instanced material reported 23 batches with no instancing and then
    /// 4 batches from 1 instanced batch covering 20 draw calls, at a constant 1924 triangles. What
    /// is worth guarding here is the contract that made those numbers readable: a response that
    /// says whether it is a measurement at all.
    /// </summary>
    [TestFixture]
    public class RemoteRenderStatsTests
    {
        private static RenderStatsCommand.Response Execute()
        {
            var json = new RenderStatsCommand().Execute("");

            Assert.That(json, Is.Not.Null.And.Not.Empty, "the command must answer with JSON");

            return JsonUtility.FromJson<RenderStatsCommand.Response>(json);
        }

        [Test]
        public void ItAnswersWithACompleteResponse()
        {
            var response = Execute();

            Assert.That(response, Is.Not.Null);
            Assert.That(response.available, Is.True,
                "the editor has a profiler, so the counters exist here");
        }

        [Test]
        public void UnavailableCountersAreNamedRatherThanZeroed()
        {
            // A counter this platform does not emit and a counter that measured zero are the same
            // number. Naming the first kind is what keeps the second kind meaningful.
            var response = Execute();

            Assert.That(response.unavailableCounters, Is.Not.Null);
            CollectionAssert.AllItemsAreNotNull(response.unavailableCounters);
        }

        [Test]
        public void ItReportsTheResolutionItMeasuredAt()
        {
            // Batch counts depend on what is on screen, so a comparison across two resolutions is
            // not a comparison.
            var response = Execute();

            Assert.That(response.resolutionWidth, Is.GreaterThan(0));
            Assert.That(response.resolutionHeight, Is.GreaterThan(0));
        }

        [Test]
        public void ItIsRegisteredUnderAStableName()
        {
            Assert.That(new RenderStatsCommand().CommandName, Is.EqualTo("Debug.RenderStats"));
        }

        [Test]
        public void CountersThatNeverSampledAreNotPresentedAsMeasurements()
        {
            var response = Execute();

            if (!response.sampled)
                Assert.That(response.batches, Is.Zero,
                    "an unsampled response must not carry numbers that look measured");
        }
    }
}
