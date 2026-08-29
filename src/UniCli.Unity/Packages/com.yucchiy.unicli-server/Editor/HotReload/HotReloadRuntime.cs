using System;
using System.Linq;
using System.Reflection;

namespace UniCli.Server.Editor.Handlers
{
    /// <summary>
    /// The two runtime tricks that make a body swap possible, both reached by reflection because
    /// neither is public API.
    ///
    /// <b>Re-emitting</b> — a method compiled into a throwaway assembly cannot legally touch the
    /// private state of the loaded type it is standing in for. MonoMod's DynamicMethodDefinition
    /// re-emits it as a dynamic method, which the runtime lets skip visibility checks. This is what
    /// makes editing a private method's body work at all.
    ///
    /// <b>Detouring</b> — Harmony 2.4 moved detours onto MonoMod.Core, but kept an
    /// (original, replacement) entry point at HarmonyLib.PatchTools.DetourMethod. Harmony's public
    /// Patch API wants a prefix whose signature is known at compile time, which a command handed an
    /// arbitrary file cannot supply.
    ///
    /// Both are resolved once and reported as one clear failure if a Harmony upgrade moves them,
    /// rather than throwing at each call site.
    /// </summary>
    internal static class HotReloadRuntime
    {
        /// <summary>Marks the throwaway assemblies this feature loads, so type lookup can skip them.</summary>
        internal const string AssemblyPrefix = "UniCliHotReload_";

        private static readonly Type s_DynamicMethodDefinition = Resolve("MonoMod.Utils.DynamicMethodDefinition");
        private static readonly MethodInfo s_Generate =
            s_DynamicMethodDefinition?.GetMethod("Generate", Type.EmptyTypes);
        private static readonly MethodInfo s_DetourMethod = ResolveDetour();

        private static Type Resolve(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType(fullName, false); } catch { return null; } })
                .FirstOrDefault(t => t != null);

        private static MethodInfo ResolveDetour()
            => Resolve("HarmonyLib.PatchTools")?.GetMethod(
                "DetourMethod",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(MethodBase), typeof(MethodBase) },
                null);

        /// <summary>
        /// Whether this editor can hot reload, and if not, what is missing. Checked before doing
        /// any work so the caller gets a setup problem reported as one, rather than as a
        /// compilation that succeeds and then swaps nothing.
        /// </summary>
        internal static bool IsAvailable(out string reason)
        {
            if (s_DynamicMethodDefinition == null || s_Generate == null)
            {
                reason = "Hot reload needs MonoMod's DynamicMethodDefinition, which ships inside the " +
                         "Harmony 'Fat' build. Copy net472/0Harmony.dll from a Harmony release into the " +
                         "project — the NuGet lib/ assemblies are not self-contained.";
                return false;
            }

            if (s_DetourMethod == null)
            {
                reason = "Hot reload needs HarmonyLib.PatchTools.DetourMethod, which this Harmony build " +
                         "does not expose. Verified against Harmony 2.4.2.";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>Points a loaded method at a recompiled body.</summary>
        internal static void Swap(MethodBase loaded, MethodInfo compiled)
        {
            var definition = Activator.CreateInstance(s_DynamicMethodDefinition, new object[] { compiled });
            try
            {
                var reEmitted = (MethodInfo)s_Generate.Invoke(definition, null);
                s_DetourMethod.Invoke(null, new object[] { loaded, reEmitted });
            }
            finally
            {
                (definition as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// Mono inlines small methods, and a detour has no effect at a call site that was already
        /// inlined — the swap reports success and the old body keeps running. Worth saying when a
        /// swap happened in an editor that was not started with inlining disabled, because the
        /// symptom otherwise looks like the command silently doing nothing.
        /// </summary>
        internal static string InliningWarning(int swappedCount)
        {
            if (swappedCount == 0)
                return null;

            var limit = Environment.GetEnvironmentVariable("MONO_INLINELIMIT");
            if (limit == "0")
                return null;

            return "Note: this editor was started without MONO_INLINELIMIT=0. Mono may have inlined " +
                   "small methods into their callers, and those call sites keep running the old body " +
                   "even though the swap succeeded. Restart the editor with MONO_INLINELIMIT=0, or mark " +
                   "the method [MethodImpl(MethodImplOptions.NoInlining)].";
        }
    }
}
