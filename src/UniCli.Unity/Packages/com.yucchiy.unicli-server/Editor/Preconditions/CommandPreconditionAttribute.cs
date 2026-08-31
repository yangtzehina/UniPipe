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
        /// True when everything the command registers with Unity's Undo system should collapse
        /// into a single, command-named entry — so taking the command back is one Ctrl+Z rather
        /// than as many as the handler happened to register.
        /// </summary>
        public bool SingleUndoStep { get; set; }

        /// <summary>
        /// True when the command observes its CancellationToken and returns promptly once it is
        /// signalled. Declared rather than assumed: cancellation is cooperative, so a client that
        /// disconnects can only expect a prompt release from commands that say they cooperate.
        /// </summary>
        public bool Cancellable { get; set; }

        /// <summary>
        /// True when the command deletes, overwrites, or otherwise makes changes that are not
        /// trivially undoable. Advisory metadata for clients today.
        /// </summary>
        public bool Destructive { get; set; }

        /// <summary>
        /// What the command needs from the environment — a graphics device, real editor windows.
        /// Checked before the handler runs, because the failures this prevents are not recoverable
        /// afterwards: one of them is a native crash that takes the editor with it, and the rest
        /// report success while returning blank frames.
        /// </summary>
        public EnvironmentRequirement Environment { get; set; }

        /// <summary>
        /// A command that does the same job in an environment this one cannot run in, named in the
        /// refusal. A refusal that says what to do instead is the difference between a gate and a
        /// dead end.
        /// </summary>
        public string AlternativeCommand { get; set; }
    }
}
