using System.Collections.Generic;
using NUnit.Framework;
using UniCli.Server.Editor;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// A command's undo footprint used to be whatever its mutations happened to register —
    /// GameObject.Create registers the new object, then parents it and adds components with raw
    /// calls. Grouping makes "one command, one Ctrl+Z" a property of the dispatcher instead of a
    /// habit each handler has to keep.
    /// </summary>
    [TestFixture]
    public class UndoGroupTests
    {
        private sealed class RecordingUndo : IUndoOperations
        {
            public readonly List<string> Calls = new();
            public int Group;

            public void IncrementCurrentGroup()
            {
                Group++;
                Calls.Add("increment");
            }

            public int GetCurrentGroup()
            {
                Calls.Add("get");
                return Group;
            }

            public void SetCurrentGroupName(string name) => Calls.Add($"name:{name}");
            public void CollapseUndoOperations(int group) => Calls.Add($"collapse:{group}");
        }

        [Test]
        public void Begin_IncrementsBeforeCapturingTheGroup()
        {
            var undo = new RecordingUndo { Group = 7 };

            UndoGroup.Begin("GameObject.Create", undo);

            // Incrementing first is what keeps the command's operations from being folded in
            // with whatever the user did in the editor immediately beforehand.
            Assert.That(undo.Calls[0], Is.EqualTo("increment"));
            Assert.That(undo.Calls[1], Is.EqualTo("get"));
        }

        [Test]
        public void Begin_NamesTheEntryAfterTheCommand()
        {
            var undo = new RecordingUndo();

            UndoGroup.Begin("GameObject.Create", undo);

            Assert.That(undo.Calls, Does.Contain("name:GameObject.Create"),
                "the undo history should read like the commands that were issued");
        }

        [Test]
        public void Collapse_CollapsesTheGroupBegunFor()
        {
            var undo = new RecordingUndo { Group = 41 };

            var group = UndoGroup.Begin("GameObject.Destroy", undo);
            group.Collapse();

            Assert.That(undo.Calls[^1], Is.EqualTo("collapse:42"));
        }

        [Test]
        public void None_CollapsesNothing()
        {
            // Commands that declare no grouping must leave the undo stack untouched.
            Assert.DoesNotThrow(() => UndoGroup.None.Collapse());
        }

        [Test]
        public void Begin_NullOperations_YieldsAnInertGroup()
        {
            var group = UndoGroup.Begin("Some.Command", null);

            Assert.DoesNotThrow(() => group.Collapse());
        }

        [Test]
        public void Collapse_IsSafeToCallTwice()
        {
            // The dispatcher collapses in a finally block; a future refactor calling it again
            // must not corrupt the undo stack.
            var undo = new RecordingUndo();
            var group = UndoGroup.Begin("Scene.Open", undo);

            group.Collapse();
            group.Collapse();

            Assert.That(undo.Calls.FindAll(c => c.StartsWith("collapse:")).Count, Is.EqualTo(2));
            Assert.That(undo.Calls[^1], Is.EqualTo(undo.Calls[^2]),
                "both collapses target the same group, so the second is a no-op for Unity");
        }

        [Test]
        public void SeparateCommands_GetSeparateGroups()
        {
            var undo = new RecordingUndo();

            var first = UndoGroup.Begin("GameObject.Create", undo);
            first.Collapse();
            var second = UndoGroup.Begin("GameObject.Destroy", undo);
            second.Collapse();

            var collapses = undo.Calls.FindAll(c => c.StartsWith("collapse:"));
            Assert.That(collapses[0], Is.Not.EqualTo(collapses[1]),
                "two commands must be two undo steps, not one merged step");
        }
    }
}
