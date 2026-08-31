#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using UniCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UniCli.Server.Editor
{
    /// <summary>
    /// Advertises this editor in the per-user registry, so a caller who does not already know the
    /// project path can still find it.
    ///
    /// Published from the bootstrap's static constructor rather than from the server, and withdrawn
    /// only when the editor quits — deliberately the same lifetime as the PID file. The server is
    /// torn down and rebuilt on every domain reload while the editor stays up, so tying the record
    /// to the server would make an editor vanish from the registry every time it recompiles, which
    /// is exactly when a client most needs to be told it exists but is momentarily unreachable.
    ///
    /// A crashed editor leaves its record behind. That is unavoidable — there is no shutdown hook
    /// for a process that dies — so the record is written as a hint and readers establish liveness
    /// themselves.
    /// </summary>
    internal static class InstanceRegistration
    {
        public static void Publish()
        {
            try
            {
                var record = BuildRecord();
                var path = InstanceRegistry.GetRecordPath(record.pipeName);
                var json = JsonUtility.ToJson(record);

                // The record is identical across domain reloads by construction — startedAt is the
                // process start, not the time of writing — so this rewrites nothing on a recompile.
                if (File.Exists(path) && File.ReadAllText(path) == json)
                    return;

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory!);

                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                // Discovery is a convenience; an editor that cannot advertise itself is still
                // perfectly usable by anyone who knows its project path.
                UniCliEditorLog.LogWarning($"[UniCli] Failed to publish instance record: {ex.Message}");
            }
        }

        public static void Withdraw()
        {
            try
            {
                var path = InstanceRegistry.GetRecordPath(ProjectIdentifier.GetPipeName());
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort: a leftover record is pruned by readers that find the process gone.
            }
        }

        internal static InstanceRecord BuildRecord()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            return new InstanceRecord
            {
                projectPath = projectRoot,
                projectName = Path.GetFileName(projectRoot.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                unityVersion = Application.unityVersion,
                pipeName = ProjectIdentifier.GetPipeName(),
                pid = Process.GetCurrentProcess().Id,
                startedAt = ProcessStartedAtUnixMilliseconds(),
                serverVersion = ResolveServerVersion()
            };
        }

        /// <summary>
        /// When the editor process started, not when this was written. Stable across domain
        /// reloads, which keeps the record byte-identical on recompiles, and it is the honest
        /// answer to "how long has this editor been up".
        /// </summary>
        private static long ProcessStartedAtUnixMilliseconds()
        {
            try
            {
                var startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();
                return (long)(startedAt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .TotalMilliseconds;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string ResolveServerVersion()
        {
            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(InstanceRegistration).Assembly);
                return packageInfo?.version ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
