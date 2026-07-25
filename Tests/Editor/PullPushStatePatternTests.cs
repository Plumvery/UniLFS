using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// The states two clones of one project can actually be in when Push or Pull
    /// runs, walked one at a time against the real storage fixture
    /// (<see cref="TwoCloneFixture"/>).
    ///
    /// <see cref="PullPushIntegrationTests"/> covers the round trip working.
    /// This file covers the shapes it has to survive, in three groups, because
    /// three things can differ between two clones and each has its own failure:
    ///
    /// - Presence. One clone has the file on disk and the other does not — a
    ///   fresh clone, a deleted file, a stand-in written for content that has
    ///   not arrived. What must not happen is Push treating "not here" as
    ///   "deleted" and taking the file away from everyone.
    /// - Identity. The .meta files and the GUID recorded in the manifest
    ///   disagree. Nothing about the content is wrong, and every scene, prefab
    ///   and Addressables reference to the asset breaks anyway, silently, which
    ///   is why it is reported rather than guessed at.
    /// - Content. The same path holds different bytes on the two sides. Which
    ///   one wins is never decided by whoever ran an operation last.
    /// </summary>
    public class PullPushStatePatternTests : TwoCloneFixture
    {
        /// <summary>
        /// Content that has to cross the wire in more than one buffer, and whose
        /// length says nothing about what is in it.
        /// </summary>
        const string BinaryAsset = "Assets/Art/atlas.bin";

        // ----- presence: one clone has the file, the other does not -----

        /// <summary>
        /// Both clones added a file the other has never seen. The manifest is one
        /// line per file so git merges the two additions without asking, and each
        /// clone then has to fetch exactly the one it is missing — not re-fetch
        /// the one it wrote itself.
        /// </summary>
        [Test]
        public void EachCloneGetsTheFileTheOtherAddedWhenTheManifestsMerge()
        {
            WriteAsset(_a, Asset, "from clone a");
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));

            WriteAsset(_b, SecondAsset, "from clone b");
            AssertNoErrors(Track(_b, SecondAsset));
            AssertNoErrors(Push(_b));
            Assert.AreEqual(2, _server.StoredHashes.Count, "two files, two blobs");

            MergeManifests(_a, _b);
            _server.ResetCounters();

            var intoA = Pull(_a);
            AssertNoErrors(intoA);
            Assert.AreEqual(1, intoA.Downloaded, "only the file this clone does not have");
            Assert.AreEqual(1, intoA.Skipped, "the one it wrote itself is already up to date");
            Assert.AreEqual("from clone b", ReadAsset(_a, SecondAsset));

            var intoB = Pull(_b);
            AssertNoErrors(intoB);
            Assert.AreEqual(1, intoB.Downloaded);
            Assert.AreEqual(1, intoB.Skipped);
            Assert.AreEqual("from clone a", ReadAsset(_b, Asset));

            Assert.AreEqual(2, _server.Gets, "one download each, and nothing fetched twice");
        }

        /// <summary>
        /// A clone whose editor started before Pull ran has a placeholder at the
        /// asset's path: real bytes are not here, but something has to be, or
        /// Unity discards the .meta and the GUID with it. That stand-in is the
        /// most dangerous file in the project — Push rewrites the manifest from
        /// what it hashes, so uploading it would point every clone at a few
        /// hundred bytes of explanation and orphan the real blob.
        /// </summary>
        [Test]
        public void APlaceholderStandingInForMissingContentIsPulledOverAndNeverPushed()
        {
            WriteAsset(_a, Asset, "version one");
            WriteMeta(_a, Asset, AssetGuid);
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            string blob = ManifestHash(_a, Asset);

            // The .meta does not travel: this is the clone where Unity already
            // discarded it, because the gitignored asset was not there.
            Commit(_a, _b, false);

            var guard = Guard(_b);
            Assert.AreEqual(1, guard.MetaFilesRestored, "the GUID in the manifest is what puts the .meta back");
            Assert.AreEqual(1, guard.PlaceholdersWritten);
            Assert.AreEqual(AssetGuid, MetaGuid(_b, Asset));
            Assert.IsTrue(UniLfsPlaceholder.IsPlaceholder(_b.Abs(Asset)));
            Assert.AreEqual(UniLfsFileState.MissingLocal, State(_b, Asset),
                "a stand-in has to read as absent, or Pull skips it and Push offers it");

            _server.ResetCounters();
            var pushed = Push(_b);
            AssertNoErrors(pushed);
            Assert.AreEqual(0, pushed.Uploaded);
            CollectionAssert.Contains(pushed.MissingLocal, Asset);
            Assert.AreEqual(0, _server.Puts);
            Assert.AreEqual(blob, ManifestHash(_b, Asset), "the entry still names the real content");
            CollectionAssert.AreEqual(new[] { blob }, _server.StoredHashes, "the stand-in never became a blob");

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            Assert.IsFalse(UniLfsPlaceholder.IsPlaceholder(_b.Abs(Asset)));
            Assert.AreEqual("version one", ReadAsset(_b, Asset));
            Assert.AreEqual(AssetGuid, MetaGuid(_b, Asset),
                "the GUID the stand-in was holding open has to survive the real content landing on it");
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));
        }

        /// <summary>
        /// Someone deleted the asset in Explorer. Only this machine's copy is
        /// gone: the manifest still names it and storage still has it, so the
        /// file must come back rather than the deletion propagating to everyone
        /// through the next Push.
        /// </summary>
        [Test]
        public void AFileDeletedFromDiskComesBackInsteadOfLeavingTheManifest()
        {
            SyncBothClones("version one");
            string blob = ManifestHash(_b, Asset);

            DeleteAsset(_b, Asset);
            Assert.AreEqual(UniLfsFileState.MissingLocal, State(_b, Asset));
            _server.ResetCounters();

            var pushed = Push(_b);
            AssertNoErrors(pushed);
            Assert.AreEqual(0, pushed.Uploaded);
            CollectionAssert.Contains(pushed.MissingLocal, Asset);
            Assert.AreEqual(0, _server.Puts);
            Assert.AreEqual(blob, ManifestHash(_b, Asset),
                "deleting a local copy is not a decision about the file everyone else has");
            Assert.IsTrue(_server.Has(Prefix, blob));

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            Assert.AreEqual("version one", ReadAsset(_b, Asset));
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));
        }

        /// <summary>
        /// Untracking is a manifest edit, not a delete: the file stays on disk in
        /// the clone that ran it, and the clone that receives the manifest keeps
        /// its own copy too — it simply stops being UniLFS's business. The blob
        /// stays in storage, because older commits still reference it.
        /// </summary>
        [Test]
        public void UntrackingInOneCloneLeavesBothCopiesOnDiskAndOutOfTheManifest()
        {
            SyncBothClones("version one");
            string blob = ManifestHash(_a, Asset);

            var untracked = Untrack(_a, Asset);
            Assert.AreEqual(1, untracked.Untracked);
            CollectionAssert.IsEmpty(UniLfsManifest.Load(_a.ManifestPath).files);
            Assert.AreEqual("version one", ReadAsset(_a, Asset), "Untrack does not touch content");

            Commit(_a, _b);
            CollectionAssert.IsEmpty(TrackedPaths(_b), "the manifest is what says a file is tracked");
            CollectionAssert.DoesNotContain(UniLfsGitIgnore.ReadManagedLines(_b.GitIgnorePath), "/" + Asset,
                "and it stops being hidden from git, so the copy on disk can be committed normally");
            _server.ResetCounters();

            AssertNoErrors(Pull(_b));
            AssertNoErrors(Push(_b));
            Assert.AreEqual(0, _server.Gets + _server.Puts, "an untracked file is not UniLFS's to move");
            Assert.AreEqual("version one", ReadAsset(_b, Asset));
            Assert.IsTrue(_server.Has(Prefix, blob), "untracking is not a request to delete anything from storage");
        }

        /// <summary>
        /// Untracking something that was only ever staged. There is no manifest
        /// entry to remove, so the whole job is undoing what Track did here —
        /// including the local ignore line, or the file would stay hidden from
        /// git while nothing was tracking it either.
        /// </summary>
        [Test]
        public void UntrackingAStagedFileTakesBackTheLocalIgnoreToo()
        {
            WriteAsset(_a, Asset, "not pushed anywhere");
            AssertNoErrors(Track(_a, Asset));
            CollectionAssert.Contains(LocallyExcluded(_a), "/" + Asset);

            var untracked = Untrack(_a, Asset);
            Assert.AreEqual(1, untracked.Untracked);
            CollectionAssert.IsEmpty(StagedPaths(_a));
            CollectionAssert.DoesNotContain(LocallyExcluded(_a), "/" + Asset,
                "a file nothing tracks has to be git's again");
            Assert.AreEqual("not pushed anywhere", ReadAsset(_a, Asset), "and it stays on disk");
            CollectionAssert.IsEmpty(TrackedPaths(_a));
        }

        /// <summary>
        /// A staged file deleted before it was ever pushed. Nothing can bring it
        /// back — no manifest entry, no blob — so the one thing that must not
        /// happen is Push inventing an entry for content it could not read.
        /// </summary>
        [Test]
        public void AStagedFileDeletedBeforeItsFirstPushIsReportedAndNotInvented()
        {
            WriteAsset(_a, Asset, "gone before it left");
            AssertNoErrors(Track(_a, Asset));
            Assert.AreEqual(UniLfsFileState.Staged, State(_a, Asset));

            DeleteAsset(_a, Asset);
            Assert.AreEqual(UniLfsFileState.MissingLocal, State(_a, Asset));

            var pushed = Push(_a);
            AssertNoErrors(pushed);
            CollectionAssert.Contains(pushed.MissingLocal, Asset);
            Assert.AreEqual(0, pushed.Promoted);
            CollectionAssert.IsEmpty(ManifestPaths(_a));
            CollectionAssert.IsEmpty(_server.StoredHashes);
            CollectionAssert.AreEqual(new[] { Asset }, StagedPaths(_a),
                "still tracked here: Push failing to find the file is not a decision to stop");
        }

        // ----- identity: the two clones disagree about a .meta -----

        /// <summary>
        /// Unity re-imported the asset in this clone and minted a new GUID for
        /// it, so the .meta on disk and the GUID in the manifest name two
        /// different assets. Nothing downstream fixes this: UniLFS never edits a
        /// .meta that exists, so the content arrives under the wrong identity and
        /// every reference to the old one stays broken. The report is the whole
        /// defence, which is why it has to fire whether or not the content is
        /// there.
        /// </summary>
        [Test]
        public void AMetaWhoseGuidDriftedIsReportedRatherThanQuietlyPulledOver()
        {
            WriteAsset(_a, Asset, "version one");
            WriteMeta(_a, Asset, AssetGuid);
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            AssertNoErrors(Pull(_b));
            CollectionAssert.IsEmpty(Guard(_b).GuidDrift, "the two clones agree so far");

            WriteMeta(_b, Asset, OtherGuid);
            var drifted = Guard(_b);
            CollectionAssert.Contains(drifted.GuidDrift, Asset);
            Assert.AreEqual(0, drifted.MetaFilesRestored, "the .meta is wrong, not missing");
            Assert.AreEqual(0, drifted.PlaceholdersWritten, "the content is right there");

            // The same disagreement with the content gone: this is the shape the
            // check is placed early for, because the restore path below it only
            // ever writes a .meta that is absent.
            DeleteAsset(_b, Asset);
            var stillDrifted = Guard(_b);
            CollectionAssert.Contains(stillDrifted.GuidDrift, Asset);
            Assert.AreEqual(0, stillDrifted.MetaFilesRestored);

            AssertNoErrors(Pull(_b));
            Assert.AreEqual("version one", ReadAsset(_b, Asset), "the content is not in doubt");
            Assert.AreEqual(OtherGuid, MetaGuid(_b, Asset),
                "and it comes back under the wrong GUID: git is the only place the right .meta still exists");
        }

        /// <summary>
        /// The manifest carries a GUID next to the hash, so a GUID that changed
        /// on its own still has to reach the other clone — and must not be
        /// mistaken for a content change on the way. The .meta and the manifest
        /// are separate files in the same commit, so the window where one has
        /// arrived and the other has not is exactly what drift looks like from
        /// the receiving end.
        /// </summary>
        [Test]
        public void AGuidOnlyChangeTravelsThroughTheManifestWithoutMovingAnyContent()
        {
            WriteAsset(_a, Asset, "version one");
            WriteMeta(_a, Asset, AssetGuid);
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            AssertNoErrors(Pull(_b));
            string blob = ManifestHash(_a, Asset);
            _server.ResetCounters();

            WriteMeta(_a, Asset, OtherGuid);
            var pushed = Push(_a);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Skipped, "the bytes did not move");
            Assert.AreEqual(0, pushed.Uploaded);
            Assert.AreEqual(0, _server.Puts, "a GUID is not content");
            // Push reads the .meta with the content it is committing, so a
            // re-import that minted a new GUID travels without anyone having to
            // re-track anything.
            Assert.AreEqual(OtherGuid, ManifestGuid(_a, Asset));
            Assert.AreEqual(blob, ManifestHash(_a, Asset));

            Commit(_a, _b, false);
            CollectionAssert.Contains(Guard(_b).GuidDrift, Asset,
                "the manifest arrived and the .meta did not; that is worth saying out loud");
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset), "nothing about the content changed");
            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(0, pulled.Downloaded);

            Commit(_a, _b);
            Assert.AreEqual(OtherGuid, MetaGuid(_b, Asset));
            CollectionAssert.IsEmpty(Guard(_b).GuidDrift, "the .meta caught up");
        }

        /// <summary>
        /// A file tracked before Unity ever wrote a .meta for it has no GUID
        /// recorded anywhere. The other clone cannot be given the right identity,
        /// so it is told rather than handed a made-up one — a GUID UniLFS
        /// invented would look exactly as authoritative as a real one and be
        /// wrong on every other machine.
        /// </summary>
        [Test]
        public void ATrackedFileWithNoGuidAnywhereIsFlaggedInsteadOfGettingAnInventedOne()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Assert.IsTrue(string.IsNullOrEmpty(ManifestGuid(_a, Asset)), "there was no .meta to read one from");
            Commit(_a, _b);

            var guard = Guard(_b);
            CollectionAssert.Contains(guard.MetaMissingNoGuid, Asset);
            Assert.AreEqual(0, guard.MetaFilesRestored, "there is no recorded GUID to restore one from");
            Assert.AreEqual(1, guard.PlaceholdersWritten, "the stand-in still keeps whatever Unity mints stable");
            Assert.IsFalse(HasMeta(_b, Asset));

            AssertNoErrors(Pull(_b));
            Assert.AreEqual("version one", ReadAsset(_b, Asset));
            Assert.IsFalse(HasMeta(_b, Asset), "Unity writes this one; UniLFS has nothing to put back");
        }

        // ----- content: the same path, different bytes -----

        /// <summary>
        /// Two people added the same file with byte-identical content — the same
        /// export, run twice. Content addressing means they agree without ever
        /// having talked, so this must not read as a divergence and must not cost
        /// a transfer in either direction.
        /// </summary>
        [Test]
        public void IdenticalContentTrackedIndependentlyIsNotADivergence()
        {
            WriteAsset(_a, Asset, "identical bytes");
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));

            WriteAsset(_b, Asset, "identical bytes");
            AssertNoErrors(Track(_b, Asset));
            CollectionAssert.AreEqual(new[] { Asset }, StagedPaths(_b));

            Commit(_a, _b);
            _server.ResetCounters();
            // Staged here and named by the manifest, which is the shape of a
            // conflict — except the bytes agree, so there is nothing to resolve.
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(0, pulled.Downloaded);
            Assert.AreEqual(1, pulled.Skipped);
            Assert.AreEqual(0, _server.Gets);

            var pushed = Push(_b);
            AssertNoErrors(pushed);
            Assert.AreEqual(0, pushed.Uploaded);
            Assert.AreEqual(1, pushed.Skipped);
            Assert.AreEqual(0, pushed.Promoted, "the entry was already there, and it already said this");
            Assert.AreEqual(0, _server.Puts);
            CollectionAssert.AreEqual(new[] { ManifestHash(_b, Asset) }, _server.StoredHashes, "one blob, not two");
            CollectionAssert.IsEmpty(StagedPaths(_b));
        }

        /// <summary>
        /// Two people added the same path with different content, neither having
        /// seen the other's. There is no shared history to compare against, so
        /// nothing may pick a winner: this clone's copy was never uploaded, so
        /// overwriting it would destroy the only version of it that exists.
        ///
        /// Being staged is what says so. Before Track stopped writing the
        /// manifest it also recorded a baseline, and a baseline means "this
        /// machine agreed with the manifest at that hash" — an agreement with
        /// itself, here — which made this read as Outdated and let Pull replace
        /// the file without a word.
        /// </summary>
        [Test]
        public void DifferentContentTrackedIndependentlyIsAConflictNeitherSideWins()
        {
            WriteAsset(_a, Asset, "the version from a");
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            string theirs = ManifestHash(_a, Asset);

            WriteAsset(_b, Asset, "the version from b");
            AssertNoErrors(Track(_b, Asset));
            CollectionAssert.AreEqual(new[] { Asset }, StagedPaths(_b));

            // Their manifest arrives naming a path this clone staged of its own.
            Commit(_a, _b);
            Assert.AreEqual(UniLfsFileState.Conflicted, State(_b, Asset),
                "tracked separately on both sides is not a history either version can be measured against");

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(0, pulled.Downloaded);
            CollectionAssert.Contains(pulled.Conflicted, Asset);
            Assert.AreEqual("the version from b", ReadAsset(_b, Asset),
                "this content is on one disk and in no bucket; Pull may not be what removes it");

            _server.ResetCounters();
            var pushed = Push(_b);
            AssertNoErrors(pushed);
            Assert.AreEqual(0, pushed.Uploaded);
            CollectionAssert.Contains(pushed.Conflicted, Asset);
            Assert.AreEqual(theirs, ManifestHash(_b, Asset), "and Push may not be what overwrites theirs");
            Assert.AreEqual(0, _server.Puts);

            // Both ways out stay open, and both are somebody saying which.
            var restored = Pull(_b, true);
            AssertNoErrors(restored);
            Assert.AreEqual("the version from a", ReadAsset(_b, Asset));
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));
            CollectionAssert.IsEmpty(StagedPaths(_b), "the path is in the manifest now, so staging has nothing left to say");
        }

        /// <summary>
        /// The other half of a conflict: keeping the local version. Push refuses
        /// to pick a side, Keep Mine is how a person says "mine" — recorded, so
        /// that an unattended Push can act on it too — and the result has to
        /// reach the clone that pushed the version being replaced, which is now
        /// the one that is behind.
        /// </summary>
        [Test]
        public void AConflictResolvedWithKeepMineIsPushedBackToTheOtherClone()
        {
            SyncBothClones("version one");

            WriteAsset(_b, Asset, "the version worth keeping");
            WriteAsset(_a, Asset, "version two");
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            Assert.AreEqual(UniLfsFileState.Conflicted, State(_b, Asset));
            string theirs = ManifestHash(_b, Asset);
            _server.ResetCounters();

            var refused = Push(_b);
            AssertNoErrors(refused);
            Assert.AreEqual(0, refused.Uploaded);
            CollectionAssert.Contains(refused.Conflicted, Asset);
            Assert.AreEqual(0, _server.Puts);
            Assert.AreEqual(theirs, ManifestHash(_b, Asset), "Push does not get to choose");

            var kept = KeepMine(_b, Asset);
            AssertNoErrors(kept);
            CollectionAssert.Contains(kept.Conflicted, Asset);
            Assert.AreEqual(theirs, ManifestHash(_b, Asset),
                "the decision is recorded, not acted on: nothing is in storage to point the manifest at yet");

            var pushed = Push(_b);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Uploaded);
            Assert.AreNotEqual(theirs, ManifestHash(_b, Asset));
            Assert.AreEqual(3, _server.StoredHashes.Count, "every version anyone pushed stays addressable");

            Commit(_b, _a);
            Assert.AreEqual(UniLfsFileState.Outdated, State(_a, Asset),
                "the clone that pushed last is the one that is behind now");
            var pulled = Pull(_a);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            Assert.AreEqual("the version worth keeping", ReadAsset(_a, Asset));
            Assert.AreEqual(ReadAsset(_b, Asset), ReadAsset(_a, Asset), "both clones end on the same content");
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_a, Asset));
        }

        /// <summary>
        /// Content differing in one byte out of two hundred thousand, at the same
        /// length — the shape a re-exported asset actually has, and the one a
        /// cheaper check than hashing would miss. It also puts real binary
        /// content through the transfer path, where the existing tests only put a
        /// line of ASCII.
        /// </summary>
        [Test]
        public void AOneByteChangeIsANewBlobAndReachesTheOtherClone()
        {
            var original = Bytes(192 * 1024, 20260725);
            WriteAssetBytes(_a, BinaryAsset, original);
            AssertNoErrors(Track(_a, BinaryAsset));
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            AssertNoErrors(Pull(_b));
            CollectionAssert.AreEqual(original, ReadAssetBytes(_b, BinaryAsset), "byte for byte, through storage");

            var edited = (byte[])original.Clone();
            edited[edited.Length / 2] ^= 0xFF;
            WriteAssetBytes(_a, BinaryAsset, edited);
            var pushed = Push(_a);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Uploaded);
            Assert.AreEqual(2, _server.StoredHashes.Count, "same length, different content, different blob");

            Commit(_a, _b);
            Assert.AreEqual(UniLfsFileState.Outdated, State(_b, BinaryAsset));

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            CollectionAssert.AreEqual(edited, ReadAssetBytes(_b, BinaryAsset));
            Assert.AreEqual(original.Length, new FileInfo(_b.Abs(BinaryAsset)).Length);
        }
    }
}
