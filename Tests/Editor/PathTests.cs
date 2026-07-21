using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class PathTests
    {
        [Test]
        public void Normalize_ConvertsBackslashes()
        {
            Assert.AreEqual("Assets/Big/a.fbx", UniLfsPaths.Normalize("Assets\\Big\\a.fbx"));
            Assert.IsNull(UniLfsPaths.Normalize(null));
        }

        [Test]
        public void ToProjectRelative_HandlesAbsolutePathsInsideProject()
        {
            string abs = UniLfsPaths.ToAbsolute("Assets/Foo/bar.png");
            Assert.AreEqual("Assets/Foo/bar.png", UniLfsPaths.ToProjectRelative(abs));
        }

        [Test]
        public void ToProjectRelative_HandlesRelativeAndBackslashPaths()
        {
            Assert.AreEqual("Assets/Foo/bar.png", UniLfsPaths.ToProjectRelative("Assets\\Foo\\bar.png"));
            Assert.AreEqual("Assets/Foo/bar.png", UniLfsPaths.ToProjectRelative("Assets/Foo/bar.png"));
        }

        /// <summary>
        /// Built from ProjectRoot because "outside the project" has no portable
        /// literal: this used to assert on "C:/...", which is only absolute on
        /// Windows — on Unix the very same string is an ordinary relative path
        /// that resolves *inside* the project, so the assertion described a
        /// contract the method never had and failed everywhere but Windows.
        /// </summary>
        [Test]
        public void ToProjectRelative_ReturnsNullOutsideProject()
        {
            Assert.IsNull(UniLfsPaths.ToProjectRelative(
                UniLfsPaths.Combine(UniLfsPaths.ProjectRoot, "../definitely-not-the-project/file.png")));
        }

        [Test]
        public void IsTrackablePath_RejectsMetaAndInternalDirs()
        {
            string reason;
            Assert.IsTrue(UniLfsPaths.IsTrackablePath("Assets/Big/a.fbx", out reason));
            Assert.IsFalse(UniLfsPaths.IsTrackablePath("Assets/Big/a.fbx.meta", out reason));
            Assert.IsFalse(UniLfsPaths.IsTrackablePath("Library/something.bin", out reason));
            Assert.IsFalse(UniLfsPaths.IsTrackablePath("Temp/x", out reason));
            Assert.IsFalse(UniLfsPaths.IsTrackablePath("UserSettings/UniLFS.json", out reason));
            Assert.IsFalse(UniLfsPaths.IsTrackablePath(UniLfsPaths.ManifestFileName, out reason));
            Assert.IsFalse(UniLfsPaths.IsTrackablePath("", out reason));
        }

        [Test]
        public void IsTrackablePath_AllowsNonAssetsProjectFiles()
        {
            string reason;
            Assert.IsTrue(UniLfsPaths.IsTrackablePath("RawContent/movie.mp4", out reason));
            Assert.IsTrue(UniLfsPaths.IsTrackablePath("Packages/com.example.local/big.bin", out reason));
        }
    }
}
