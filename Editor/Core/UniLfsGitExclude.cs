using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// Hides staged-but-not-pushed files from git through
    /// <c>.git/info/exclude</c> — the per-checkout ignore list git itself never
    /// tracks.
    ///
    /// A tracked file has to be invisible to git from the moment someone tracks
    /// it, or the window before the first Push is one <c>git add -A</c> away
    /// from putting a multi-gigabyte file in the history. The committed
    /// <c>.gitignore</c> cannot carry that: staging is local, and deriving a
    /// committed file from a local one makes it differ per machine. This file
    /// is local on both ends, which is exactly the shape of the fact it states.
    ///
    /// Everything here is best-effort. A project that is not in a git checkout
    /// yet, or a layout the lookup below does not understand, must not stop
    /// anyone from tracking a file — <see cref="UniLfsCore"/> reports staged
    /// paths that nothing is ignoring instead.
    /// </summary>
    public static class UniLfsGitExclude
    {
        public const string BeginMarker = "# >>> UniLFS staged files - local to this checkout, never committed >>>";
        public const string EndMarker = "# <<< UniLFS staged files <<<";

        /// <summary>Where the exclude file is, and what paths in it are relative to.</summary>
        public class Location
        {
            /// <summary>Absolute path of <c>info/exclude</c>, which may not exist yet.</summary>
            public string ExcludeFilePath;
            /// <summary>
            /// The project root relative to the repository root, with a trailing
            /// slash, or empty when they are the same directory. Exclude
            /// patterns are anchored to the repository root, unlike the
            /// .gitignore UniLFS writes into the project root.
            /// </summary>
            public string PathPrefix;
        }

        /// <summary>
        /// Finds the exclude file for the checkout containing
        /// <paramref name="projectRootAbsolute"/>, or null when there is none.
        /// </summary>
        public static Location Find(string projectRootAbsolute)
        {
            try
            {
                string projectRoot = UniLfsPaths.Normalize(Path.GetFullPath(projectRootAbsolute));
                string repoRoot = null, gitDir = null;
                for (var dir = new DirectoryInfo(projectRoot); dir != null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(candidate))
                    {
                        repoRoot = UniLfsPaths.Normalize(dir.FullName);
                        gitDir = candidate;
                        break;
                    }
                    if (File.Exists(candidate))
                    {
                        // A linked worktree or a submodule: ".git" is a file
                        // holding "gitdir: <path>", which may be relative to the
                        // directory the file is in.
                        string target = ReadGitDirPointer(candidate);
                        if (target == null) return null;
                        repoRoot = UniLfsPaths.Normalize(dir.FullName);
                        gitDir = Path.IsPathRooted(target) ? target : Path.Combine(dir.FullName, target);
                        break;
                    }
                }
                if (gitDir == null || !Directory.Exists(gitDir)) return null;

                // A linked worktree has its own git directory but shares
                // info/exclude with the main one, which is where git looks for
                // it. Without this, the block would be written somewhere git
                // never reads.
                string commonDir = gitDir;
                string commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile))
                {
                    string common = File.ReadAllText(commonFile, Encoding.UTF8).Trim();
                    if (common.Length > 0)
                        commonDir = Path.IsPathRooted(common) ? common : Path.Combine(gitDir, common);
                }

                string prefix = "";
                if (!string.Equals(projectRoot, repoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (!projectRoot.StartsWith(repoRoot + "/", StringComparison.OrdinalIgnoreCase)) return null;
                    prefix = projectRoot.Substring(repoRoot.Length + 1) + "/";
                }

                return new Location
                {
                    ExcludeFilePath = UniLfsPaths.Normalize(Path.Combine(Path.GetFullPath(commonDir), "info", "exclude")),
                    PathPrefix = prefix,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Rewrites the managed block from <paramref name="stagedProjectRelativePaths"/>.
        /// Returns false when there is no checkout to write to, or the write
        /// failed — the caller reports that rather than failing the operation.
        /// </summary>
        public static bool Update(string projectRootAbsolute, IEnumerable<string> stagedProjectRelativePaths)
        {
            var location = Find(projectRootAbsolute);
            if (location == null) return false;
            var paths = stagedProjectRelativePaths.ToList();
            try
            {
                // Nothing staged and no block already there: leave the file
                // alone rather than creating one inside .git to say nothing.
                if (paths.Count == 0 &&
                    UniLfsIgnoreBlock.ReadManagedLines(location.ExcludeFilePath, BeginMarker, EndMarker).Count == 0)
                    return true;

                var block = new List<string> { BeginMarker };
                block.AddRange(paths
                    .Select(p => UniLfsIgnoreBlock.Escape(location.PathPrefix + p))
                    .Distinct()
                    .OrderBy(p => p, StringComparer.Ordinal));
                block.Add(EndMarker);
                UniLfsIgnoreBlock.Write(location.ExcludeFilePath, BeginMarker, EndMarker, block);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static List<string> ReadManagedLines(string projectRootAbsolute)
        {
            var location = Find(projectRootAbsolute);
            if (location == null) return new List<string>();
            return UniLfsIgnoreBlock.ReadManagedLines(location.ExcludeFilePath, BeginMarker, EndMarker);
        }

        static string ReadGitDirPointer(string dotGitFile)
        {
            try
            {
                foreach (var line in File.ReadAllLines(dotGitFile, Encoding.UTF8))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("gitdir:", StringComparison.Ordinal)) continue;
                    var target = trimmed.Substring("gitdir:".Length).Trim();
                    return target.Length == 0 ? null : target;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
