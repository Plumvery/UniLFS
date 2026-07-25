using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// Push and Pull run for real, between two clones of one project and the
    /// storage they share: three directories, one per clone plus one standing in
    /// for the bucket, with <see cref="FakeRemoteFileServer"/> in front of it.
    ///
    /// The unit tests elsewhere pin the pieces — the three-way rules, the
    /// baseline record, the signer, the manifest. What none of them can reach is
    /// the thing UniLFS is for: whether content one person pushes actually
    /// arrives for the next person, and whether that person's own work survives
    /// the trip. Every rule about Outdated, Conflicted and rolled-back manifests
    /// exists for a situation with two machines in it, and a single project
    /// directory cannot produce one.
    ///
    /// "git" here is <see cref="Commit"/>: it carries the manifest, the managed
    /// .gitignore and the .meta files between the two clones, and deliberately
    /// leaves the tracked assets behind, because those are gitignored — which is
    /// the whole reason Pull exists.
    /// </summary>
    public class PullPushIntegrationTests
    {
        const string Bucket = "unilfs-test";
        const string Prefix = "unilfs";
        const string AccessKeyId = "UNILFSTESTACCESSKEY";
        const string SecretAccessKey = "unilfs-test-secret-access-key-0123456789";
        const string Asset = "Assets/Art/big.bin";
        const string SecondAsset = "Assets/Art/duplicate.bin";
        const string AssetGuid = "0123456789abcdef0123456789abcdef";

        string _tempRoot;
        FakeRemoteFileServer _server;
        Workspace _a;
        Workspace _b;
        CancellationTokenSource _deadline;
        Dictionary<string, string> _savedEnvironment;
        int _writes;

        /// <summary>One clone: a project root with its own manifest, Library/ and settings.</summary>
        class Workspace
        {
            public readonly string Name;
            public readonly string Root;

            public Workspace(string name, string root)
            {
                Name = name;
                Root = root;
            }

            public string Abs(string projectRelativePath)
            {
                return Path.Combine(Root, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            public string ManifestPath
            {
                get { return Path.Combine(Root, UniLfsPaths.ManifestFileName); }
            }
        }

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "unilfs-pullpush-" + Guid.NewGuid().ToString("N"));
            // UniLFS never times its own transfers out on purpose (multi-gigabyte
            // uploads must not hit a clock), so cancellation is the only thing
            // standing between a wedged fixture and a hung editor.
            _deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            // Environment credentials outrank the per-project ones, so a machine
            // with real ones configured would sign every request in here with
            // them and get a 403 back. Cleared for the duration.
            _savedEnvironment = new Dictionary<string, string>();
            foreach (var name in new[] { UniLfsCredentials.EnvS3AccessKeyId, UniLfsCredentials.EnvS3SecretAccessKey })
            {
                _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }

            _server = new FakeRemoteFileServer(Path.Combine(_tempRoot, "storage"), Bucket, AccessKeyId, SecretAccessKey);
            _a = CreateWorkspace("clone-a");
            _b = CreateWorkspace("clone-b");
        }

        [TearDown]
        public void TearDown()
        {
            var faults = _server != null ? _server.Faults : new List<string>();
            if (_server != null) _server.Dispose();
            if (_deadline != null) _deadline.Dispose();
            if (_savedEnvironment != null)
                foreach (var kv in _savedEnvironment)
                    Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); }
            catch (Exception) { }

            // A test that read a 500 out of a broken fixture would otherwise
            // report a product failure.
            CollectionAssert.IsEmpty(faults, "the storage fixture itself failed");
        }

        /// <summary>
        /// The round trip everything else builds on: one clone tracks and pushes,
        /// git carries the manifest, the other clone pulls the content down.
        /// </summary>
        [Test]
        public void PushedContentReachesTheOtherCloneThroughStorage()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));

            var pushed = Push(_a);
            AssertNoErrors(pushed);
            Assert.AreEqual(1, pushed.Uploaded);

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
        /// byte-identical content costs an existence check and no transfer. This
        /// is also the check that proves Push asks storage rather than trusting
        /// its own per-machine record, which a fresh clone does not have.
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
            Assert.Greater(_server.Heads, 0, "Push has to ask storage before it can skip the upload");
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
        /// A manifest is committed to git; the blobs it names are not. Verify is
        /// what stops "I forgot to Push" from reaching everyone else as a file
        /// nobody can download, and it has to keep working when storage loses a
        /// blob afterwards.
        /// </summary>
        [Test]
        public void VerifyReportsManifestEntriesStorageCannotBack()
        {
            WriteAsset(_a, Asset, "version one");
            AssertNoErrors(Track(_a, Asset));
            Commit(_a, _b); // manifest committed, Push forgotten

            var missing = Verify(_b);
            Assert.IsTrue(missing.HasErrors, "an unpushed blob must fail Verify");
            Assert.IsTrue(missing.Errors.Any(e => e.Contains(Asset)),
                "the report should name the file: " + string.Join("; ", missing.Errors.ToArray()));

            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            var clean = Verify(_b);
            AssertNoErrors(clean);
            Assert.AreEqual(1, clean.Skipped, "one blob confirmed present");

            // Someone empties the bucket behind the project's back. The manifest
            // cannot notice on its own — asking storage is the only way.
            _server.Delete(Prefix, ManifestHash(_b, Asset));
            Assert.IsTrue(Verify(_b).HasErrors, "a blob that left storage must stop passing Verify");
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
            string tracked = ManifestHash(_a, Asset);

            using (UniLfsPaths.OverrideProjectRoot(_a.Root))
                new UniLfsUserSettings { s3AccessKeyId = AccessKeyId, s3SecretAccessKey = "not-the-secret" }.Save();

            var pushed = Push(_a);
            Assert.IsTrue(pushed.HasErrors, "a refused request must not pass for success");
            Assert.AreEqual(0, pushed.Uploaded);
            Assert.Greater(_server.Rejected, 0);
            Assert.IsFalse(_server.Has(Prefix, tracked));
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
            File.WriteAllText(_a.Abs(Asset) + UniLfsMetaFile.Extension,
                "fileFormatVersion: 2\nguid: " + AssetGuid + "\n", new UTF8Encoding(false));

            AssertNoErrors(Track(_a, Asset));
            Assert.AreEqual(AssetGuid, UniLfsManifest.Load(_a.ManifestPath).Find(Asset).guid,
                "Track records the GUID so a clone can put it back");
            AssertNoErrors(Push(_a));

            // The .meta deliberately does not travel: this is the clone where
            // Unity already threw it away, because the gitignored asset was not
            // there when it looked.
            Commit(_a, _b, false);
            AssertNoErrors(Pull(_b));

            Assert.AreEqual(AssetGuid, UniLfsMetaFile.ReadGuid(_b.Abs(Asset) + UniLfsMetaFile.Extension),
                "a fresh GUID here would break every reference to the asset");
        }

        // ----- fixture -----

        Workspace CreateWorkspace(string name)
        {
            string root = Path.Combine(_tempRoot, name);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            using (UniLfsPaths.OverrideProjectRoot(root))
            {
                new UniLfsSettings
                {
                    provider = UniLfsSettings.ProviderS3,
                    s3Endpoint = _server.Endpoint,
                    s3Bucket = Bucket,
                    s3Region = "auto",
                    s3Prefix = Prefix,
                    // More than one, so the parallel paths in Push and Pull are
                    // the ones under test rather than a serial special case.
                    parallelTransfers = 2,
                }.Save();
                new UniLfsUserSettings
                {
                    s3AccessKeyId = AccessKeyId,
                    s3SecretAccessKey = SecretAccessKey,
                }.Save();
            }
            return new Workspace(name, root);
        }

        /// <summary>Where a test starts from "the two clones were already in sync".</summary>
        void SyncBothClones(string content)
        {
            WriteAsset(_a, Asset, content);
            AssertNoErrors(Track(_a, Asset));
            AssertNoErrors(Push(_a));
            Commit(_a, _b);
            var pulled = Pull(_b);
            AssertNoErrors(pulled);
            Assert.AreEqual(1, pulled.Downloaded, "the fixture should leave both clones in sync");
            Assert.AreEqual(content, ReadAsset(_b, Asset));
        }

        /// <summary>
        /// What git carries between the clones: the manifest, the managed
        /// .gitignore and (unless a test is reproducing a clone that lost them)
        /// the .meta files. The tracked assets are gitignored and stay put.
        /// </summary>
        void Commit(Workspace from, Workspace to, bool includeMeta = true)
        {
            File.Copy(from.ManifestPath, to.ManifestPath, true);
            string gitignore = Path.Combine(from.Root, ".gitignore");
            if (File.Exists(gitignore)) File.Copy(gitignore, Path.Combine(to.Root, ".gitignore"), true);
            if (!includeMeta) return;
            foreach (var meta in Directory.GetFiles(Path.Combine(from.Root, "Assets"), "*" + UniLfsMetaFile.Extension, SearchOption.AllDirectories))
            {
                string dest = Path.Combine(to.Root, meta.Substring(from.Root.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(meta, dest, true);
            }
        }

        /// <summary>
        /// Writes content and stamps a modification time no other write in this
        /// test used. The state cache keys its hashes on (mtime, size), so two
        /// writes landing in the same filesystem timestamp tick would be read
        /// back as the same content.
        /// </summary>
        void WriteAsset(Workspace ws, string projectRelativePath, string content)
        {
            string abs = ws.Abs(projectRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.WriteAllText(abs, content, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(abs, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_writes));
        }

        static string ReadAsset(Workspace ws, string projectRelativePath)
        {
            return File.ReadAllText(ws.Abs(projectRelativePath), Encoding.UTF8);
        }

        string ManifestHash(Workspace ws, string projectRelativePath)
        {
            var entry = UniLfsManifest.Load(ws.ManifestPath).Find(projectRelativePath);
            Assert.NotNull(entry, projectRelativePath + " is not in " + ws.Name + "'s manifest");
            return entry.hash;
        }

        UniLfsOpResult Track(Workspace ws, params string[] projectRelativePaths)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.TrackAsync(projectRelativePaths, null, _deadline.Token).GetAwaiter().GetResult();
        }

        UniLfsOpResult Push(Workspace ws)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.PushAsync((IProgress<UniLfsProgress>)null, _deadline.Token).GetAwaiter().GetResult();
        }

        UniLfsOpResult Pull(Workspace ws, bool restoreModified = false)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.PullAsync(restoreModified, null, _deadline.Token).GetAwaiter().GetResult();
        }

        UniLfsOpResult Verify(Workspace ws)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.VerifyRemoteAsync(null, _deadline.Token).GetAwaiter().GetResult();
        }

        UniLfsFileState State(Workspace ws, string projectRelativePath)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
            {
                var statuses = UniLfsCore.StatusAsync((IProgress<UniLfsProgress>)null, _deadline.Token)
                    .GetAwaiter().GetResult();
                var entry = statuses.Find(s => s.File.path == projectRelativePath);
                Assert.NotNull(entry, projectRelativePath + " is not tracked in " + ws.Name);
                return entry.State;
            }
        }

        static void AssertNoErrors(UniLfsOpResult result)
        {
            if (result.Errors.Count > 0)
                Assert.Fail("unexpected errors:\n  " + string.Join("\n  ", result.Errors.ToArray()));
        }
    }
}
