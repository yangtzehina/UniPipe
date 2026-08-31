using System;
using System.IO;

namespace UniCli.Protocol
{
    /// <summary>
    /// One running editor, advertising itself to anything looking for it.
    ///
    /// Addressing an editor has until now meant already knowing its project path: the pipe name is
    /// derived from that path, so a caller who does not know the path cannot ask a question. That
    /// is fine from inside a project directory and unworkable from anywhere else — a CI runner, an
    /// agent working across two projects, a shell that happens to be somewhere else.
    ///
    /// A record is a hint, never the truth. An editor that crashes leaves its file behind, so a
    /// reader has to establish liveness itself rather than trust what it finds here.
    /// </summary>
    [Serializable]
    public class InstanceRecord
    {
        /// <summary>Absolute project root — the directory containing Assets, not Assets itself.</summary>
        public string projectPath;

        /// <summary>The project folder's name: what a human calls this project.</summary>
        public string projectName;

        public string unityVersion;

        /// <summary>Also this record's file name — the identifier both sides already derive.</summary>
        public string pipeName;

        public int pid;

        /// <summary>Unix milliseconds, so a stale record can be reported with its age.</summary>
        public long startedAt;

        /// <summary>Version of the server package, for the client's compatibility check.</summary>
        public string serverVersion;
    }

    /// <summary>
    /// Where running editors advertise themselves. Shared source, because the editor writes these
    /// records and the client reads them, and a disagreement about the path would be silent.
    /// </summary>
    public static class InstanceRegistry
    {
        /// <summary>
        /// Overrides the state directory. Tests set it so they never see the developer's own
        /// editors, and CI sets it to keep parallel jobs on one machine from discovering each
        /// other's editors.
        /// </summary>
        public const string HomeVariable = "UNICLI_HOME";

        public const string RecordExtension = ".json";

        public static string GetHomeDirectory()
        {
            var overridden = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrEmpty(overridden))
                return overridden;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unicli");
        }

        public static string GetDirectory()
        {
            return Path.Combine(GetHomeDirectory(), "instances");
        }

        public static string GetRecordPath(string pipeName)
        {
            return Path.Combine(GetDirectory(), pipeName + RecordExtension);
        }
    }
}
