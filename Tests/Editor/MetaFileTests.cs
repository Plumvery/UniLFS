using System;
using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class MetaFileTests
    {
        const string RealGuid = "06ab76e82690a410dbcf6a86d888a12e";

        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        string Path_(string name)
        {
            return Path.Combine(_dir, name);
        }

        [Test]
        public void PathFor_AppendsMetaExtension()
        {
            Assert.AreEqual("Assets/clip.mp4.meta", UniLfsMetaFile.PathFor("Assets/clip.mp4"));
            Assert.IsNull(UniLfsMetaFile.PathFor(null));
        }

        [Test]
        public void IsValidGuid_AcceptsOnly32HexDigits()
        {
            Assert.IsTrue(UniLfsMetaFile.IsValidGuid(RealGuid));
            Assert.IsTrue(UniLfsMetaFile.IsValidGuid(RealGuid.ToUpperInvariant()));
            Assert.IsFalse(UniLfsMetaFile.IsValidGuid(null));
            Assert.IsFalse(UniLfsMetaFile.IsValidGuid(""));
            Assert.IsFalse(UniLfsMetaFile.IsValidGuid(RealGuid.Substring(1)));
            Assert.IsFalse(UniLfsMetaFile.IsValidGuid(RealGuid + "0"));
            Assert.IsFalse(UniLfsMetaFile.IsValidGuid(new string('z', 32)));
        }

        [Test]
        public void ReadGuid_ParsesAUnityMetaFile()
        {
            string path = Path_("clip.mp4.meta");
            File.WriteAllText(path,
                "fileFormatVersion: 2\n" +
                "guid: " + RealGuid + "\n" +
                "VideoClipImporter:\n" +
                "  externalObjects: {}\n" +
                "  serializedVersion: 2\n" +
                "  frameRange: 0\n");
            Assert.AreEqual(RealGuid, UniLfsMetaFile.ReadGuid(path));
        }

        [Test]
        public void ReadGuid_ReturnsNullWhenThereIsNothingToRead()
        {
            Assert.IsNull(UniLfsMetaFile.ReadGuid(Path_("absent.meta")));
            Assert.IsNull(UniLfsMetaFile.ReadGuid(null));

            string noGuid = Path_("noguid.meta");
            File.WriteAllText(noGuid, "fileFormatVersion: 2\nfolderAsset: yes\n");
            Assert.IsNull(UniLfsMetaFile.ReadGuid(noGuid));
        }

        [Test]
        public void ReadGuid_RejectsAMalformedGuid()
        {
            string path = Path_("bad.meta");
            File.WriteAllText(path, "fileFormatVersion: 2\nguid: not-a-guid\n");
            Assert.IsNull(UniLfsMetaFile.ReadGuid(path));
        }

        [Test]
        public void WriteMinimal_RoundTripsThroughReadGuid()
        {
            string path = Path_("clip.mp4.meta");
            Assert.IsTrue(UniLfsMetaFile.WriteMinimal(path, RealGuid));
            Assert.AreEqual(RealGuid, UniLfsMetaFile.ReadGuid(path));
        }

        [Test]
        public void WriteMinimal_NeverTouchesAnExistingMeta()
        {
            // Import settings only live in the .meta on disk, so an existing one
            // always wins - even when its GUID disagrees with the manifest.
            string path = Path_("clip.mp4.meta");
            string original = "fileFormatVersion: 2\nguid: " + new string('f', 32) + "\nVideoClipImporter:\n  quality: 0.7\n";
            File.WriteAllText(path, original);

            Assert.IsFalse(UniLfsMetaFile.WriteMinimal(path, RealGuid));
            Assert.AreEqual(original, File.ReadAllText(path));
        }

        [Test]
        public void WriteMinimal_RefusesAnUnusableGuid()
        {
            string path = Path_("clip.mp4.meta");
            Assert.IsFalse(UniLfsMetaFile.WriteMinimal(path, null));
            Assert.IsFalse(UniLfsMetaFile.WriteMinimal(path, ""));
            Assert.IsFalse(UniLfsMetaFile.WriteMinimal(path, "nope"));
            Assert.IsFalse(File.Exists(path));
        }

        [Test]
        public void WriteMinimal_CreatesMissingDirectories()
        {
            string path = Path_("nested/deeper/clip.mp4.meta");
            Assert.IsTrue(UniLfsMetaFile.WriteMinimal(path, RealGuid));
            Assert.AreEqual(RealGuid, UniLfsMetaFile.ReadGuid(path));
        }
    }
}
