using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniCli.Protocol;

namespace UniCli.Client;

/// <summary>
/// Turns "which editor" into a project path.
///
/// The order matters more than any single rule. An explicit path wins, because someone who typed a
/// path meant it. A working directory inside a project wins next, because that is the cheapest
/// correct answer and needs no registry at all. Only then does discovery apply, and only when it
/// is unambiguous.
///
/// Ambiguity is refused, never guessed. Picking one of two editors named MyGame would send writes
/// into a project the caller never named — the same class of mistake the dirty-scene gate exists to
/// prevent, and just as silent.
/// </summary>
internal static class ProjectResolver
{
    /// <summary>
    /// What this process resolved, once it has. A CLI invocation talks to exactly one editor, so
    /// later steps that need the path again — caching completions, for one — can ask rather than
    /// resolve a second time and risk a different answer.
    /// </summary>
    public static string? Resolved { get; private set; }

    /// <summary>
    /// Resolves the project to talk to, or explains why it could not.
    /// <paramref name="selector"/> is a path or an editor name; null means "work it out".
    /// </summary>
    public static async Task<Result<string, string>> ResolveAsync(string? selector)
    {
        // An explicit path is taken at face value, including one that no running editor matches:
        // the caller may be about to have Unity launched for it.
        if (!string.IsNullOrEmpty(selector) && Directory.Exists(selector))
            return Remember(selector!);

        // No registry read at all when the working directory answers the question.
        if (string.IsNullOrEmpty(selector))
        {
            var fromCwd = ProjectIdentifier.FindUnityProjectRoot();
            if (fromCwd != null)
                return Remember(fromCwd);
        }

        var entries = await InstanceDirectory.ProbeAllAsync();
        var matched = Match(selector, entries);

        return matched.IsError ? matched : Remember(matched.SuccessValue);
    }

    private static Result<string, string> Remember(string projectPath)
    {
        Resolved = projectPath;
        return Result<string, string>.Success(projectPath);
    }

    /// <summary>
    /// The registry half of the decision, with no filesystem or process access, so its rules can
    /// be tested directly.
    /// </summary>
    internal static Result<string, string> Match(string? selector, IReadOnlyList<InstanceEntry> entries)
    {
        var usable = entries.Where(e => e.IsUsable).ToList();

        if (string.IsNullOrEmpty(selector))
        {
            if (usable.Count == 1)
                return Result<string, string>.Success(usable[0].Record.projectPath);

            if (usable.Count == 0)
                return Result<string, string>.Error(
                    "Unity project not found.\n" +
                    "  Run this command from within a Unity project directory,\n" +
                    "  or set UNICLI_PROJECT environment variable to specify the project path.\n" +
                    "  No running editors were found in the registry either.");

            return Result<string, string>.Error(
                "Several editors are running and none was named.\n" +
                Describe(usable) +
                "\n  Name one with UNICLI_PROJECT, or run from inside the project directory.");
        }

        var matches = usable.Where(e => Matches(e.Record, selector!)).ToList();

        if (matches.Count == 1)
            return Result<string, string>.Success(matches[0].Record.projectPath);

        if (matches.Count > 1)
            return Result<string, string>.Error(
                $"'{selector}' matches more than one running editor.\n" +
                Describe(matches) +
                "\n  Use a longer path fragment, or the full project path.");

        if (usable.Count == 0)
            return Result<string, string>.Error(
                $"'{selector}' is not a directory, and no editors are running to match it by name.");

        return Result<string, string>.Error(
            $"'{selector}' is not a directory and matches no running editor.\n" +
            Describe(usable));
    }

    /// <summary>
    /// Name first, then any trailing run of path segments — so two projects that share a folder
    /// name can be told apart by including the parent, without typing the whole path.
    /// </summary>
    private static bool Matches(InstanceRecord record, string selector)
    {
        if (string.Equals(record.projectName, selector, StringComparison.OrdinalIgnoreCase))
            return true;

        var path = Normalize(record.projectPath);
        var wanted = Normalize(selector);

        if (string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private static string Describe(IEnumerable<InstanceEntry> entries)
    {
        return string.Join("\n", entries.Select(e =>
            $"    {e.Record.projectName,-24} {e.Record.projectPath}" +
            (e.State == InstanceState.Reloading ? "  (reloading)" : "")));
    }
}
