using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// Puts committed files back from git, for the one case where git holds a
    /// better answer than UniLFS can reconstruct: a <c>.meta</c> Unity discarded
    /// on the first launch of a fresh clone.
    ///
    /// <see cref="UniLfsMetaGuard"/> can rebuild such a file from the GUID in the
    /// manifest, which saves every reference to the asset — but a rebuilt .meta
    /// carries no import settings, and the one in git does. Restoring it is what
    /// the guard's own warning has always told people to do by hand
    /// (<c>git checkout -- &lt;path&gt;.meta</c>); this does it for them.
    ///
    /// This is the only place UniLFS runs git. Everything else it needs from a
    /// checkout it reads or writes as a file, because the shape of those facts is
    /// a file (<see cref="UniLfsGitExclude"/>). Reading a blob out of the object
    /// store is not: it means the index format, loose objects and packfiles, to
    /// re-implement one command every machine with a git checkout already has.
    /// So this shells out — and treats a missing git, a timeout and a failure the
    /// same way: restore nothing, and let the guard fall back to rebuilding from
    /// the manifest exactly as before.
    /// </summary>
    public static class UniLfsGitRestore
    {
        /// <summary>
        /// Startup runs this, so it cannot wait on git indefinitely. Generous
        /// for a command that reads the index: exceeding it means something is
        /// wrong (an index.lock nobody released, a network filesystem), and the
        /// fallback is the behaviour that shipped before this existed.
        /// </summary>
        public const int TimeoutMs = 10000;

        /// <summary>
        /// Argument budget per git invocation. Windows caps a command line at
        /// 32767 characters; a project can track hundreds of files, so the
        /// pathspecs are spread over several calls well under that.
        /// </summary>
        const int MaxArgumentLength = 6000;

        /// <summary>
        /// Restores the project-relative paths git has and the working tree does
        /// not, and returns the ones that came back.
        ///
        /// Paths that already exist on disk are skipped rather than restored:
        /// <c>git checkout --</c> overwrites, and nothing here is worth throwing
        /// away someone's local edit for. Paths git does not know are skipped
        /// too — one unknown pathspec makes the whole checkout call fail, so a
        /// single newly tracked asset would otherwise cost every other file its
        /// restore.
        /// </summary>
        public static List<string> Restore(string projectRootAbsolute, IEnumerable<string> projectRelativePaths)
        {
            var restored = new List<string>();
            if (string.IsNullOrEmpty(projectRootAbsolute) || projectRelativePaths == null) return restored;
            if (!Directory.Exists(projectRootAbsolute)) return restored;

            var wanted = new List<string>();
            foreach (var path in projectRelativePaths)
            {
                if (!IsUsablePathspec(path)) continue;
                if (File.Exists(Path.Combine(projectRootAbsolute, path))) continue;
                if (!wanted.Contains(path)) wanted.Add(path);
            }
            if (wanted.Count == 0) return restored;

            var known = ListKnownToGit(projectRootAbsolute, wanted);
            if (known.Count == 0) return restored;

            foreach (var batch in Batches(known))
            {
                string output;
                if (!TryRunGit(projectRootAbsolute, "checkout -- " + Quote(batch), out output)) continue;
                foreach (var path in batch)
                    if (File.Exists(Path.Combine(projectRootAbsolute, path)))
                        restored.Add(path);
            }
            return restored;
        }

        /// <summary>
        /// Which of <paramref name="paths"/> the index knows. <c>core.quotePath=false</c>
        /// because git escapes non-ASCII paths in its output by default, and a
        /// project full of Japanese file names would match nothing.
        /// </summary>
        static List<string> ListKnownToGit(string projectRoot, List<string> paths)
        {
            var known = new List<string>();
            foreach (var batch in Batches(paths))
            {
                string output;
                if (!TryRunGit(projectRoot, "-c core.quotePath=false ls-files -- " + Quote(batch), out output)) continue;
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim('\r', ' ');
                    if (trimmed.Length == 0) continue;
                    // ls-files prints paths relative to the working directory,
                    // which is the project root, so they come back in the same
                    // shape they went in.
                    if (paths.Contains(trimmed) && !known.Contains(trimmed)) known.Add(trimmed);
                }
            }
            return known;
        }

        /// <summary>
        /// A pathspec UniLFS is willing to hand to a command line. A path holding
        /// a quote or a control character cannot be quoted safely here, and one
        /// starting with "-" would read as an option; none of them are paths
        /// UniLFS can track anyway, so refusing beats escaping.
        /// </summary>
        static bool IsUsablePathspec(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path[0] == '-') return false;
            foreach (var c in path)
                if (c == '"' || c < ' ') return false;
            return true;
        }

        static IEnumerable<List<string>> Batches(List<string> paths)
        {
            var batch = new List<string>();
            int length = 0;
            foreach (var path in paths)
            {
                int cost = path.Length + 3;
                if (batch.Count > 0 && length + cost > MaxArgumentLength)
                {
                    yield return batch;
                    batch = new List<string>();
                    length = 0;
                }
                batch.Add(path);
                length += cost;
            }
            if (batch.Count > 0) yield return batch;
        }

        static string Quote(List<string> paths)
        {
            var sb = new StringBuilder();
            foreach (var path in paths)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('"').Append(path).Append('"');
            }
            return sb.ToString();
        }

        static bool TryRunGit(string workingDirectory, string arguments, out string standardOutput)
        {
            standardOutput = "";
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            try
            {
                using (var process = Process.Start(info))
                {
                    if (process == null) return false;
                    // Read on the callback threads rather than ReadToEnd: a
                    // blocking read cannot be given up on when the timeout
                    // expires, and it deadlocks outright once either pipe fills.
                    var output = new StringBuilder();
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data == null) return;
                        lock (output) output.Append(e.Data).Append('\n');
                    };
                    process.ErrorDataReceived += (sender, e) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(TimeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return false;
                    }
                    if (process.ExitCode != 0) return false;
                    lock (output) standardOutput = output.ToString();
                    return true;
                }
            }
            catch (Exception)
            {
                // No git on PATH is the ordinary case here, not an error worth
                // a Console line at every editor start: the caller has a
                // fallback and says what it did.
                return false;
            }
        }
    }
}
