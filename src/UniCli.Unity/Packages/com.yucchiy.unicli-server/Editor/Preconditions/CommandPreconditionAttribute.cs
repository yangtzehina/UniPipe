using System;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Declares what has to be true before a command may run, and what it does once it does.
    ///
    /// Preconditions used to be enforced by each handler calling <see cref="EditorStateGuard"/>
    /// on its own first line. That works only for as long as every author remembers, and a
    /// command that forgets fails silently in the worst way — we watched an editor automation
    /// tool discard unsaved scenes because one command skipped the check its siblings made.
    /// Declaring the requirement lets <see cref="CommandDispatcher"/> enforce it for every
    /// command uniformly, including ones written outside this package.
    ///
    /// The traits are also reported through <c>Commands.List</c>, so a client can tell which
    /// commands are destructive before calling one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CommandPreconditionAttribute : Attribute
    {
        /// <summary>Editor state this command requires; checked before the handler runs.</summary>
        public GuardCondition EditorState { get; set; }

        /// <summary>
        /// True when the command can replace the open scenes, discarding unsaved changes.
        /// Reported as a trait so callers can see the risk; the accompanying
        /// <c>dirtyAction</c> handling stays in the handler, which is the only place that
        /// knows which scenes a particular request actually affects.
        /// </summary>
        public bool ReplacesOpenScenes { get; set; }

        /// <summary>
        /// True when the command deletes, overwrites, or otherwise makes changes that are not
        /// trivially undoable. Advisory metadata for clients today.
        /// </summary>
        public bool Destructive { get; set; }
    }
}
