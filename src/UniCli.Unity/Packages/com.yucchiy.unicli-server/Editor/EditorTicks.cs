using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Lets a number of editor updates pass.
    ///
    /// Some things the editor does — repainting a view, and so producing the render statistics that
    /// describe it — happen on the editor's own schedule rather than when asked. A command that
    /// reads the result has to let the editor run first, and it cannot do that by blocking: the
    /// server pumps commands from <see cref="EditorApplication.update"/>, so a blocking wait would
    /// stop the very loop it is waiting for.
    /// </summary>
    internal static class EditorTicks
    {
        public static Task WaitAsync(int ticks, CancellationToken cancellationToken)
        {
            if (ticks <= 0)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();
            var remaining = ticks;

            void OnUpdate()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    EditorApplication.update -= OnUpdate;
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                if (--remaining > 0)
                    return;

                EditorApplication.update -= OnUpdate;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += OnUpdate;
            return tcs.Task;
        }
    }
}
