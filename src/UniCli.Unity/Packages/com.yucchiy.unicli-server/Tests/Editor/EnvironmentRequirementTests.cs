using NUnit.Framework;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// Refusing commands the environment cannot actually run.
    ///
    /// The measurements these encode, taken on 2022.3.62f3 with the same project and command:
    /// Screenshot.Capture under -batchmode -nographics crashed the editor natively; under plain
    /// -batchmode it returned success and a fully transparent frame; Scene.Screenshot3D under
    /// -nographics returned success and a buffer whose every byte was 0xCD. The same run showed
    /// Scene.Screenshot3D working correctly under plain -batchmode, which is why the two
    /// requirements are separate — gating everything screenshot-shaped on batch mode would have
    /// disabled a capability that works.
    /// </summary>
    [TestFixture]
    public class EnvironmentRequirementTests
    {
        private sealed class FakeEnvironment : IEnvironmentProbe
        {
            public bool IsBatchMode { get; set; }
            public bool HasGraphicsDevice { get; set; } = true;
        }

        private sealed class FakeState : IEditorStateProbe
        {
            public bool IsPlaying { get; set; }
            public bool IsCompiling { get; set; }
        }

        private static readonly FakeEnvironment Interactive =
            new() { IsBatchMode = false, HasGraphicsDevice = true };

        private static readonly FakeEnvironment BatchWithGraphics =
            new() { IsBatchMode = true, HasGraphicsDevice = true };

        private static readonly FakeEnvironment BatchNoGraphics =
            new() { IsBatchMode = true, HasGraphicsDevice = false };

        private static CommandPrecondition Requiring(
            EnvironmentRequirement requirement, string alternative = null)
        {
            return new CommandPrecondition(
                default, false, false, false, false, requirement, alternative);
        }

        private static string Check(CommandPrecondition precondition, IEnvironmentProbe environment)
            => CommandPreconditions.Check(precondition, "Some.Command", new FakeState(), environment);

        [Test]
        public void GraphicsCommand_IsRefusedWithoutAGraphicsDevice()
        {
            var reason = Check(Requiring(EnvironmentRequirement.Graphics), BatchNoGraphics);

            Assert.That(reason, Is.Not.Null);
            Assert.That(reason, Does.Contain("-nographics"));
        }

        [Test]
        public void GraphicsCommand_RunsInBatchModeThatHasAGraphicsDevice()
        {
            // Measured: Scene.Screenshot3D renders correctly here. Refusing it would remove a
            // working headless capability.
            Assert.That(Check(Requiring(EnvironmentRequirement.Graphics), BatchWithGraphics), Is.Null);
        }

        [Test]
        public void InteractiveCommand_IsRefusedInBatchMode_EvenWithGraphics()
        {
            var reason = Check(
                Requiring(EnvironmentRequirement.InteractiveWindows), BatchWithGraphics);

            Assert.That(reason, Is.Not.Null);
            Assert.That(reason, Does.Contain("batch mode"));
        }

        [Test]
        public void InteractiveCommand_RunsInARealEditor()
        {
            Assert.That(
                Check(Requiring(EnvironmentRequirement.InteractiveWindows), Interactive), Is.Null);
        }

        [Test]
        public void ACommandNeedingNothing_RunsAnywhere()
        {
            Assert.That(Check(Requiring(EnvironmentRequirement.None), BatchNoGraphics), Is.Null);
        }

        [Test]
        public void TheMissingGraphicsDeviceIsReportedFirst()
        {
            // A command that needs both, in an environment missing both, should be told about the
            // graphics device: that is the one that crashes rather than merely returning nothing.
            var reason = Check(
                Requiring(EnvironmentRequirement.Graphics | EnvironmentRequirement.InteractiveWindows),
                BatchNoGraphics);

            Assert.That(reason, Does.Contain("-nographics"));
        }

        [Test]
        public void NoAlternativeIsOfferedWhenItWouldAlsoBeRefused()
        {
            // The alternative a rendering command names is another rendering command. Without a
            // graphics device that one is refused too, so naming it would send the caller into a
            // second refusal.
            var reason = Check(
                Requiring(EnvironmentRequirement.Graphics | EnvironmentRequirement.InteractiveWindows,
                          "Scene.Screenshot3D"),
                BatchNoGraphics);

            Assert.That(reason, Does.Not.Contain("Scene.Screenshot3D"));
        }

        [Test]
        public void TheRefusalNamesAWorkingAlternative()
        {
            var reason = Check(
                Requiring(EnvironmentRequirement.InteractiveWindows, "Scene.Screenshot3D"),
                BatchWithGraphics);

            Assert.That(reason, Does.Contain("Scene.Screenshot3D"),
                "a refusal that does not say what to do instead is a dead end");
        }

        [Test]
        public void EnvironmentIsCheckedBeforeEditorState()
        {
            // Play Mode is something a caller can fix by waiting; a missing graphics device is
            // not going to appear. Reporting the recoverable one first would send a CI job into a
            // retry loop against a wall.
            var precondition = new CommandPrecondition(
                GuardCondition.NotPlaying, false, false, false, false,
                EnvironmentRequirement.Graphics, null);

            var reason = CommandPreconditions.Check(
                precondition, "Some.Command", new FakeState { IsPlaying = true }, BatchNoGraphics);

            Assert.That(reason, Does.Contain("-nographics"));
            Assert.That(reason, Does.Not.Contain("Play Mode"));
        }

        [Test]
        public void TheRequirementIsReportedInMetadata()
        {
            Assert.That(Requiring(EnvironmentRequirement.Graphics).EnvironmentName,
                Is.EqualTo("Graphics"));
            Assert.That(Requiring(EnvironmentRequirement.None).EnvironmentName, Is.Null,
                "a command that needs nothing must not advertise a requirement");
        }

        [Test]
        public void DeclaringOnlyAnEnvironment_IsNotAnEmptyPrecondition()
        {
            Assert.That(Requiring(EnvironmentRequirement.Graphics).IsEmpty, Is.False);
        }

        [CommandPrecondition(
            Environment = EnvironmentRequirement.Graphics, AlternativeCommand = "Other.Command")]
        private sealed class DeclaredOnAType { }

        [Test]
        public void TheAttributeIsRead()
        {
            var resolved = CommandPreconditions.Resolve(typeof(DeclaredOnAType));

            Assert.That(resolved.Environment, Is.EqualTo(EnvironmentRequirement.Graphics));
            Assert.That(resolved.AlternativeCommand, Is.EqualTo("Other.Command"));
        }
    }
}
