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
    /// Three directories: one per clone of a project, plus one standing in for
    /// the bucket both of them push to, with <see cref="FakeRemoteFileServer"/>
    /// in front of it.
    ///
    /// The unit tests elsewhere pin the pieces — the three-way rules, the
    /// baseline record, the signer, the manifest. What none of them can reach is
    /// the thing UniLFS is for: whether content one person pushes actually
    /// arrives for the next person, and whether that person's own work survives
    /// the trip. Every rule about Outdated, Conflicted and rolled-back manifests
    /// exists for a situation with two machines in it, and a single project
    /// directory cannot produce one.
    ///
    /// "git" here is <see cref="Commit"/> and <see cref="MergeManifests"/>: they
    /// carry the manifest, the managed .gitignore and the .meta files between the
    /// two clones, and deliberately leave the tracked assets behind, because
    /// those are gitignored — which is the whole reason Pull exists.
    ///
    /// Everything a test says about the fixture is deliberately said through the
    /// helpers below, so a test reads as the situation it is describing rather
    /// than as path arithmetic.
    /// </summary>
    public abstract class TwoCloneFixture
    {
        protected const string Bucket = "unilfs-test";
        protected const string Prefix = "unilfs";
        protected const string AccessKeyId = "UNILFSTESTACCESSKEY";
        protected const string SecretAccessKey = "unilfs-test-secret-access-key-0123456789";
        protected const string Asset = "Assets/Art/big.bin";
        protected const string SecondAsset = "Assets/Art/duplicate.bin";
        protected const string AssetGuid = "0123456789abcdef0123456789abcdef";
        /// <summary>
        /// The GUID a clone's own Unity would mint for the same asset: what
        /// every "the two sides disagree about identity" case is built from.
        /// </summary>
        protected const string OtherGuid = "fedcba9876543210fedcba9876543210";

        protected string _tempRoot;
        protected FakeRemoteFileServer _server;
        protected Workspace _a;
        protected Workspace _b;
        protected CancellationTokenSource _deadline;
        Dictionary<string, string> _savedEnvironment;
        int _writes;

        /// <summary>One clone: a project root with its own manifest, Library/ and settings.</summary>
        protected class Workspace
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

            public string GitIgnorePath
            {
                get { return Path.Combine(Root, ".gitignore"); }
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

        Workspace CreateWorkspace(string name)
        {
            string root = Path.Combine(_tempRoot, name);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            // A clone is a git checkout, and UniLFS hides staged files through
            // .git/info/exclude — without a .git directory the tests would
            // exercise only the "no checkout to write to" fallback.
            Directory.CreateDirectory(Path.Combine(root, ".git"));
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
        protected void SyncBothClones(string content)
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

        // ----- what git carries -----

        /// <summary>
        /// What git carries between the clones: the manifest, the managed
        /// .gitignore and (unless a test is reproducing a clone that lost them)
        /// the .meta files. The tracked assets are gitignored and stay put.
        ///
        /// One direction only, and it overwrites: this is a fast-forward, or a
        /// merge someone resolved in favour of <paramref name="from"/>.
        /// </summary>
        protected void Commit(Workspace from, Workspace to, bool includeMeta = true)
        {
            // A project where nothing has been pushed yet has no manifest at
            // all - Track does not create one - and git cannot carry a file
            // that does not exist.
            if (File.Exists(from.ManifestPath)) File.Copy(from.ManifestPath, to.ManifestPath, true);
            else if (File.Exists(to.ManifestPath)) File.Delete(to.ManifestPath);
            if (File.Exists(from.GitIgnorePath)) File.Copy(from.GitIgnorePath, to.GitIgnorePath, true);
            if (includeMeta) CopyMetaFiles(from, to);
        }

        /// <summary>
        /// Both clones added something the other does not have, and git merged
        /// the two manifests. The file is one entry per line precisely so this
        /// merge is the trivial one — which also means a path both sides touched
        /// is a conflict git would stop at, so this refuses it rather than
        /// picking a winner behind the test's back.
        /// </summary>
        protected void MergeManifests(Workspace one, Workspace other)
        {
            var merged = UniLfsManifest.Load(one.ManifestPath);
            foreach (var theirs in UniLfsManifest.Load(other.ManifestPath).files)
            {
                var mine = merged.Find(theirs.path);
                if (mine == null)
                {
                    merged.Upsert(theirs.path, theirs.hash, theirs.size).guid = theirs.guid;
                    continue;
                }
                Assert.AreEqual(mine.hash, theirs.hash,
                    "both clones changed " + theirs.path + "; git stops at that line and so does this fixture");
            }
            foreach (var ws in new[] { one, other })
            {
                merged.Save(ws.ManifestPath);
                using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                    UniLfsGitIgnore.Update(UniLfsPaths.GitIgnorePath, merged.files.Select(f => f.path));
            }
            CopyMetaFiles(one, other);
            CopyMetaFiles(other, one);
        }

        static void CopyMetaFiles(Workspace from, Workspace to)
        {
            foreach (var meta in Directory.GetFiles(Path.Combine(from.Root, "Assets"), "*" + UniLfsMetaFile.Extension, SearchOption.AllDirectories))
            {
                string dest = Path.Combine(to.Root, meta.Substring(from.Root.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(meta, dest, true);
            }
        }

        // ----- what a person does to the files -----

        /// <summary>
        /// Writes content and stamps a modification time no other write in this
        /// test used. The state cache keys its hashes on (mtime, size), so two
        /// writes landing in the same filesystem timestamp tick would be read
        /// back as the same content.
        /// </summary>
        protected void WriteAsset(Workspace ws, string projectRelativePath, string content)
        {
            WriteAssetBytes(ws, projectRelativePath, new UTF8Encoding(false).GetBytes(content));
        }

        protected void WriteAssetBytes(Workspace ws, string projectRelativePath, byte[] content)
        {
            string abs = ws.Abs(projectRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.WriteAllBytes(abs, content);
            File.SetLastWriteTimeUtc(abs, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_writes));
        }

        protected static string ReadAsset(Workspace ws, string projectRelativePath)
        {
            return File.ReadAllText(ws.Abs(projectRelativePath), Encoding.UTF8);
        }

        protected static byte[] ReadAssetBytes(Workspace ws, string projectRelativePath)
        {
            return File.ReadAllBytes(ws.Abs(projectRelativePath));
        }

        /// <summary>Someone deleted the asset in Explorer. The manifest still names it.</summary>
        protected static void DeleteAsset(Workspace ws, string projectRelativePath)
        {
            File.Delete(ws.Abs(projectRelativePath));
        }

        /// <summary>
        /// Content that is not text and does not compress to nothing, so a
        /// transfer has to carry it in more than one buffer to get it right.
        /// Deterministic, because a test that fails on one run has to fail on the
        /// next one too.
        /// </summary>
        protected static byte[] Bytes(int length, int seed)
        {
            var bytes = new byte[length];
            var random = new Random(seed);
            random.NextBytes(bytes);
            return bytes;
        }

        // ----- .meta files -----

        protected static string MetaPath(Workspace ws, string projectRelativePath)
        {
            return UniLfsMetaFile.PathFor(ws.Abs(projectRelativePath));
        }

        /// <summary>The .meta Unity would have written next to the asset.</summary>
        protected static void WriteMeta(Workspace ws, string projectRelativePath, string guid)
        {
            File.WriteAllText(MetaPath(ws, projectRelativePath),
                "fileFormatVersion: 2\nguid: " + guid + "\n", new UTF8Encoding(false));
        }

        protected static string MetaGuid(Workspace ws, string projectRelativePath)
        {
            return UniLfsMetaFile.ReadGuid(MetaPath(ws, projectRelativePath));
        }

        protected static bool HasMeta(Workspace ws, string projectRelativePath)
        {
            return File.Exists(MetaPath(ws, projectRelativePath));
        }

        // ----- staging -----

        /// <summary>What this clone has asked to track and not pushed yet.</summary>
        protected static List<string> StagedPaths(Workspace ws)
        {
            return UniLfsStagedPaths.Load(Path.Combine(ws.Root, UniLfsPaths.StagedFileName)).paths;
        }

        /// <summary>The paths hidden from git by this checkout's own exclude file.</summary>
        protected static List<string> LocallyExcluded(Workspace ws)
        {
            return UniLfsGitExclude.ReadManagedLines(ws.Root);
        }

        /// <summary>The committed ignore block, which only ever names the manifest's paths.</summary>
        protected static List<string> CommittedIgnoreLines(Workspace ws)
        {
            return UniLfsGitIgnore.ReadManagedLines(ws.GitIgnorePath);
        }

        /// <summary>"Keep mine": the recorded half of resolving a conflict.</summary>
        protected UniLfsOpResult KeepMine(Workspace ws, params string[] projectRelativePaths)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.KeepLocal(projectRelativePaths);
        }

        // ----- the manifest -----

        /// <summary>
        /// Whether the committed record names this path — which, after the
        /// staging split, is the same question as "can anyone else see it".
        /// </summary>
        protected static bool InManifest(Workspace ws, string projectRelativePath)
        {
            return UniLfsManifest.Load(ws.ManifestPath).Find(projectRelativePath) != null;
        }

        protected static List<string> ManifestPaths(Workspace ws)
        {
            return UniLfsManifest.Load(ws.ManifestPath).files.Select(f => f.path).ToList();
        }

        protected static UniLfsManifestFile ManifestEntry(Workspace ws, string projectRelativePath)
        {
            var entry = UniLfsManifest.Load(ws.ManifestPath).Find(projectRelativePath);
            Assert.NotNull(entry, projectRelativePath + " is not in " + ws.Name + "'s manifest");
            return entry;
        }

        protected static string ManifestHash(Workspace ws, string projectRelativePath)
        {
            return ManifestEntry(ws, projectRelativePath).hash;
        }

        protected static string ManifestGuid(Workspace ws, string projectRelativePath)
        {
            return ManifestEntry(ws, projectRelativePath).guid;
        }

        // ----- the operations, each run against one clone's project root -----

        protected UniLfsOpResult Track(Workspace ws, params string[] projectRelativePaths)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.TrackAsync(projectRelativePaths, null, _deadline.Token).GetAwaiter().GetResult();
        }

        protected UniLfsOpResult Untrack(Workspace ws, params string[] projectRelativePaths)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.Untrack(projectRelativePaths);
        }

        protected UniLfsOpResult Push(Workspace ws)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.PushAsync((IProgress<UniLfsProgress>)null, _deadline.Token).GetAwaiter().GetResult();
        }

        protected UniLfsOpResult Pull(Workspace ws, bool restoreModified = false)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.PullAsync(restoreModified, null, _deadline.Token).GetAwaiter().GetResult();
        }

        protected UniLfsOpResult Verify(Workspace ws)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.VerifyRemoteAsync(null, _deadline.Token).GetAwaiter().GetResult();
        }

        /// <summary>
        /// What the guard does at editor start: restore .meta files Unity
        /// discarded and stand in for content that is not on disk. Only the
        /// report is returned — logging it is the editor's job, and a test that
        /// triggered an expected LogError would fail on it.
        /// </summary>
        protected GuardOutcome Guard(Workspace ws)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
            {
                var report = UniLfsMetaGuard.Run();
                return new GuardOutcome
                {
                    PlaceholdersWritten = report.PlaceholdersWritten,
                    MetaFilesRestored = report.MetaFilesRestored,
                    MetaMissingNoGuid = report.MetaMissingNoGuid,
                    GuidDrift = report.GuidDrift,
                    RejectedPaths = report.RejectedPaths,
                };
            }
        }

        /// <summary>
        /// What the guard reported, in a type this assembly owns: the guard's
        /// own report is internal to the package, and an internal type cannot
        /// appear in the signature of a protected member.
        /// </summary>
        public class GuardOutcome
        {
            public int PlaceholdersWritten;
            public int MetaFilesRestored;
            public List<string> MetaMissingNoGuid = new List<string>();
            /// <summary>Files whose .meta carries a different GUID than the manifest recorded.</summary>
            public List<string> GuidDrift = new List<string>();
            public List<string> RejectedPaths = new List<string>();
        }

        protected List<UniLfsStatusEntry> Statuses(Workspace ws)
        {
            using (UniLfsPaths.OverrideProjectRoot(ws.Root))
                return UniLfsCore.StatusAsync((IProgress<UniLfsProgress>)null, _deadline.Token)
                    .GetAwaiter().GetResult();
        }

        protected List<string> TrackedPaths(Workspace ws)
        {
            return Statuses(ws).Select(s => s.File.path).ToList();
        }

        protected UniLfsFileState State(Workspace ws, string projectRelativePath)
        {
            var entry = Statuses(ws).Find(s => s.File.path == projectRelativePath);
            Assert.NotNull(entry, projectRelativePath + " is not tracked in " + ws.Name);
            return entry.State;
        }

        protected static void AssertNoErrors(UniLfsOpResult result)
        {
            if (result.Errors.Count > 0)
                Assert.Fail("unexpected errors:\n  " + string.Join("\n  ", result.Errors.ToArray()));
        }
    }
}
