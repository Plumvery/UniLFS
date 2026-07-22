using System;
using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// The three-way rules and the baseline they read from. Together these are
    /// what separate "I edited this" from "someone else pushed a newer version"
    /// — two situations with identical hashes and opposite fixes.
    /// </summary>
    public class SyncStateTests
    {
        const string Mine = "1111000000000000000000000000000000000000000000000000000000000000";
        const string Theirs = "2222000000000000000000000000000000000000000000000000000000000000";
        const string Shared = "3333000000000000000000000000000000000000000000000000000000000000";

        [Test]
        public void LocalMatchingTheManifest_IsUpToDate()
        {
            Assert.AreEqual(UniLfsFileState.UpToDate, UniLfsThreeWay.Classify(Shared, Shared, Shared));
            Assert.AreEqual(UniLfsFileState.UpToDate, UniLfsThreeWay.Classify(Shared, Shared, null),
                "agreement does not need a baseline to be obvious");
        }

        [Test]
        public void OnlyLocalMoved_IsModified()
        {
            Assert.AreEqual(UniLfsFileState.Modified, UniLfsThreeWay.Classify(Mine, Shared, Shared));
        }

        /// <summary>
        /// The case this whole mechanism exists for: a teammate pushed a new
        /// version, so the manifest moved while this copy stayed exactly where
        /// it was last synced. Reading it as Modified (which is all the hashes
        /// alone can say) hid it from Pull and offered it to Push, where the
        /// manifest got rewritten back to this older content.
        /// </summary>
        [Test]
        public void OnlyTheManifestMoved_IsOutdated()
        {
            Assert.AreEqual(UniLfsFileState.Outdated, UniLfsThreeWay.Classify(Shared, Theirs, Shared));
        }

        [Test]
        public void BothMoved_IsConflicted()
        {
            Assert.AreEqual(UniLfsFileState.Conflicted, UniLfsThreeWay.Classify(Mine, Theirs, Shared));
        }

        /// <summary>
        /// Without a baseline the divergence cannot be attributed to either
        /// side, so the reading has to be the one that makes every caller leave
        /// local content alone — Pull skips it, Push keeps its old behaviour.
        /// </summary>
        [Test]
        public void NoBaseline_FallsBackToModified()
        {
            Assert.AreEqual(UniLfsFileState.Modified, UniLfsThreeWay.Classify(Mine, Theirs, null));
            Assert.AreEqual(UniLfsFileState.Modified, UniLfsThreeWay.Classify(Mine, Theirs, ""));
        }
    }

    public class StateCacheBaselineTests
    {
        const string Older = "1111000000000000000000000000000000000000000000000000000000000000";
        const string Newer = "2222000000000000000000000000000000000000000000000000000000000000";
        const string Path1 = "Assets/Art/big.psd";

        string _dir;
        string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = System.IO.Path.Combine(_dir, "statecache.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        [Test]
        public void Unknown_UntilSynced()
        {
            var cache = new UniLfsStateCache(_path);
            Assert.IsNull(cache.GetBaseline(Path1), "a file this machine never synced has nothing to compare against");
        }

        [Test]
        public void Baseline_SurvivesReload()
        {
            var cache = new UniLfsStateCache(_path);
            cache.RecordSynced(Path1, Older);
            cache.Save();

            Assert.AreEqual(Older, new UniLfsStateCache(_path).GetBaseline(Path1));
        }

        /// <summary>
        /// Re-hashing happens every time the file changes on disk, which is
        /// exactly when the baseline matters most. Folding it into the same
        /// entry as the hash cache makes this easy to get wrong: overwrite the
        /// entry wholesale and every local edit erases the record of what the
        /// file was last in sync with.
        /// </summary>
        [Test]
        public void RecordKnown_DoesNotClearTheBaseline()
        {
            var cache = new UniLfsStateCache(_path);
            cache.RecordSynced(Path1, Older);
            cache.RecordKnown(Path1, 1234, 99, Newer);
            cache.Save();

            Assert.AreEqual(Older, new UniLfsStateCache(_path).GetBaseline(Path1),
                "the file changed; which version it was last synced to did not");
        }

        [Test]
        public void RecordSynced_MovesTheBaselineForward()
        {
            var cache = new UniLfsStateCache(_path);
            cache.RecordSynced(Path1, Older);
            cache.RecordSynced(Path1, Newer);
            Assert.AreEqual(Newer, cache.GetBaseline(Path1));
        }

        [Test]
        public void Forget_ClearsTheBaseline()
        {
            var cache = new UniLfsStateCache(_path);
            cache.RecordSynced(Path1, Older);
            cache.Forget(Path1);
            Assert.IsNull(cache.GetBaseline(Path1), "an untracked file has no baseline to keep");
        }

        [Test]
        public void CorruptFile_IsTreatedAsEmpty()
        {
            File.WriteAllText(_path, "{ this is not json");
            var cache = new UniLfsStateCache(_path);
            Assert.IsNull(cache.GetBaseline(Path1));
        }
    }
}
