using System;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// What a command needs from the environment the editor is running in, as opposed to from the
    /// editor's state.
    ///
    /// These exist because the alternative is not a confusing error message — it is a lie or a
    /// crash. Measured on 2022.3.62f3, same project, same command:
    ///
    /// <list type="bullet">
    /// <item><c>Screenshot.Capture</c> under <c>-batchmode -nographics</c> takes the whole editor
    /// down with a native crash in <c>MonoGUIView::IsHDRActive()</c>. A CI job loses its editor to
    /// one command.</item>
    /// <item><c>Screenshot.Capture</c> under plain <c>-batchmode</c> returns success and a fully
    /// transparent frame.</item>
    /// <item><c>Scene.Screenshot3D</c> under <c>-nographics</c> returns success and an unrendered
    /// buffer — every pixel 0xCD, alpha included.</item>
    /// </list>
    ///
    /// The same measurements are why this is two flags and not one. <c>Scene.Screenshot3D</c> works
    /// perfectly under plain <c>-batchmode</c>; gating everything screenshot-shaped on batch mode
    /// would have disabled a capability that demonstrably works.
    /// </summary>
    [Flags]
    public enum EnvironmentRequirement
    {
        None = 0,

        /// <summary>
        /// A working graphics device. Absent under <c>-nographics</c>, where rendering commands
        /// return uninitialized buffers or crash.
        /// </summary>
        Graphics = 1 << 0,

        /// <summary>
        /// Editor windows that actually render. Absent under <c>-batchmode</c>, which has the
        /// window objects but nothing behind them, so a capture comes back empty.
        /// </summary>
        InteractiveWindows = 1 << 1
    }
}
