using NUnit.Framework;
using UniCli.Server.Editor.Handlers.Remote;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// Bringing up the editor's side of the player connection.
    ///
    /// Nothing initializes it on its own — it happens as a side effect of the profiler's attach
    /// control, which does not exist in batch mode. Measured on 2022.3.62f3: the same player build
    /// answered eight remote commands under an interactive editor and none under
    /// <c>-batchmode</c>, with the scripting backend making no difference. That is the headless-CI
    /// topology exactly, so the initialisation cannot be left to a window that will never open.
    /// </summary>
    [TestFixture]
    public class EditorPlayerConnectionTests
    {
        [Test]
        public void InitialisingIsIdempotent()
        {
            // Called from three entry points — connecting, resolving a player, registering the
            // message handler — because any of them can be the first thing a caller does.
            Assert.DoesNotThrow(() =>
            {
                EditorPlayerConnection.EnsureInitialized();
                EditorPlayerConnection.EnsureInitialized();
            });
        }

        [Test]
        public void ResolvingAPlayerInitialisesFirst()
        {
            // With no player connected this throws its own explanatory error rather than a
            // NullReference from an uninitialised connection.
            var error = Assert.Throws<System.InvalidOperationException>(
                () => RemoteHelper.ResolvePlayerId(0));

            Assert.That(error.Message, Does.Contain("Development Build"));
        }
    }
}
