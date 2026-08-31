using System;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Subscribes the event stream to the editor transitions a client would otherwise poll for.
    ///
    /// Registered from <c>[InitializeOnLoad]</c> rather than from the server, so the record is
    /// complete even across the window where the server is being rebuilt — a domain reload is
    /// precisely when a client is most in the dark, and it is also when the server is not there to
    /// notice anything.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorEventSources
    {
        static EditorEventSources()
        {
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            Application.logMessageReceivedThreaded -= OnLogMessage;
            Application.logMessageReceivedThreaded += OnLogMessage;

            // Reaching the static constructor after a reload *is* the "reloaded" event: the domain
            // that would have raised afterAssemblyReload no longer exists to do it.
            if (SessionState.GetBool(ReloadPendingKey, false))
            {
                SessionState.SetBool(ReloadPendingKey, false);
                EditorEventStream.Publish("domain.reloaded", "Assemblies reloaded; the editor is running new code.");
            }
        }

        private const string ReloadPendingKey = "UniCli.EditorEventSources.ReloadPending";

        private static void OnCompilationStarted(object _)
            => EditorEventStream.Publish("compile.started", "Script compilation started.");

        private static void OnCompilationFinished(object _)
            => EditorEventStream.Publish("compile.finished", "Script compilation finished.");

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var errors = 0;
            var firstError = "";
            foreach (var message in messages ?? Array.Empty<CompilerMessage>())
            {
                if (message.type != CompilerMessageType.Error) continue;
                errors++;
                if (errors == 1) firstError = message.message;
            }

            if (errors == 0)
                return;

            // Only failures: a client wants to know an assembly did not build, not that the other
            // forty did. The compile.finished event still marks the end of the whole run.
            var assembly = System.IO.Path.GetFileNameWithoutExtension(assemblyPath);
            EditorEventStream.Publish(
                "compile.failed",
                $"{assembly} failed to compile with {errors} error(s).",
                EditorEventStream.Json(("assembly", assembly), ("errors", errors.ToString()), ("first", firstError)));
        }

        private static void OnBeforeAssemblyReload()
        {
            SessionState.SetBool(ReloadPendingKey, true);
            EditorEventStream.Publish("domain.reloading",
                "Assemblies are about to reload; in-memory state is being discarded.");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
            => EditorEventStream.Publish("playmode.changed", $"Play mode: {change}.",
                EditorEventStream.Json(("change", change.ToString())));

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // Errors only. Ordinary logs would evict the state transitions this stream exists for,
            // and Console.GetLog already serves the full record.
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;

            EditorEventStream.Publish("log.error", condition ?? "",
                EditorEventStream.Json(("type", type.ToString())));
        }
    }
}
