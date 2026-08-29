using UnityEditor;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Unity's undo grouping, behind an interface so the grouping logic can be tested without
    /// an editor. Mirrors <see cref="Undo"/>'s four grouping calls and nothing else.
    /// </summary>
    public interface IUndoOperations
    {
        void IncrementCurrentGroup();
        int GetCurrentGroup();
        void SetCurrentGroupName(string name);
        void CollapseUndoOperations(int group);
    }

    internal sealed class UnityUndoOperations : IUndoOperations
    {
        public static readonly UnityUndoOperations Instance = new();

        public void IncrementCurrentGroup() => Undo.IncrementCurrentGroup();
        public int GetCurrentGroup() => Undo.GetCurrentGroup();
        public void SetCurrentGroupName(string name) => Undo.SetCurrentGroupName(name);
        public void CollapseUndoOperations(int group) => Undo.CollapseUndoOperations(group);
    }

    /// <summary>
    /// Collapses everything a command registers with <see cref="Undo"/> into one entry, named
    /// after the command.
    ///
    /// Without it, a command's undo footprint is whatever its mutations happened to register:
    /// <c>GameObject.Create</c> registers the new object, then parents it and adds components
    /// with raw calls. What lands in the undo history is an implementation detail of the
    /// handler, which is a poor thing to ask someone to reason about when they want the last
    /// command taken back. Grouping at this layer makes "one command, one Ctrl+Z" a property of
    /// the framework rather than a habit of each handler, and names the entry after the command
    /// so the undo history reads like the commands that were issued.
    /// </summary>
    public readonly struct UndoGroup
    {
        private readonly IUndoOperations _operations;
        private readonly int _group;

        private UndoGroup(IUndoOperations operations, int group)
        {
            _operations = operations;
            _group = group;
        }

        /// <summary>A group that collapses nothing, for commands that declare no undo grouping.</summary>
        public static UndoGroup None => default;

        public static UndoGroup Begin(string commandName, IUndoOperations operations)
        {
            if (operations == null)
                return None;

            // Increment first so this command's operations cannot be folded together with
            // whatever the user did in the editor immediately beforehand.
            operations.IncrementCurrentGroup();
            var group = operations.GetCurrentGroup();
            operations.SetCurrentGroupName(commandName);
            return new UndoGroup(operations, group);
        }

        public static UndoGroup Begin(string commandName)
            => Begin(commandName, UnityUndoOperations.Instance);

        /// <summary>
        /// Collapses the command's operations. Called even when the command failed: a command
        /// that threw halfway still made the changes it made, and those should come back in one
        /// step rather than several.
        /// </summary>
        public void Collapse() => _operations?.CollapseUndoOperations(_group);
    }
}
