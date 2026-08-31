using System.IO;
using NUnit.Framework;
using UniCli.Protocol;
using UnityEngine;

namespace UniCli.Server.Editor.Tests
{
    /// <summary>
    /// The record an editor publishes so callers who do not know its path can still find it.
    ///
    /// Everything downstream reads <c>projectPath</c> as the project root. Unity hands out
    /// <c>Application.dataPath</c>, which points at Assets, so the one thing that must not
    /// regress here is the conversion between the two.
    /// </summary>
    [TestFixture]
    public class InstanceRegistrationTests
    {
        [Test]
        public void ProjectPath_IsTheRoot_NotTheAssetsFolder()
        {
            var record = InstanceRegistration.BuildRecord();

            Assert.That(record.projectPath, Does.Not.EndWith("Assets"),
                "callers resolve manifests and Library paths from this; pointing it at Assets " +
                "would send every one of them a level too deep");
            Assert.That(Directory.Exists(Path.Combine(record.projectPath, "Assets")), Is.True);
        }

        [Test]
        public void ProjectName_IsTheFolderName()
        {
            var record = InstanceRegistration.BuildRecord();

            Assert.That(record.projectName, Is.EqualTo(Path.GetFileName(record.projectPath)),
                "this is the handle a human types to name the editor");
        }

        [Test]
        public void PipeName_MatchesWhatTheServerListensOn()
        {
            // The record's file name is this value, and the client dials it. A divergence would
            // advertise an editor at an address nothing is listening on.
            Assert.That(InstanceRegistration.BuildRecord().pipeName,
                Is.EqualTo(ProjectIdentifier.GetPipeName()));
        }

        [Test]
        public void PidIsThisEditor()
        {
            Assert.That(InstanceRegistration.BuildRecord().pid,
                Is.EqualTo(System.Diagnostics.Process.GetCurrentProcess().Id));
        }

        [Test]
        public void StartedAt_IsTheProcessStart_NotTheTimeOfWriting()
        {
            // Two records built moments apart must be byte-identical, or every domain reload
            // rewrites the file and "uptime" becomes "time since last recompile".
            var first = JsonUtility.ToJson(InstanceRegistration.BuildRecord());
            var second = JsonUtility.ToJson(InstanceRegistration.BuildRecord());

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void RecordPath_LivesUnderTheRegistryDirectory()
        {
            var path = InstanceRegistry.GetRecordPath("unicli-abcdef12");

            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(InstanceRegistry.GetDirectory()));
            Assert.That(Path.GetFileName(path), Is.EqualTo("unicli-abcdef12.json"));
        }

        [Test]
        public void TheRecordSurvivesAJsonRoundTrip()
        {
            var record = InstanceRegistration.BuildRecord();
            var restored = JsonUtility.FromJson<InstanceRecord>(JsonUtility.ToJson(record));

            Assert.That(restored.pipeName, Is.EqualTo(record.pipeName));
            Assert.That(restored.projectPath, Is.EqualTo(record.projectPath));
            Assert.That(restored.pid, Is.EqualTo(record.pid));
            Assert.That(restored.startedAt, Is.EqualTo(record.startedAt));
        }
    }
}
