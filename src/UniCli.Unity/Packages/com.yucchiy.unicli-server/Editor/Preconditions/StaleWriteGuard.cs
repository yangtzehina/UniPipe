using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UniCli.Server.Editor
{
    /// <summary>Content fingerprints for read-modify-write commands.</summary>
    public static class ContentHash
    {
        /// <summary>
        /// Fingerprint of a file's bytes, or null when the file does not exist — callers treat a
        /// missing file as "nothing to be stale against" rather than as an error, because that is
        /// also the state before a file is first created.
        /// </summary>
        public static string OfFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                return Of(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                return null;
            }
        }

        public static string OfText(string text)
            => text == null ? null : Of(Encoding.UTF8.GetBytes(text));

        private static string Of(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);

            // Twelve hex characters. Long enough that an accidental collision between two
            // versions of the same file is not a practical concern, short enough to pass around
            // in a command line without becoming noise.
            var builder = new StringBuilder(12);
            for (var i = 0; i < 6; i++)
                builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }
    }

    /// <summary>
    /// Refuses a write based on a stale read.
    ///
    /// Commands like <c>AssemblyDefinition.AddReference</c> read a file, change it in memory and
    /// write it back. If anything touched that file in between — another agent, the person at the
    /// keyboard, Unity's own inspector — the write silently discards their change, and nothing in
    /// the result says so. A caller that passes the fingerprint its decision was based on gets
    /// told instead.
    ///
    /// This cannot move up into the dispatcher the way the editor-state checks did: the expected
    /// fingerprint arrives as a request field, and deserialization happens below that layer. It
    /// lives here so every read-modify-write command applies the same rule and phrases the refusal
    /// the same way.
    /// </summary>
    public static class StaleWriteGuard
    {
        /// <summary>
        /// Returns null when the write may proceed, or the reason it may not. Passing no expected
        /// fingerprint opts out — the check is available to callers who want it, not imposed on
        /// callers who did not read first.
        /// </summary>
        public static string Check(string expectedHash, string actualHash, string what)
        {
            if (string.IsNullOrEmpty(expectedHash))
                return null;

            if (string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                return null;

            if (actualHash == null)
                return $"{what} no longer exists (expected content {expectedHash}). " +
                       "Read it again before writing.";

            return $"{what} changed since it was read (expected {expectedHash}, found {actualHash}). " +
                   "Someone or something else edited it; read it again and reapply the change, " +
                   "or omit expectedSha to overwrite deliberately.";
        }

        /// <summary>Convenience for the common case of guarding a file on disk.</summary>
        public static string CheckFile(string expectedHash, string path, string what)
            => Check(expectedHash, ContentHash.OfFile(path), what);
    }
}
