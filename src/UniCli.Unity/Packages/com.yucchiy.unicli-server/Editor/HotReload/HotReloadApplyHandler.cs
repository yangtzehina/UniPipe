using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace UniCli.Server.Editor.Handlers
{
    /// <summary>
    /// Applies an edited C# file to the running editor without a domain reload.
    ///
    /// Unity's own answer to editing code is to recompile the project and reload the domain, which
    /// throws away Play Mode and every object in it. That is the loop this command replaces: edit a
    /// method body, run this, and the next call runs the new body against the objects already
    /// alive.
    ///
    /// How: the file is compiled on its own, producing a second set of types with the same names.
    /// Each recompiled method is matched to the loaded one it replaces, re-emitted as a dynamic
    /// method (which is what lets a body reach private state it no longer has the right to touch),
    /// and the loaded method is detoured onto it.
    ///
    /// What it will not do, and says so instead of guessing: add or remove fields, change a
    /// signature, introduce a type. Each of those needs a real recompile, and each is reported per
    /// type or method rather than failing the whole call — a partial application is normal and
    /// worth being explicit about.
    ///
    /// Requires Harmony (0Harmony.dll) and the UNIPIPE_HOTRELOAD define; see unipipe/docs.
    /// </summary>
    [CommandPrecondition(EditorState = GuardCondition.NotCompiling, Cancellable = true, Destructive = true)]
    public sealed class HotReloadApplyHandler : CommandHandler<HotReloadApplyRequest, HotReloadApplyResponse>
    {
        public override string CommandName => "HotReload.Apply";

        public override string Description =>
            "Apply an edited C# file to the running editor by swapping method bodies, without a domain reload";

        protected override bool TryWriteFormatted(HotReloadApplyResponse response, bool success, IFormatWriter writer)
        {
            if (!success)
                return false;

            writer.WriteLine($"Swapped {response.swapped.Length} method(s) in {response.elapsedMs}ms.");
            foreach (var method in response.swapped)
                writer.WriteLine("  " + method);

            if (response.skipped.Length > 0)
            {
                writer.WriteLine($"Skipped {response.skipped.Length}:");
                foreach (var skip in response.skipped)
                    writer.WriteLine($"  {skip.what} — {skip.detail}");
            }

            if (!string.IsNullOrEmpty(response.warning))
                writer.WriteLine(response.warning);

            return true;
        }

        protected override async ValueTask<HotReloadApplyResponse> ExecuteAsync(
            HotReloadApplyRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(request.path))
                throw new ArgumentException("'path' is required");

            var sourcePath = ResolvePath(request.path);
            if (!File.Exists(sourcePath))
                throw new ArgumentException($"Script not found: {request.path}");

            if (!HotReloadRuntime.IsAvailable(out var unavailable))
                throw new CommandFailedException(unavailable, null);

            var startedAt = DateTime.UtcNow;

            var assembly = await CompileInIsolationAsync(sourcePath, cancellationToken);

            var candidates = new List<SwapCandidate>();
            var skips = new List<SwapSkip>();
            MethodSwapper.Plan(assembly.GetTypes(), FindLoadedType, candidates, skips);

            var swapped = new List<string>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    HotReloadRuntime.Swap(candidate.Loaded, candidate.Compiled);
                    swapped.Add(candidate.Description);
                }
                catch (Exception ex)
                {
                    skips.Add(new SwapSkip(candidate.Description, SkipReason.Unsupported,
                        $"{ex.GetType().Name}: {ex.Message}"));
                }
            }

            return new HotReloadApplyResponse
            {
                swapped = swapped.ToArray(),
                skipped = skips.Select(s => new HotReloadSkip
                {
                    what = s.What,
                    reason = s.Reason.ToString(),
                    detail = s.Detail
                }).ToArray(),
                elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                warning = HotReloadRuntime.InliningWarning(swapped.Count)
            };
        }

        /// <summary>
        /// The type this recompiled one replaces. Searched across loaded assemblies by full name,
        /// skipping the throwaway assemblies previous hot reloads produced so a second apply
        /// matches the original rather than the last patch.
        /// </summary>
        private static Type FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;

                var name = assembly.GetName().Name;
                if (name.StartsWith(HotReloadRuntime.AssemblyPrefix, StringComparison.Ordinal)) continue;

                Type type;
                try { type = assembly.GetType(fullName, throwOnError: false); }
                catch { continue; }

                if (type != null)
                    return type;
            }

            return null;
        }

        private static async ValueTask<Assembly> CompileInIsolationAsync(
            string sourcePath, CancellationToken cancellationToken)
        {
            var directory = Path.Combine("Temp", "UniCliHotReload");
            Directory.CreateDirectory(directory);

            // The assembly name has to be unique per apply: loading two assemblies with the same
            // name leaves the first one winning, so the second edit would silently do nothing.
            var stem = HotReloadRuntime.AssemblyPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            var assemblyPath = Path.Combine(directory, stem + ".dll");

            var completion = new TaskCompletionSource<CompilerMessage[]>();
            var builder = new AssemblyBuilder(assemblyPath, sourcePath)
            {
                referencesOptions = ReferencesOptions.UseEngineModules,
                additionalReferences = EvalHandler.GetAdditionalReferences()
            };
            builder.buildFinished += (_, messages) => completion.TrySetResult(messages);

            if (!builder.Build())
                throw new CommandFailedException("Failed to start compilation", null);

            var result = await completion.Task.WithCancellation(cancellationToken);

            var errors = (result ?? Array.Empty<CompilerMessage>())
                .Where(m => m.type == CompilerMessageType.Error)
                .Select(m => ScriptValidateHandler.StripSourceLocation(m.message))
                .ToArray();

            if (errors.Length > 0)
                throw new CommandFailedException(
                    $"The file does not compile ({errors.Length} error(s)): {string.Join("; ", errors)}", null);

            return Assembly.Load(File.ReadAllBytes(assemblyPath));
        }
    }

    [Serializable]
    public class HotReloadApplyRequest
    {
        /// <summary>Path to the edited .cs file.</summary>
        public string path;
    }

    [Serializable]
    public class HotReloadApplyResponse
    {
        public string[] swapped;
        public HotReloadSkip[] skipped;
        public long elapsedMs;
        public string warning;
    }

    [Serializable]
    public class HotReloadSkip
    {
        public string what;
        public string reason;
        public string detail;
    }
}
