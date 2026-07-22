using System;
using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class ManifestTests
    {
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

        [Test]
        public void LoadMissingFile_ReturnsEmptyManifest()
        {
            var manifest = UniLfsManifest.Load(Path.Combine(_dir, "nope.json"));
            Assert.AreEqual(1, manifest.version);
            Assert.AreEqual(0, manifest.files.Count);
        }

        [Test]
        public void SaveAndLoad_RoundTrips()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/Big/model.fbx", new string('a', 64), 123456789L);
            manifest.Upsert("Assets/大きい/テクスチャ.png", new string('b', 64), 42L);
            string path = Path.Combine(_dir, "unilfs.manifest.json");
            manifest.Save(path);

            var loaded = UniLfsManifest.Load(path);
            Assert.AreEqual(2, loaded.files.Count);
            var entry = loaded.Find("Assets/大きい/テクスチャ.png");
            Assert.NotNull(entry);
            Assert.AreEqual(42L, entry.size);
            Assert.AreEqual(new string('b', 64), entry.hash);
        }

        [Test]
        public void ToJsonString_IsSortedOneLinePerFile()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/z.png", new string('1', 64), 2);
            manifest.Upsert("Assets/a.png", new string('2', 64), 1);
            string json = manifest.ToJsonString();

            int indexA = json.IndexOf("Assets/a.png", StringComparison.Ordinal);
            int indexZ = json.IndexOf("Assets/z.png", StringComparison.Ordinal);
            Assert.Greater(indexA, 0);
            Assert.Greater(indexZ, indexA, "entries must be sorted by path");

            foreach (var line in json.Split('\n'))
            {
                if (line.Contains("\"path\""))
                {
                    StringAssert.Contains("\"hash\"", line);
                    StringAssert.Contains("\"size\"", line);
                }
            }
        }

        [Test]
        public void UpsertExisting_UpdatesInsteadOfDuplicating()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/a.png", new string('1', 64), 1);
            manifest.Upsert("Assets/a.png", new string('2', 64), 2);
            Assert.AreEqual(1, manifest.files.Count);
            Assert.AreEqual(new string('2', 64), manifest.files[0].hash);
            Assert.AreEqual(2, manifest.files[0].size);
        }

        [Test]
        public void Remove_DeletesEntry()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/a.png", new string('1', 64), 1);
            Assert.IsTrue(manifest.Remove("Assets/a.png"));
            Assert.IsFalse(manifest.Remove("Assets/a.png"));
            Assert.AreEqual(0, manifest.files.Count);
        }

        [Test]
        public void Guid_RoundTripsThroughSaveAndLoad()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/Big/clip.mp4", new string('a', 64), 1L).guid = "06ab76e82690a410dbcf6a86d888a12e";
            string path = Path.Combine(_dir, "m.json");
            manifest.Save(path);

            var loaded = UniLfsManifest.Load(path);
            Assert.AreEqual("06ab76e82690a410dbcf6a86d888a12e", loaded.Find("Assets/Big/clip.mp4").guid);
        }

        [Test]
        public void Guid_IsOmittedWhenNotRecorded()
        {
            // Manifests written before GUIDs were recorded have to round-trip
            // byte for byte, or upgrading the package shows up as a diff on
            // every entry.
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/a.png", new string('1', 64), 1);
            StringAssert.DoesNotContain("guid", manifest.ToJsonString());
        }

        [Test]
        public void Upsert_KeepsARecordedGuidWhenTheHashChanges()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/a.png", new string('1', 64), 1).guid = "06ab76e82690a410dbcf6a86d888a12e";
            manifest.Upsert("Assets/a.png", new string('2', 64), 2);
            Assert.AreEqual("06ab76e82690a410dbcf6a86d888a12e", manifest.files[0].guid);
            Assert.AreEqual(new string('2', 64), manifest.files[0].hash);
        }

        [Test]
        public void Save_IsParseableAfterEscaping()
        {
            var manifest = new UniLfsManifest();
            manifest.Upsert("Assets/quote\"backslash\\end.png", new string('c', 64), 7);
            string path = Path.Combine(_dir, "m.json");
            manifest.Save(path);
            var loaded = UniLfsManifest.Load(path);
            Assert.NotNull(loaded.Find("Assets/quote\"backslash\\end.png"));
        }
    }
}
