using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class GitIgnoreTests
    {
        string _dir;
        string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, ".gitignore");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        [Test]
        public void Update_CreatesFileWithManagedBlock()
        {
            UniLfsGitIgnore.Update(_path, new[] { "Assets/Big/a.fbx" });
            string text = File.ReadAllText(_path);
            StringAssert.Contains(UniLfsGitIgnore.BeginMarker, text);
            StringAssert.Contains(UniLfsGitIgnore.EndMarker, text);
            StringAssert.Contains("/Assets/Big/a.fbx", text);
            StringAssert.Contains(UniLfsPaths.UserSettingsGitIgnoreLine, text);
        }

        [Test]
        public void Update_PreservesUserContentOutsideBlock()
        {
            File.WriteAllText(_path, "Library/\nTemp/\n# my comment\n");
            UniLfsGitIgnore.Update(_path, new[] { "Assets/a.png" });
            UniLfsGitIgnore.Update(_path, new[] { "Assets/a.png", "Assets/b.png" });
            string text = File.ReadAllText(_path);
            StringAssert.Contains("Library/", text);
            StringAssert.Contains("# my comment", text);
            Assert.AreEqual(1, CountOccurrences(text, UniLfsGitIgnore.BeginMarker), "block must not be duplicated");
            StringAssert.Contains("/Assets/b.png", text);
        }

        [Test]
        public void Update_RemovingAllFilesLeavesEmptyBlock()
        {
            UniLfsGitIgnore.Update(_path, new[] { "Assets/a.png" });
            UniLfsGitIgnore.Update(_path, new string[0]);
            string text = File.ReadAllText(_path);
            StringAssert.DoesNotContain("/Assets/a.png", text);
            StringAssert.Contains(UniLfsPaths.UserSettingsGitIgnoreLine, text);
        }

        [Test]
        public void Update_PreservesCrlfFiles()
        {
            File.WriteAllText(_path, "Library/\r\nTemp/\r\n", new UTF8Encoding(false));
            UniLfsGitIgnore.Update(_path, new[] { "Assets/a.png" });
            string text = File.ReadAllText(_path);
            StringAssert.Contains("Library/\r\n", text);
            StringAssert.Contains(UniLfsGitIgnore.BeginMarker + "\r\n", text);
        }

        [Test]
        public void EscapeGitIgnorePath_EscapesPatternCharacters()
        {
            Assert.AreEqual("/Assets/a.png", UniLfsGitIgnore.EscapeGitIgnorePath("Assets/a.png"));
            Assert.AreEqual("/Assets/sprite\\[0\\].png", UniLfsGitIgnore.EscapeGitIgnorePath("Assets/sprite[0].png"));
            Assert.AreEqual("/Assets/star\\*.png", UniLfsGitIgnore.EscapeGitIgnorePath("Assets/star*.png"));
            Assert.AreEqual("/Assets/q\\?.png", UniLfsGitIgnore.EscapeGitIgnorePath("Assets/q?.png"));
        }

        [Test]
        public void ReadManagedLines_ReturnsBlockContents()
        {
            UniLfsGitIgnore.Update(_path, new[] { "Assets/b.png", "Assets/a.png" });
            var lines = UniLfsGitIgnore.ReadManagedLines(_path);
            CollectionAssert.Contains(lines, "/Assets/a.png");
            CollectionAssert.Contains(lines, "/Assets/b.png");
            CollectionAssert.Contains(lines, UniLfsPaths.UserSettingsGitIgnoreLine);
            int a = lines.IndexOf("/Assets/a.png");
            int b = lines.IndexOf("/Assets/b.png");
            Assert.Less(a, b, "tracked paths must be sorted");
        }

        static int CountOccurrences(string text, string needle)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
