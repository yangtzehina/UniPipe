using UnityEditor.Networking.PlayerConnection;

namespace UniCli.Server.Editor.Handlers.Remote
{
    /// <summary>
    /// Brings up the editor's side of the player connection.
    ///
    /// Nothing in an editor initializes this on its own — it happens as a side effect of the
    /// editor's own UI, the profiler's attach control. In batch mode there is no such UI, so
    /// <see cref="EditorConnection.ConnectedPlayers"/> stays empty however many players are
    /// running, and every remote command answers "no runtime player connected".
    ///
    /// Measured on 2022.3.62f3: the same player build answered eight commands under an interactive
    /// editor and none under <c>-batchmode</c>, with the scripting backend making no difference.
    /// That is the headless-CI topology exactly — a build under test driven by an editor with no
    /// display — so the initialisation cannot be left to a window that will never open.
    /// </summary>
    internal static class EditorPlayerConnection
    {
        private static bool s_Initialized;

        public static void EnsureInitialized()
        {
            if (s_Initialized)
                return;

            // Idempotent on Unity's side, but tracked here so the common path is a bool check.
            EditorConnection.instance.Initialize();
            s_Initialized = true;
        }
    }
}
