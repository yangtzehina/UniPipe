using System.IO;
using NUnit.Framework;
using UniCli.Server.Editor;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// AssemblyDefinition.AddReference and friends read a file, change it in memory and write it
    /// back. Anything that touched the file in between was silently discarded, and the result gave
    /// no sign of it. A caller that passes the fingerprint its decision was based on gets told.
    /// </summary>
    [TestFixture]
    public class StaleWriteGuardTests
    {
        [Test]
        public void NoExpectedHash_OptsOut()
        {
            // Callers that did not read first should not be forced to.
            Assert.That(StaleWriteGuard.Check(null, "abc123", "file"), Is.Null);
            Assert.That(StaleWriteGuard.Check("", "abc123", "file"), Is.Null);
        }

        [Test]
        public void MatchingHash_Proceeds()
        {
            Assert.That(StaleWriteGuard.Check("abc123", "abc123", "file"), Is.Null);
        }

        [Test]
        public void HashComparisonIgnoresCase()
        {
            Assert.That(StaleWriteGuard.Check("ABC123", "abc123", "file"), Is.Null);
        }

        [Test]
        public void ChangedContent_RefusesAndExplainsBothSides()
        {
            var reason = StaleWriteGuard.Check("aaaaaa", "bbbbbb", "Assembly definition 'FairyGUI'");

            Assert.That(reason, Is.Not.Null);
            Assert.That(reason, Does.Contain("FairyGUI"), "the caller needs to know which file");
            Assert.That(reason, Does.Contain("aaaaaa").And.Contain("bbbbbb"),
                "showing both fingerprints makes the refusal checkable");
            Assert.That(reason, Does.Contain("expectedSha"),
                "a refusal should say how to proceed deliberately");
        }

        [Test]
        public void VanishedFile_SaysSoRatherThanShowingANullHash()
        {
            var reason = StaleWriteGuard.Check("aaaaaa", null, "Assembly definition 'Gone'");

            Assert.That(reason, Does.Contain("no longer exists"));
            Assert.That(reason, Does.Not.Contain("found "),
                "there is no current fingerprint to report");
        }

        [Test]
        public void FileHash_IsStableForTheSameContent()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                File.WriteAllText(path, "{ \"name\": \"Example\" }");
                var first = ContentHash.OfFile(path);
                var second = ContentHash.OfFile(path);

                Assert.That(first, Is.EqualTo(second));
                Assert.That(first, Is.Not.Null.And.Length.EqualTo(12));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void FileHash_ChangesWithContent()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                File.WriteAllText(path, "before");
                var before = ContentHash.OfFile(path);
                File.WriteAllText(path, "after");
                var after = ContentHash.OfFile(path);

                Assert.That(after, Is.Not.EqualTo(before));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void MissingFile_HashesToNull_WhichIsNotAnError()
        {
            // Also the state before a file is first created, so it must not read as failure.
            Assert.That(ContentHash.OfFile(Path.Combine(Path.GetTempPath(), "unicli-no-such-file")), Is.Null);
            Assert.That(ContentHash.OfFile(null), Is.Null);
        }

        [Test]
        public void TextHash_MatchesFileHashForTheSameBytes()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                const string content = "reference: FairyGUI";
                File.WriteAllText(path, content);

                // Lets a caller fingerprint content it holds in memory without writing it first.
                Assert.That(ContentHash.OfText(content), Is.EqualTo(ContentHash.OfFile(path)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void CheckFile_RefusesWhenTheFileOnDiskMoved()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                File.WriteAllText(path, "original");
                var readHash = ContentHash.OfFile(path);
                File.WriteAllText(path, "someone else got here first");

                Assert.That(StaleWriteGuard.CheckFile(readHash, path, "asmdef"), Is.Not.Null);
                Assert.That(StaleWriteGuard.CheckFile(ContentHash.OfFile(path), path, "asmdef"), Is.Null);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Script.Validate compiles a throwaway copy of the source, so the compiler's own message
    /// names a temporary file that no longer exists by the time the caller reads the result.
    /// </summary>
    [TestFixture]
    public class ScriptDiagnosticMessageTests
    {
        [Test]
        public void StripsTheTemporaryPathAndSeverity()
        {
            const string raw = "Temp/UniCliScriptValidate/ead0e3f3.cs(2,61): error CS0103: " +
                               "The name 'undefinedThing' does not exist in the current context";

            var cleaned = Handlers.ScriptValidateHandler.StripSourceLocation(raw);

            Assert.That(cleaned, Is.EqualTo(
                "CS0103: The name 'undefinedThing' does not exist in the current context"));
        }

        [Test]
        public void StripsWarningsToo()
        {
            var cleaned = Handlers.ScriptValidateHandler.StripSourceLocation(
                "Temp/x.cs(9,13): warning CS0168: The variable 'e' is declared but never used");

            Assert.That(cleaned, Does.StartWith("CS0168:"));
        }

        [Test]
        public void LeavesMessagesWithoutALocationAlone()
        {
            const string raw = "Internal compiler error";
            Assert.That(Handlers.ScriptValidateHandler.StripSourceLocation(raw), Is.EqualTo(raw));
        }

        [Test]
        public void HandlesNull()
        {
            Assert.That(Handlers.ScriptValidateHandler.StripSourceLocation(null), Is.Null);
        }
    }
}
