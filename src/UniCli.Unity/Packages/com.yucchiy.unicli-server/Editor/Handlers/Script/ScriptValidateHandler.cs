using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor.Compilation;
using UnityEngine;

namespace UniCli.Server.Editor.Handlers
{
    /// <summary>
    /// Compiles C# source on its own and reports what the compiler says, without adding it to the
    /// project.
    ///
    /// Writing a broken script is not an ordinary error here. Unity recompiles on its own schedule,
    /// and a project that fails to compile can drop the editor into Safe Mode — where the server is
    /// gone and no command can reach it, including the one that would put the file back. The agent
    /// that broke it has no way left to fix it. Checking the source before it is written keeps that
    /// from happening, and costs one throwaway compile.
    ///
    /// The source is compiled to a temporary assembly that is never loaded, so validating cannot
    /// change the editor's state the way <c>eval</c> does.
    /// </summary>
    [CommandPrecondition(EditorState = GuardCondition.NotCompiling, Cancellable = true)]
    public sealed class ScriptValidateHandler : CommandHandler<ScriptValidateRequest, ScriptValidateResponse>
    {
        public override string CommandName => "Script.Validate";

        public override string Description =>
            "Compile C# source in isolation and report errors and warnings, without adding it to the project";

        protected override bool TryWriteFormatted(ScriptValidateResponse response, bool success, IFormatWriter writer)
        {
            if (!success)
                return false;

            if (response.valid)
            {
                writer.WriteLine(response.warnings.Length == 0
                    ? "Valid."
                    : $"Valid, with {response.warnings.Length} warning(s):");
            }
            else
            {
                writer.WriteLine($"{response.errors.Length} error(s):");
            }

            foreach (var diagnostic in response.errors)
                writer.WriteLine("  " + Describe(diagnostic));
            foreach (var diagnostic in response.warnings)
                writer.WriteLine("  " + Describe(diagnostic));

            return true;
        }

        private static string Describe(ScriptDiagnostic diagnostic)
            => diagnostic.line > 0
                ? $"({diagnostic.line},{diagnostic.column}) {diagnostic.message}"
                : diagnostic.message;

        protected override async ValueTask<ScriptValidateResponse> ExecuteAsync(
            ScriptValidateRequest request, CancellationToken cancellationToken)
        {
            var source = await ResolveSourceAsync(request);

            var workDirectory = Path.Combine("Temp", "UniCliScriptValidate");
            Directory.CreateDirectory(workDirectory);

            var stem = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sourcePath = Path.Combine(workDirectory, stem + ".cs");
            var assemblyPath = Path.Combine(workDirectory, stem + ".dll");

            try
            {
                File.WriteAllText(sourcePath, source);
                var messages = await CompileAsync(sourcePath, assemblyPath, cancellationToken);
                return BuildResponse(messages, sourcePath);
            }
            finally
            {
                TryDelete(sourcePath);
                TryDelete(assemblyPath);
            }
        }

        private async ValueTask<string> ResolveSourceAsync(ScriptValidateRequest request)
        {
            var hasCode = !string.IsNullOrEmpty(request.code);
            var hasPath = !string.IsNullOrEmpty(request.path);

            if (hasCode == hasPath)
                throw new ArgumentException("Provide exactly one of 'code' or 'path'");

            if (hasCode)
                return request.code;

            var resolved = ResolvePath(request.path);
            if (!File.Exists(resolved))
                throw new ArgumentException($"Script not found: {request.path}");

            return await Task.Run(() => File.ReadAllText(resolved));
        }

        private static ValueTask<CompilerMessage[]> CompileAsync(
            string sourcePath, string assemblyPath, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<CompilerMessage[]>();

            var builder = new AssemblyBuilder(assemblyPath, sourcePath)
            {
                referencesOptions = ReferencesOptions.UseEngineModules,
                additionalReferences = EvalHandler.GetAdditionalReferences()
            };

            builder.buildFinished += (_, messages) => completion.TrySetResult(messages);

            if (!builder.Build())
                throw new CommandFailedException("Failed to start compilation", null);

            return completion.Task.WithCancellation(cancellationToken);
        }

        private static ScriptValidateResponse BuildResponse(CompilerMessage[] messages, string sourcePath)
        {
            var errors = new List<ScriptDiagnostic>();
            var warnings = new List<ScriptDiagnostic>();

            foreach (var message in messages ?? Array.Empty<CompilerMessage>())
            {
                var diagnostic = new ScriptDiagnostic
                {
                    message = StripSourceLocation(message.message),
                    line = message.line,
                    column = message.column
                };

                if (message.type == CompilerMessageType.Error)
                    errors.Add(diagnostic);
                else if (message.type == CompilerMessageType.Warning)
                    warnings.Add(diagnostic);
            }

            return new ScriptValidateResponse
            {
                valid = errors.Count == 0,
                errors = errors.ToArray(),
                warnings = warnings.ToArray()
            };
        }

        // The compiler prefixes each message with the file it was handed. That file is this
        // command's temporary copy, deleted before the caller sees the result, so quoting it back
        // points at nothing. Line and column are reported separately.
        private static readonly Regex s_SourceLocationPrefix =
            new(@"^.*?\(\d+,\d+\):\s*(?:error|warning)\s+", RegexOptions.Compiled);

        internal static string StripSourceLocation(string message)
            => message == null ? null : s_SourceLocationPrefix.Replace(message, "", 1).Trim();

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Temp/ is cleared when the editor exits; a file left behind is harmless.
            }
        }
    }

    [Serializable]
    public class ScriptValidateRequest
    {
        /// <summary>C# source to check. Mutually exclusive with <see cref="path"/>.</summary>
        public string code;

        /// <summary>Path to an existing .cs file to check. Mutually exclusive with <see cref="code"/>.</summary>
        public string path;
    }

    [Serializable]
    public class ScriptValidateResponse
    {
        public bool valid;
        public ScriptDiagnostic[] errors;
        public ScriptDiagnostic[] warnings;
    }

    [Serializable]
    public class ScriptDiagnostic
    {
        public string message;
        public int line;
        public int column;
    }
}
