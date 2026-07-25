using System;
using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// The staging file: what this machine has asked to track and not pushed
    /// yet. It is the only per-machine state nothing can recompute, so the
    /// rules that matter are about what it refuses to hold and what it does
    /// when it cannot be read.
    /// </summary>
    public class StagedPathsTests
    {
        const string One = "Assets/Art/one.bin";
        const string Two = "Assets/Art/two.bin";

        string _dir;
        string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, UniLfsPaths.StagedFileName);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        [Test]
        public void Missing_ReadsAsEmpty()
        {
            var staged = UniLfsStagedPaths.Load(_path);
            CollectionAssert.IsEmpty(staged.paths);
            CollectionAssert.IsEmpty(staged.resolveLocal);
            Assert.IsFalse(staged.Contains(One));
        }

        [Test]
        public void Add_IsIdempotentAndSurvivesReload()
        {
            var staged = UniLfsStagedPaths.Load(_path);
            Assert.IsTrue(staged.Add(One));
            Assert.IsFalse(staged.Add(One), "staging the same path twice is not a second decision");
            staged.Add(Two);
            staged.Save(_path);

            var reloaded = UniLfsStagedPaths.Load(_path);
            CollectionAssert.AreEqual(new[] { One, Two }, reloaded.paths, "sorted, so the file does not churn");
        }

        [Test]
        public void KeepLocal_IsRecordedSeparatelyFromStaging()
        {
            var staged = UniLfsStagedPaths.Load(_path);
            Assert.IsTrue(staged.KeepLocal(One));
            Assert.IsFalse(staged.KeepLocal(One));
            staged.Save(_path);

            var reloaded = UniLfsStagedPaths.Load(_path);
            Assert.IsTrue(reloaded.ResolvesLocal(One));
            Assert.IsFalse(reloaded.Contains(One),
                "resolving a conflict says which version wins, not that the path is waiting for its first Push");
        }

        [Test]
        public void Remove_DropsEveryTraceOfThePath()
        {
            var staged = UniLfsStagedPaths.Load(_path);
            staged.Add(One);
            staged.KeepLocal(One);
            Assert.IsTrue(staged.Remove(One));
            Assert.IsFalse(staged.Contains(One));
            Assert.IsFalse(staged.ResolvesLocal(One), "a promoted path has nothing left to decide");
        }

        /// <summary>
        /// The file exists only while it has something to say — otherwise every
        /// project that ever pushed would carry an empty one next to the
        /// manifest forever.
        /// </summary>
        [Test]
        public void Save_DeletesTheFileOnceNothingIsStaged()
        {
            var staged = UniLfsStagedPaths.Load(_path);
            staged.Add(One);
            staged.Save(_path);
            Assert.IsTrue(File.Exists(_path));

            staged.Remove(One);
            staged.Save(_path);
            Assert.IsFalse(File.Exists(_path));
        }

        /// <summary>
        /// Unreadable staging costs the intent to track those paths, and
        /// re-running Track restores it. Throwing instead would stop Push
        /// working at all, which is worse for the files already in the manifest.
        /// </summary>
        [Test]
        public void CorruptFile_IsTreatedAsEmpty()
        {
            File.WriteAllText(_path, "{ this is not json");
            CollectionAssert.IsEmpty(UniLfsStagedPaths.Load(_path).paths);
        }

        [Test]
        public void RoundTrip_KeepsBothListsApart()
        {
            var staged = UniLfsStagedPaths.Load(_path);
            staged.Add(One);
            staged.KeepLocal(Two);
            staged.Save(_path);

            var reloaded = UniLfsStagedPaths.Load(_path);
            CollectionAssert.AreEqual(new[] { One }, reloaded.paths);
            CollectionAssert.AreEqual(new[] { Two }, reloaded.resolveLocal);
            StringAssert.DoesNotContain("hash", reloaded.ToJsonString(),
                "staging holds intent; anything derived would be a second copy of the truth able to go stale");
        }
    }
}
