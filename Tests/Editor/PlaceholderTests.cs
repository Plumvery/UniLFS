using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class PlaceholderTests
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

        string Path_(string name)
        {
            return Path.Combine(_dir, name);
        }

        [Test]
        public void Write_ProducesADetectablePlaceholder()
        {
            string path = Path_("clip.mp4");
            Assert.IsTrue(UniLfsPlaceholder.Write(path, new string('a', 64), 12345L));
            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void Write_RecordsWhatTheRealFileShouldBe()
        {
            string path = Path_("clip.mp4");
            UniLfsPlaceholder.Write(path, new string('b', 64), 999L);
            string text = File.ReadAllText(path);
            StringAssert.Contains(new string('b', 64), text);
            StringAssert.Contains("999", text);
        }

        [Test]
        public void Write_CreatesMissingDirectories()
        {
            string path = Path_("nested/deeper/clip.mp4");
            Assert.IsTrue(UniLfsPlaceholder.Write(path, new string('a', 64), 1L));
            Assert.IsTrue(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void Write_RefusesToOverwriteRealContent()
        {
            string path = Path_("clip.mp4");
            File.WriteAllText(path, "real content that must survive");
            Assert.IsFalse(UniLfsPlaceholder.Write(path, new string('a', 64), 1L));
            Assert.AreEqual("real content that must survive", File.ReadAllText(path));
        }

        [Test]
        public void Write_ReplacesAnExistingPlaceholder()
        {
            string path = Path_("clip.mp4");
            UniLfsPlaceholder.Write(path, new string('a', 64), 1L);
            Assert.IsTrue(UniLfsPlaceholder.Write(path, new string('c', 64), 2L));
            StringAssert.Contains(new string('c', 64), File.ReadAllText(path));
        }

        [Test]
        public void IsPlaceholder_FalseForRealContent()
        {
            string path = Path_("clip.mp4");
            File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 });
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void IsPlaceholder_FalseForMissingFile()
        {
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(Path_("nope.mp4")));
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(null));
        }

        [Test]
        public void IsPlaceholder_FalseForEmptyFile()
        {
            string path = Path_("empty.mp4");
            File.WriteAllBytes(path, new byte[0]);
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void IsPlaceholder_FalseForLargeFileStartingWithTheMarker()
        {
            // The size bound is what keeps the probe cheap for real assets, and
            // it also means a big file can never be mistaken for a stand-in
            // just because of how it starts.
            string path = Path_("big.mp4");
            var sb = new StringBuilder(UniLfsPlaceholder.Marker);
            sb.Append('x', UniLfsPlaceholder.MaxLength + 100);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Assert.Greater(new FileInfo(path).Length, UniLfsPlaceholder.MaxLength);
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void IsPlaceholder_FalseWhenOnlyTheMarkerMatches()
        {
            // Calling something a placeholder is calling it absent, and Pull
            // overwrites what is absent. A real tracked file that merely starts
            // with the marker must not be destroyed by its own first line.
            string path = Path_("decoy.mp4");
            File.WriteAllText(path, UniLfsPlaceholder.Marker + "\nthis is not a placeholder\nat all\n");
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void IsPlaceholder_FalseWhenTheHeaderIsIncomplete()
        {
            string noHash = Path_("nohash.mp4");
            File.WriteAllText(noHash, UniLfsPlaceholder.Marker + "\nsize: 123\nsha256: " + new string('a', 64) + "\n");
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(noHash), "lines must be in order");

            string badSize = Path_("badsize.mp4");
            File.WriteAllText(badSize, UniLfsPlaceholder.Marker + "\nsha256: " + new string('a', 64) + "\nsize: enormous\n");
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(badSize), "the size has to parse");

            string truncated = Path_("truncated.mp4");
            File.WriteAllText(truncated, UniLfsPlaceholder.Marker + "\nsha256: " + new string('a', 64) + "\n");
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(truncated), "a missing size line is not a placeholder");
        }

        [Test]
        public void IsPlaceholder_TrueForWhatWriteActuallyProduces()
        {
            // Guards the pair: tightening the check must not orphan the writer.
            string path = Path_("roundtrip.mp4");
            UniLfsPlaceholder.Write(path, new string('a', 64), 20431463L);
            Assert.IsTrue(UniLfsPlaceholder.IsPlaceholder(path));
        }

        [Test]
        public void Clear_RemovesPlaceholderButKeepsRealContent()
        {
            string placeholder = Path_("stand-in.mp4");
            string real = Path_("real.mp4");
            UniLfsPlaceholder.Write(placeholder, new string('a', 64), 1L);
            File.WriteAllText(real, "real content");

            Assert.IsTrue(UniLfsPlaceholder.Clear(placeholder));
            Assert.IsFalse(File.Exists(placeholder));

            Assert.IsFalse(UniLfsPlaceholder.Clear(real));
            Assert.IsTrue(File.Exists(real));
        }
    }
}
