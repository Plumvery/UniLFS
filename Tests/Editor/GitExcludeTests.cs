using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// Finding and writing <c>.git/info/exclude</c>, which is what hides a
    /// staged file from git before its first Push.
    ///
    /// The lookup is the part worth testing: <c>.git</c> is a directory only in
    /// the ordinary case, a Unity project is often not the repository root, and
    /// a linked worktree keeps its exclude file somewhere else entirely. Every
    /// one of those getting it wrong looks the same from the outside — a
    /// multi-gigabyte file that git is willing to commit.
    /// </summary>
    public class GitExcludeTests
    {
        const string Asset = "Assets/Art/big.bin";

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

        [Test]
        public void ProjectAtTheRepositoryRoot_WritesInsideDotGit()
        {
            string project = NewDir("project");
            Directory.CreateDirectory(Path.Combine(project, ".git"));

            Assert.IsTrue(UniLfsGitExclude.Update(project, new[] { Asset }));

            string expected = Path.Combine(project, ".git", "info", "exclude");
            Assert.IsTrue(File.Exists(expected));
            CollectionAssert.Contains(UniLfsGitExclude.ReadManagedLines(project), "/" + Asset);
            StringAssert.Contains(UniLfsGitExclude.BeginMarker, File.ReadAllText(expected));
        }

        /// <summary>
        /// Exclude patterns are relative to the repository root, unlike the
        /// .gitignore UniLFS writes into the project root. A Unity project one
        /// directory down would otherwise ignore a path that does not exist.
        /// </summary>
        [Test]
        public void ProjectBelowTheRepositoryRoot_AnchorsPathsToTheRepository()
        {
            string repo = NewDir("repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            string project = Path.Combine(repo, "UnityProject");
            Directory.CreateDirectory(project);

            Assert.IsTrue(UniLfsGitExclude.Update(project, new[] { Asset }));

            CollectionAssert.Contains(UniLfsGitExclude.ReadManagedLines(project), "/UnityProject/" + Asset);
            Assert.IsTrue(File.Exists(Path.Combine(repo, ".git", "info", "exclude")));
        }

        /// <summary>
        /// In a linked worktree or a submodule, <c>.git</c> is a file pointing
        /// at the real git directory.
        /// </summary>
        [Test]
        public void DotGitAsAPointerFile_IsFollowed()
        {
            string project = NewDir("worktree");
            string gitDir = NewDir("real-git-dir");
            File.WriteAllText(Path.Combine(project, ".git"), "gitdir: " + gitDir + "\n", new UTF8Encoding(false));

            Assert.IsTrue(UniLfsGitExclude.Update(project, new[] { Asset }));

            Assert.IsTrue(File.Exists(Path.Combine(gitDir, "info", "exclude")));
            CollectionAssert.Contains(UniLfsGitExclude.ReadManagedLines(project), "/" + Asset);
        }

        /// <summary>
        /// A linked worktree has its own git directory but shares info/exclude
        /// with the main one, which is the only place git reads it from.
        /// </summary>
        [Test]
        public void WorktreeWithACommonDir_WritesWhereGitActuallyReads()
        {
            string project = NewDir("worktree");
            string mainGitDir = NewDir("main-git-dir");
            string worktreeGitDir = Path.Combine(mainGitDir, "worktrees", "wt");
            Directory.CreateDirectory(worktreeGitDir);
            File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(project, ".git"), "gitdir: " + worktreeGitDir + "\n", new UTF8Encoding(false));

            Assert.IsTrue(UniLfsGitExclude.Update(project, new[] { Asset }));

            Assert.IsTrue(File.Exists(Path.Combine(mainGitDir, "info", "exclude")),
                "the block belongs in the shared git directory, not the worktree's own");
            Assert.IsFalse(File.Exists(Path.Combine(worktreeGitDir, "info", "exclude")));
        }

        [Test]
        public void NoRepository_IsReportedRatherThanFailing()
        {
            string project = NewDir("loose-project");

            Assert.IsNull(UniLfsGitExclude.Find(project));
            Assert.IsFalse(UniLfsGitExclude.Update(project, new[] { Asset }),
                "the caller has to be able to warn that nothing is hiding this file");
            CollectionAssert.IsEmpty(UniLfsGitExclude.ReadManagedLines(project));
        }

        [Test]
        public void Update_ReplacesTheBlockAndKeepsTheRestOfTheFile()
        {
            string project = NewDir("project");
            string info = Path.Combine(project, ".git", "info");
            Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, "exclude"), "# git's own header\n*.tmp\n", new UTF8Encoding(false));

            UniLfsGitExclude.Update(project, new[] { Asset });
            UniLfsGitExclude.Update(project, new[] { "Assets/Art/other.bin" });

            string text = File.ReadAllText(Path.Combine(info, "exclude"));
            StringAssert.Contains("# git's own header", text);
            StringAssert.Contains("*.tmp", text);
            StringAssert.Contains("/Assets/Art/other.bin", text);
            StringAssert.DoesNotContain("/" + Asset, text, "a pushed path must lose its local line");
        }

        /// <summary>
        /// Nothing staged and no block already there: writing a file inside
        /// .git to say nothing is worse than leaving it alone.
        /// </summary>
        [Test]
        public void NothingStagedAndNoBlock_LeavesTheFileAlone()
        {
            string project = NewDir("project");
            Directory.CreateDirectory(Path.Combine(project, ".git"));

            Assert.IsTrue(UniLfsGitExclude.Update(project, new string[0]));
            Assert.IsFalse(File.Exists(Path.Combine(project, ".git", "info", "exclude")));
        }

        string NewDir(string name)
        {
            string path = Path.Combine(_dir, name);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
