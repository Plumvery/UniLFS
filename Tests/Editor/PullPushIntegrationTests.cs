using System.IO;
using System.Linq;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// Push and Pull run for real, between two clones of one project and the
    /// storage they share — see <see cref="TwoCloneFixture"/> for the three
    /// directories that make that situation.
    ///
    /// This is the round trip itself: content pushed here arrives there, and
    /// nobody's work is undone on the way. The state patterns two clones can end
    /// up in — one side has the file and the other does not, the two disagree
    /// about a GUID, the same path holds different bytes — are walked in
    /// <see cref="PullPushStatePatternTests"/>.
    /// </summary>
    public class PullPushIntegrationTests : TwoCloneFixture
    {
        /// <summary>
        /// The round trip everything else builds on: one clone tracks and pushes,
        /// git carries the manifest, the other clone pulls the content down.
        /// </summary>
        [Test]
        public void PushedContentReachesTheOtherCloneThroughStorage()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));
            Assert.IsFalse(InManifest(_a, Asset), "Track stages; Push is what records content the team can pull");

            var pushed = Push(_a);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Uploaded);
            Assert.AreEqual(1, pushed.Promoted);

            string hash = ManifestHash(_a, Asset);
            Assert.IsTrue(_server.Has(Prefix, hash), "the blob should be a file in the storage directory");
            CollectionAssert.AreEqual(new[] { hash }, _server.StoredHashes);

            Commit(_a, _b);
            Assert.IsFalse(File.Exists(_b.Abs(Asset)),
                "tracked assets are gitignored, so a clone starts without them");
            Assert.AreEqual(UniLfsFileState.MissingLocal, State(_b, Asset));

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            Assert.AreEqual("version one", ReadAsset(_b, Asset));
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));
        }

        /// <summary>
        /// The 0.3.3 case, end to end: a teammate updates a file this clone
        /// already has. The file is on disk, so nothing reads as missing, and
        /// Pull used to download only what was missing — the update reached
        /// nobody.
        /// </summary>
        [Test]
        public void UpdateToAFileTheCloneAlreadyHasIsPulled()
        {
            SyncBothClones("version one");

            WriteAsset(_a, Asset, "version two");
            var pushed = Push(_a);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Uploaded);
            Commit(_a, _b);

            // Outdated, not Modified: this copy is exactly what the clone last
            // synced, so it is the manifest that moved.
            Assert.AreEqual(UniLfsFileState.Outdated, State(_b, Asset));

            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            Assert.AreEqual("version two", ReadAsset(_b, Asset));
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));
            Assert.AreEqual(2, _server.StoredHashes.Count, "both versions stay in storage");
        }

        /// <summary>
        /// Push rewrites the manifest from what it hashed, so a clone that has
        /// not pulled yet holds an older copy of exactly the file the manifest
        /// just moved on. Pushing it would undo the other person's work.
        /// </summary>
        [Test]
        public void PushFromTheCloneThatIsBehindDoesNotRollBackTheManifest()
        {
            SyncBothClones("version one");

            WriteAsset(_a, Asset, "version two");
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            string theirs = ManifestHash(_b, Asset);
            _server.ResetCounters();

            var pushed = Push(_b);
            AssertNoErrors(pushed);
            Assert.AreEqual(0, pushed.Uploaded);
            CollectionAssert.Contains(pushed.Outdated, Asset);
            Assert.AreEqual(theirs, ManifestHash(_b, Asset),
                "the newer entry has to survive a Push from a clone that is behind");
            Assert.AreEqual(0, _server.Puts, "nothing to upload, so storage should not be written to");

            // And the way out is still open.
            AssertNoErrors(Pull(_b));
            Assert.AreEqual("version two", ReadAsset(_b, Asset));
        }

        /// <summary>
        /// Local work and the manifest moving apart at the same time. Neither
        /// version can be picked automatically, so Pull leaves the file alone and
        /// says so; asking for the manifest's version explicitly still works.
        /// </summary>
        [Test]
        public void LocalEditSurvivesPullUntilRestoreModifiedIsAsked()
        {
            SyncBothClones("version one");

            WriteAsset(_b, Asset, "local experiment");
            Assert.AreEqual(UniLfsFileState.Modified, State(_b, Asset));

            WriteAsset(_a, Asset, "version two");
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            Assert.AreEqual(UniLfsFileState.Conflicted, State(_b, Asset));

            var kept = Pull(_b);
            AssertNoErrors(kept);
            Assert.AreEqual(0, kept.Downloaded);
            CollectionAssert.Contains(kept.Conflicted, Asset);
            Assert.AreEqual("local experiment", ReadAsset(_b, Asset),
                "Pull must not overwrite local work on its own");

            var restored = Pull(_b, true);
            AssertNoErrors(restored);
            Assert.AreEqual(1, restored.Downloaded);
            Assert.AreEqual("version two", ReadAsset(_b, Asset));
            Assert.AreEqual(UniLfsFileState.UpToDate, State(_b, Asset));
        }

        /// <summary>
        /// Blobs are addressed by content hash, so the second clone tracking
        /// byte-identical content costs no transfer. This clone pulled that very
        /// blob earlier, which recorded the proof that storage has it — so the
        /// push does not even ask, let alone upload.
        /// </summary>
        [Test]
        public void ContentTheOtherCloneAlreadyPushedIsNotUploadedAgain()
        {
            SyncBothClones("shared content");
            _server.ResetCounters();

            WriteAsset(_b, SecondAsset, "shared content");
            AssertNoErrors(Track(_b, SecondAsset));
            var pushed = Push(_b);
            AssertNoErrors(pushed);

            Assert.AreEqual(0, pushed.Uploaded, "the blob was already in storage");
            Assert.AreEqual(0, _server.Puts);
            Assert.AreEqual(0, _server.Heads,
                "this clone pulled the blob itself, and that proof spares the existence check");
            Assert.AreEqual(ManifestHash(_b, Asset), ManifestHash(_b, SecondAsset));
            CollectionAssert.AreEqual(new[] { ManifestHash(_b, Asset) }, _server.StoredHashes,
                "one blob, two paths");

            // And the other clone can pull the new path out of the blob it
            // already pushed itself.
            Commit(_b, _a);
            var pulled = Pull(_a);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded);
            Assert.AreEqual("shared content", ReadAsset(_a, SecondAsset));
        }

        /// <summary>
        /// A clone with no record of a blob cannot take its presence on faith:
        /// this clone never pushed or pulled the content, so its push has to ask
        /// storage — and the answer spares the upload, not the question.
        /// </summary>
        [Test]
        public void CloneWithoutProofStillAsksStorageBeforeSkippingTheUpload()
        {
            WriteAsset(_a, Asset, "shared content");
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            _server.ResetCounters();

            // B tracks byte-identical content under its own path without ever
            // pulling, so nothing has recorded proof on B's side.
            WriteAsset(_b, SecondAsset, "shared content");
            AssertNoErrors(Track(_b, SecondAsset));
            var pushed = Push(_b);
            AssertNoErrors(pushed);

            Assert.Greater(_server.Heads, 0, "no local proof, so Push has to ask storage");
            Assert.AreEqual(0, _server.Puts, "storage answered it has the blob, so nothing is re-sent");
            Assert.AreEqual(0, pushed.Uploaded);
            Assert.AreEqual(1, pushed.Promoted);
        }

        /// <summary>
        /// The point of trusting recorded proof: a push with nothing to do
        /// touches storage not at all. Before this, every push asked about
        /// every tracked blob, so a no-change push cost one round trip per file
        /// — the slow part of pushing a project where nothing moved.
        /// </summary>
        [Test]
        public void PushWithNothingChangedMakesNoRemoteRequests()
        {
            SyncBothClones("version one");
            _server.ResetCounters();

            var pushed = Push(_a);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Skipped);
            Assert.AreEqual(0, pushed.Uploaded);
            Assert.AreEqual(0, _server.Heads, "the blob's presence is already proven by this clone's own push");
            Assert.AreEqual(0, _server.Puts);
            Assert.AreEqual(0, _server.Gets);

            var pulled = Pull(_a);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Skipped);
            Assert.AreEqual(0, _server.Heads, "a pull with nothing to download needs no requests either");
            Assert.AreEqual(0, _server.Gets);
        }

        /// <summary>
        /// The stale-proof case trusting the record opens up, and the way back
        /// out. A blob deleted from the bucket hides behind the recorded
        /// confirmation, so a push alone no longer notices — Verify is what
        /// retracts the record, and the next push then asks, hears "no", and
        /// re-uploads.
        /// </summary>
        [Test]
        public void VerifyRetractsStaleProofSoPushUploadsAgain()
        {
            SyncBothClones("version one");
            string hash = ManifestHash(_a, Asset);
            _server.Delete(Prefix, hash);
            _server.ResetCounters();

            // The record still vouches for the blob, so this push skips it.
            var trusting = Push(_a);
            AssertNoErrors(trusting);
            Assert.AreEqual(0, _server.Puts);
            Assert.IsFalse(_server.Has(Prefix, hash), "nothing noticed the hole yet");

            // Verify asks for real and retracts the confirmation ...
            Assert.IsTrue(Verify(_a).HasErrors, "Verify must report the blob storage lost");

            // ... which is what lets the next push heal the bucket.
            var healing = Push(_a);
            AssertNoErrors(healing);
            Assert.AreEqual(1, healing.Uploaded);
            Assert.IsTrue(_server.Has(Prefix, hash), "the re-upload puts the blob back");
        }

        /// <summary>
        /// A manifest is committed to git; the blobs it names are not. Push is
        /// the only thing that writes an entry, and only for content storage
        /// confirmed, so "I forgot to Push" can no longer produce a manifest
        /// nobody can pull from. Verify covers what that guarantee cannot: a
        /// hand-edited or badly merged manifest, and a bucket someone emptied
        /// afterwards.
        /// </summary>
        [Test]
        public void VerifyReportsManifestEntriesStorageCannotBack()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            var clean = Verify(_b);
            AssertNoErrors(clean);
            Assert.AreEqual(1, clean.Skipped, "one blob confirmed present");

            // Someone empties the bucket behind the project's back. The manifest
            // cannot notice on its own — asking storage is the only way.
            _server.Delete(Prefix, ManifestHash(_b, Asset));
            var missing = Verify(_b);
            Assert.IsTrue(missing.HasErrors, "a blob that left storage must stop passing Verify");
            Assert.IsTrue(missing.Errors.Any(e => e.Contains(Asset)),
                "the report should name the file: " + string.Join("; ", missing.Errors.ToArray()));
        }

        /// <summary>
        /// The failure the whole staging split removes: committing a manifest
        /// entry for content that was never uploaded. Track cannot write one, so
        /// a clone that carries the commit gets nothing to fail on.
        /// </summary>
        [Test]
        public void TrackingWithoutPushingCommitsNothingForAnyoneToFailOn()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));
            CollectionAssert.AreEqual(new[] { Asset }, StagedPaths(_a));
            CollectionAssert.IsEmpty(ManifestPaths(_a));

            Commit(_a, _b); // the manifest is committed, the Push forgotten
            CollectionAssert.IsEmpty(ManifestPaths(_b), "staging is local; it does not travel");
            AssertNoErrors(Verify(_b));
            CollectionAssert.IsEmpty(TrackedPaths(_b));

            // And the file is hidden from git in the meantime, through the
            // exclude file rather than the committed .gitignore.
            CollectionAssert.Contains(LocallyExcluded(_a), "/" + Asset);
            CollectionAssert.DoesNotContain(CommittedIgnoreLines(_a), "/" + Asset);

            // Push moves it from one to the other, adding before removing.
            AssertNoErrors(Push(_a));
            CollectionAssert.Contains(CommittedIgnoreLines(_a), "/" + Asset);
            CollectionAssert.DoesNotContain(LocallyExcluded(_a), "/" + Asset);
            CollectionAssert.IsEmpty(StagedPaths(_a), "the manifest is the record now");
        }

        /// <summary>
        /// The fixture verifies SigV4 signatures, so the other tests here mean
        /// something: they are not passing against a server that lets anything
        /// through. A refused request also has to surface as a failure rather
        /// than as a quiet no-op.
        /// </summary>
        [Test]
        public void StorageRefusesRequestsSignedWithTheWrongSecret()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));

            using (UniLfsPaths.OverrideProjectRoot(_a.Root))
                new UniLfsUserSettings { s3AccessKeyId = AccessKeyId, s3SecretAccessKey = "not-the-secret" }.Save();

            var pushed = Push(_a);
            Assert.IsTrue(pushed.HasErrors, "a refused request must not pass for success");
            Assert.AreEqual(0, pushed.Uploaded);
            Assert.AreEqual(0, pushed.Promoted);
            Assert.Greater(_server.Rejected, 0);
            CollectionAssert.IsEmpty(_server.StoredHashes);
            // A Push that could not confirm anything writes no entry, and the
            // file stays staged: still tracked here, still nobody else's.
            CollectionAssert.IsEmpty(ManifestPaths(_a));
            CollectionAssert.AreEqual(new[] { Asset }, StagedPaths(_a));
        }

        /// <summary>
        /// Unity discards a .meta whose asset it cannot find, and mints a fresh
        /// GUID when the asset turns up — breaking every scene and prefab
        /// reference to it. The GUID recorded at Track time is what Pull puts
        /// back, and this is the trip that has to carry it.
        /// </summary>
        [Test]
        public void PulledAssetKeepsTheGuidTheOtherCloneRecorded()
        {
            WriteAsset(_a, Asset, "version one");
            WriteMeta(_a, Asset, AssetGuid);

            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Assert.AreEqual(AssetGuid, ManifestGuid(_a, Asset),
                "Push reads the GUID with the content, so a clone can put it back");

            // The .meta deliberately does not travel: this is the clone where
            // Unity already threw it away, because the gitignored asset was not
            // there when it looked.
            Commit(_a, _b, false);
            AssertNoErrors(Pull(_b));

            Assert.AreEqual(AssetGuid, MetaGuid(_b, Asset),
                "a fresh GUID here would break every reference to the asset");
        }
    }
}
