using System;
using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// The pattern file is the one part of UniLFS a person writes by hand, so
    /// what matters here is that every line does what someone who knows
    /// gitignore would expect — and that no line, however written, can reach a
    /// .meta file or anything under Library/.
    /// </summary>
    public class TrackPatternsTests
    {
        static UniLfsTrackPatterns Parse(params string[] lines)
        {
            return UniLfsTrackPatterns.Parse(string.Join("\n", lines));
        }

        [Test]
        public void Parse_IgnoresBlankLinesCommentsAndSurroundingSpace()
        {
            var patterns = Parse("", "# a comment", "   ", "  *.psd  ", "#*.mp4");
            Assert.AreEqual(1, patterns.Count);
            CollectionAssert.IsEmpty(patterns.Errors);
            Assert.IsTrue(patterns.Matches("Assets/Art/hero.psd"));
            Assert.IsFalse(patterns.Matches("Assets/Art/hero.mp4"));
        }

        [Test]
        public void Parse_ReadsCrlfFiles()
        {
            var patterns = UniLfsTrackPatterns.Parse("*.psd\r\n*.mp4\r\n");
            Assert.AreEqual(2, patterns.Count);
            Assert.IsTrue(patterns.Matches("Assets/a.mp4"));
        }

        [Test]
        public void Load_MissingFileIsEmptyRatherThanAnError()
        {
            var patterns = UniLfsTrackPatterns.Load(Path.Combine(Path.GetTempPath(), "unilfs-nope-" + Guid.NewGuid().ToString("N")));
            Assert.IsTrue(patterns.IsEmpty);
            CollectionAssert.IsEmpty(patterns.Errors);
            Assert.IsFalse(patterns.Matches("Assets/a.psd"));
        }

        [Test]
        public void NoSlash_MatchesTheFileNameAtAnyDepth()
        {
            var patterns = Parse("*.psd");
            Assert.IsTrue(patterns.Matches("hero.psd"));
            Assert.IsTrue(patterns.Matches("Assets/Art/Deep/hero.psd"));
            Assert.IsFalse(patterns.Matches("Assets/Art/hero.png"));
            Assert.IsFalse(patterns.Matches("Assets/Art/psd/hero.png"), "the folder name is not the file name");
        }

        [Test]
        public void WithSlash_IsAnchoredAtTheProjectRoot()
        {
            var patterns = Parse("Assets/Movies/*.mp4");
            Assert.IsTrue(patterns.Matches("Assets/Movies/intro.mp4"));
            Assert.IsFalse(patterns.Matches("Assets/Movies/Sub/intro.mp4"), "* does not cross a folder boundary");
            Assert.IsFalse(patterns.Matches("Packages/Assets/Movies/intro.mp4"));
        }

        [Test]
        public void LeadingSlash_IsTheSameAsWritingItWithout()
        {
            Assert.IsTrue(Parse("/Assets/Movies/intro.mp4").Matches("Assets/Movies/intro.mp4"));
        }

        [Test]
        public void TrailingSlash_TakesEverythingUnderTheFolder()
        {
            var patterns = Parse("Assets/Movies/");
            Assert.IsTrue(patterns.Matches("Assets/Movies/intro.mp4"));
            Assert.IsTrue(patterns.Matches("Assets/Movies/Sub/Deep/outro.wav"));
            Assert.IsFalse(patterns.Matches("Assets/Sounds/outro.wav"));
        }

        [Test]
        public void DoubleStar_CrossesFoldersAndMatchesNoneAtAll()
        {
            var patterns = Parse("Assets/**/*.wav");
            Assert.IsTrue(patterns.Matches("Assets/Sounds/a.wav"));
            Assert.IsTrue(patterns.Matches("Assets/Sounds/Deep/Deeper/a.wav"));
            Assert.IsTrue(patterns.Matches("Assets/a.wav"), "**/ also stands for no folder at all");
            Assert.IsFalse(patterns.Matches("Packages/Sounds/a.wav"));
        }

        [Test]
        public void QuestionMark_MatchesOneCharacterInsideOneName()
        {
            var patterns = Parse("Assets/take?.mp4");
            Assert.IsTrue(patterns.Matches("Assets/take1.mp4"));
            Assert.IsFalse(patterns.Matches("Assets/take12.mp4"));
            Assert.IsFalse(patterns.Matches("Assets/take/.mp4"));
        }

        [Test]
        public void Negation_ExcludesAndTheLastMatchingLineWins()
        {
            var patterns = Parse("*.png", "!Assets/UI/*.png", "Assets/UI/keep.png");
            Assert.IsTrue(patterns.Matches("Assets/Art/a.png"));
            Assert.IsFalse(patterns.Matches("Assets/UI/button.png"));
            Assert.IsTrue(patterns.Matches("Assets/UI/keep.png"), "a later line re-includes what an earlier one excluded");
        }

        [Test]
        public void Matching_IgnoresCase()
        {
            Assert.IsTrue(Parse("*.PSD").Matches("Assets/Art/hero.psd"));
            Assert.IsTrue(Parse("assets/movies/").Matches("Assets/Movies/intro.mp4"));
        }

        /// <summary>
        /// The one thing a pattern must not be able to say. It is rejected at
        /// parse time rather than filtered later, so the file tells you the
        /// line is wrong instead of the line quietly matching nothing.
        /// </summary>
        [Test]
        public void ParentDirectorySegments_AreRejectedWithAnError()
        {
            var patterns = Parse("../outside/*.psd");
            Assert.IsTrue(patterns.IsEmpty);
            Assert.AreEqual(1, patterns.Errors.Count);
            StringAssert.Contains("line 1", patterns.Errors[0]);
        }

        [Test]
        public void EmptyPatternAfterNegation_IsAnError()
        {
            var patterns = Parse("!");
            Assert.IsTrue(patterns.IsEmpty);
            Assert.AreEqual(1, patterns.Errors.Count);
        }

        /// <summary>
        /// Whatever the file says, the paths Track itself refuses stay out of
        /// reach: a "*" line is the shortest way to ask for all of them.
        /// </summary>
        [Test]
        public void NothingUntrackableCanBeMatched()
        {
            var patterns = Parse("**", "*", "*.meta", "Library/", "unilfs.manifest.json");
            Assert.IsFalse(patterns.Matches("Assets/Art/hero.psd.meta"));
            Assert.IsFalse(patterns.Matches("Library/ArtifactDB"));
            Assert.IsFalse(patterns.Matches("Temp/x.bin"));
            Assert.IsFalse(patterns.Matches("UserSettings/UniLFS.json"));
            Assert.IsFalse(patterns.Matches(UniLfsPaths.ManifestFileName));
            Assert.IsFalse(patterns.Matches(UniLfsPaths.StagedFileName));
            Assert.IsFalse(patterns.Matches(UniLfsPaths.TrackFileName));
            Assert.IsTrue(patterns.Matches("Assets/Art/hero.psd"), "the same lines still match an ordinary asset");
        }

        [Test]
        public void CreateIfMissing_WritesTheTemplateOnceAndMatchesNothing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, UniLfsPaths.TrackFileName);
                Assert.IsTrue(UniLfsTrackPatterns.CreateIfMissing(path));
                Assert.IsFalse(UniLfsTrackPatterns.CreateIfMissing(path), "an existing file is never overwritten");

                var patterns = UniLfsTrackPatterns.Load(path);
                Assert.IsTrue(patterns.IsEmpty, "creating the file must not start tracking anything");
                CollectionAssert.IsEmpty(patterns.Errors);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// The project sweep behind Track Matching, against a throwaway project
    /// root: what it walks into, and what it leaves for Track to skip.
    /// </summary>
    public class TrackPatternScanTests
    {
        string _root;
        IDisposable _scope;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _scope = UniLfsPaths.OverrideProjectRoot(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_scope != null) _scope.Dispose();
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        void WriteFile(string projectRelative)
        {
            string abs = Path.Combine(_root, projectRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.WriteAllText(abs, "x");
        }

        [Test]
        public void FindPatternMatches_TakesMatchesAndSkipsWhatCannotBeTracked()
        {
            WriteFile("Assets/Art/hero.psd");
            WriteFile("Assets/Art/hero.psd.meta");
            WriteFile("Assets/Art/notes.txt");
            WriteFile("Assets/UI/button.psd");
            WriteFile("Library/cache/stale.psd");
            WriteFile("Temp/scratch.psd");
            WriteFile(".hidden/secret.psd");

            int alreadyTracked;
            var matches = UniLfsCore.FindPatternMatches(
                UniLfsTrackPatterns.Parse("*.psd\n!Assets/UI/*.psd"), out alreadyTracked);

            CollectionAssert.AreEqual(new[] { "Assets/Art/hero.psd" }, matches);
            Assert.AreEqual(0, alreadyTracked);
        }

        [Test]
        public void FindPatternMatches_LeavesAlreadyStagedFilesToBeCounted()
        {
            WriteFile("Assets/Art/one.psd");
            WriteFile("Assets/Art/two.psd");
            var staged = UniLfsStagedPaths.Load(UniLfsPaths.StagedPath);
            staged.Add("Assets/Art/one.psd");
            staged.Save(UniLfsPaths.StagedPath);

            int alreadyTracked;
            var matches = UniLfsCore.FindPatternMatches(UniLfsTrackPatterns.Parse("*.psd"), out alreadyTracked);

            CollectionAssert.AreEqual(new[] { "Assets/Art/two.psd" }, matches);
            Assert.AreEqual(1, alreadyTracked, "an already staged file is reported, not re-tracked");
        }

        [Test]
        public void FindPatternMatches_WithNoPatternsWalksNothing()
        {
            WriteFile("Assets/Art/hero.psd");
            int alreadyTracked;
            CollectionAssert.IsEmpty(UniLfsCore.FindPatternMatches(UniLfsTrackPatterns.Parse(""), out alreadyTracked));
            Assert.AreEqual(0, alreadyTracked);
        }
    }
}
