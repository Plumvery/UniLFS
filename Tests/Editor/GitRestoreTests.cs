using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// Getting a committed .meta back from git, which is the only reason UniLFS
    /// runs git at all. The rules that matter are the ones that decide whether a
    /// fresh clone keeps its import settings or silently loses them: a pathspec
    /// git does not know must not cost every other file its restore, a file
    /// already on disk must never be overwritten, and a project outside a
    /// checkout must fall back rather than throw at editor start.
    ///
    /// These need the git command line. Where there is none the suite reports
    /// itself as ignored rather than failing — the code under test treats a
    /// missing git as "restore nothing", so there is nothing to assert.
    /// </summary>
    public class GitRestoreTests
    {
        const string MetaWithImportSettings =
            "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nTextureImporter:\n  mipmaps: 0\n  sRGBTexture: 0\n";

        string _repo;

        [SetUp]
        public void SetUp()
        {
            _repo = Path.Combine(Path.GetTempPath(), "unilfs-git-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_repo);
            if (!Git("init"))
                Assert.Ignore("git is not available on this machine, so there is nothing to restore from.");
            Git("config user.email unilfs@example.com");
            Git("config user.name UniLFS");
            // Deterministic bytes on the way back out, whatever this machine's
            // global config says about line endings.
            Git("config core.autocrlf false");
        }

        [TearDown]
        public void TearDown()
        {
            try { DeleteTree(_repo); } catch (Exception) { }
        }

        [Test]
        public void ADiscardedMetaComesBackWithItsImportSettings()
        {
            Commit("Assets/big.psd.meta", MetaWithImportSettings);
            File.Delete(Path.Combine(_repo, "Assets/big.psd.meta"));

            var restored = UniLfsGitRestore.Restore(_repo, new[] { "Assets/big.psd.meta" });

            CollectionAssert.AreEqual(new[] { "Assets/big.psd.meta" }, restored);
            Assert.AreEqual(MetaWithImportSettings, Read("Assets/big.psd.meta"),
                "the point of asking git rather than rebuilding: the import settings come back too");
        }

        [Test]
        public void APathGitDoesNotKnowDoesNotCostTheOthersTheirRestore()
        {
            // One newly tracked asset whose .meta has never been committed is
            // enough to make a single `git checkout --` call fail outright, and
            // every other file would lose its import settings with it.
            Commit("Assets/committed.psd.meta", MetaWithImportSettings);
            File.Delete(Path.Combine(_repo, "Assets/committed.psd.meta"));

            var restored = UniLfsGitRestore.Restore(_repo, new[]
            {
                "Assets/never-committed.psd.meta",
                "Assets/committed.psd.meta",
            });

            CollectionAssert.AreEqual(new[] { "Assets/committed.psd.meta" }, restored);
            Assert.AreEqual(MetaWithImportSettings, Read("Assets/committed.psd.meta"));
        }

        [Test]
        public void AFileStillOnDiskIsLeftAlone()
        {
            Commit("Assets/edited.psd.meta", MetaWithImportSettings);
            const string LocalEdit = "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nlocal: edit\n";
            File.WriteAllText(Path.Combine(_repo, "Assets/edited.psd.meta"), LocalEdit, new UTF8Encoding(false));

            var restored = UniLfsGitRestore.Restore(_repo, new[] { "Assets/edited.psd.meta" });

            CollectionAssert.IsEmpty(restored);
            Assert.AreEqual(LocalEdit, Read("Assets/edited.psd.meta"),
                "git checkout -- overwrites, so a path that still has a file must never be handed to it");
        }

        [Test]
        public void APathWithASpaceIsRestored()
        {
            Commit("Assets/Big Texture.psd.meta", MetaWithImportSettings);
            File.Delete(Path.Combine(_repo, "Assets/Big Texture.psd.meta"));

            var restored = UniLfsGitRestore.Restore(_repo, new[] { "Assets/Big Texture.psd.meta" });

            CollectionAssert.AreEqual(new[] { "Assets/Big Texture.psd.meta" }, restored);
        }

        [Test]
        public void ANonAsciiPathIsRestored()
        {
            // git escapes non-ASCII paths in its output unless told otherwise,
            // and an escaped path matches nothing on the way back.
            Commit("Assets/背景.psd.meta", MetaWithImportSettings);
            File.Delete(Path.Combine(_repo, "Assets/背景.psd.meta"));

            var restored = UniLfsGitRestore.Restore(_repo, new[] { "Assets/背景.psd.meta" });

            CollectionAssert.AreEqual(new[] { "Assets/背景.psd.meta" }, restored);
            Assert.AreEqual(MetaWithImportSettings, Read("Assets/背景.psd.meta"));
        }

        [Test]
        public void APathThatCannotBeQuotedIsRefused()
        {
            // Never created on disk (Windows forbids it outright); what matters
            // is that it is dropped before it reaches a command line rather than
            // escaped into one.
            var restored = UniLfsGitRestore.Restore(_repo, new[] { "Assets/od\"d.psd.meta", "--upload-pack=evil" });
            CollectionAssert.IsEmpty(restored);
        }

        [Test]
        public void OutsideAGitCheckoutNothingIsRestoredAndNothingThrows()
        {
            var loose = Path.Combine(Path.GetTempPath(), "unilfs-not-a-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(loose);
            try
            {
                Assert.DoesNotThrow(() =>
                {
                    var restored = UniLfsGitRestore.Restore(loose, new[] { "Assets/big.psd.meta" });
                    CollectionAssert.IsEmpty(restored);
                });
            }
            finally
            {
                DeleteTree(loose);
            }
        }

        [Test]
        public void AMissingDirectoryRestoresNothing()
        {
            var gone = Path.Combine(Path.GetTempPath(), "unilfs-gone-" + Guid.NewGuid().ToString("N"));
            CollectionAssert.IsEmpty(UniLfsGitRestore.Restore(gone, new[] { "Assets/big.psd.meta" }));
            CollectionAssert.IsEmpty(UniLfsGitRestore.Restore(_repo, null));
        }

        [Test]
        public void TheGuardPrefersGitsMetaToOneRebuiltFromTheManifest()
        {
            // The fresh-clone shape: the .meta is committed, the asset is
            // gitignored and absent, and Unity has already discarded the .meta
            // by the time any managed code runs.
            Commit("Assets/big.psd.meta", MetaWithImportSettings);
            WriteManifest("Assets/big.psd", "0123456789abcdef0123456789abcdef");
            File.Delete(Path.Combine(_repo, "Assets/big.psd.meta"));

            UniLfsMetaGuard.GuardReport report;
            using (UniLfsPaths.OverrideProjectRoot(_repo))
                report = UniLfsMetaGuard.Run();

            Assert.AreEqual(1, report.MetaFilesRestoredFromGit);
            Assert.AreEqual(0, report.MetaFilesRebuilt,
                "git had the file, so nothing had to be rebuilt without its import settings");
            Assert.AreEqual(MetaWithImportSettings, Read("Assets/big.psd.meta"),
                "this is the whole issue: a fresh clone keeps its import settings");
            Assert.AreEqual(1, report.PlaceholdersWritten,
                "the placeholder still has to stop the next refresh orphaning the .meta again");
            CollectionAssert.IsEmpty(report.GuidDrift);
        }

        [Test]
        public void WithNothingCommittedTheGuardStillRebuildsFromTheManifest()
        {
            // A file tracked and pushed but whose .meta never reached git: the
            // behaviour that shipped before the restore existed, and the one a
            // machine without git falls back to.
            WriteManifest("Assets/fresh.psd", "89abcdef0123456789abcdef01234567");

            UniLfsMetaGuard.GuardReport report;
            using (UniLfsPaths.OverrideProjectRoot(_repo))
                report = UniLfsMetaGuard.Run();

            Assert.AreEqual(0, report.MetaFilesRestoredFromGit);
            Assert.AreEqual(1, report.MetaFilesRebuilt);
            Assert.AreEqual("89abcdef0123456789abcdef01234567",
                UniLfsMetaFile.ReadGuid(Path.Combine(_repo, "Assets/fresh.psd.meta")),
                "the GUID is what saves the references, settings or no settings");
        }

        // ---------- helpers ----------

        void WriteManifest(string assetPath, string guid)
        {
            var manifest = new UniLfsManifest();
            var entry = manifest.Upsert(assetPath, "0000000000000000000000000000000000000000000000000000000000000000", 4096);
            entry.guid = guid;
            manifest.Save(Path.Combine(_repo, UniLfsPaths.ManifestFileName));
        }

        void Commit(string relativePath, string content)
        {
            var abs = Path.Combine(_repo, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.WriteAllText(abs, content, new UTF8Encoding(false));
            Assert.IsTrue(Git("add -A"), "git add failed");
            Assert.IsTrue(Git("commit -m add-" + Guid.NewGuid().ToString("N")), "git commit failed");
        }

        string Read(string relativePath)
        {
            return File.ReadAllText(Path.Combine(_repo, relativePath), Encoding.UTF8).Replace("\r\n", "\n");
        }

        bool Git(string arguments)
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = _repo,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                using (var process = Process.Start(info))
                {
                    if (process == null) return false;
                    process.OutputDataReceived += (s, e) => { };
                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void DeleteTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            // git marks objects read-only, which Directory.Delete refuses.
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }
}
