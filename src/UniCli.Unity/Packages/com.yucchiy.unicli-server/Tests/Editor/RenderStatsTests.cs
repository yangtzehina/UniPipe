using NUnit.Framework;
using UniCli.Server.Editor.Handlers;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// Render statistics, and the one judgement the command makes about them.
    ///
    /// <see cref="UnityStats"/> keeps its values after the render that produced them, so the
    /// numbers are always there whether or not anything was drawn. The resolution is what
    /// distinguishes the two, and getting that wrong means reporting "0 batches" as a measurement
    /// when the truth is that nothing rendered at all.
    /// </summary>
    [TestFixture]
    public class RenderStatsTests
    {
        [TestCase("691x352", true)]
        [TestCase("1920x1080", true)]
        [TestCase("1x1", true)]
        [TestCase("0x0", false)]
        [TestCase("0x1080", false)]
        [TestCase("1920x0", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("x", false)]
        [TestCase("notaresolution", false)]
        public void ARenderIsRecognisedByItsResolution(string resolution, bool expected)
        {
            Assert.That(RenderGetStatsHandler.IsRendered(resolution), Is.EqualTo(expected));
        }

        [Test]
        public void TheCommandDeclaresWhatItNeedsFromTheEnvironment()
        {
            // Measured: under -batchmode every one of these reads zero and the resolution is 0x0,
            // because batch mode runs no per-frame display render. Without the declaration the
            // command would answer with a page of zeros that looks like data.
            var precondition = CommandPreconditions.Resolve(typeof(RenderGetStatsHandler));

            Assert.That(precondition.Environment,
                Is.EqualTo(EnvironmentRequirement.Graphics | EnvironmentRequirement.InteractiveWindows));
        }

        [Test]
        public void ItIsRefusedInBatchMode()
        {
            var precondition = CommandPreconditions.Resolve(typeof(RenderGetStatsHandler));

            var reason = CommandPreconditions.Check(
                precondition, "Render.GetStats", new NeverBusy(), new BatchModeWithGraphics());

            Assert.That(reason, Is.Not.Null);
        }

        private sealed class NeverBusy : IEditorStateProbe
        {
            public bool IsPlaying => false;
            public bool IsCompiling => false;
        }

        private sealed class BatchModeWithGraphics : IEnvironmentProbe
        {
            public bool IsBatchMode => true;
            public bool HasGraphicsDevice => true;
        }
    }
}
