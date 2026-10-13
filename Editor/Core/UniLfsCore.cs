using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// The UniLFS operations. Each one runs mylfs (see <see cref="UniLfsMylfs"/>),
    /// which owns every rule about tracking, staging, pushing and pulling;
    /// this class keeps the API the editor UI, Auto Sync and batch mode were
    /// written against, and the in-process lock that stops two of them from
    /// overlapping. mylfs adds a cross-process lock of its own, so a terminal
    /// and the editor cannot run on one checkout at once either.
    ///
    /// All async methods are safe to block on from batch mode (nothing
    /// marshals back to the main thread) and never call AssetDatabase -
    /// callers refresh after Pull.
    /// </summary>
    public static class UniLfsCore
    {
        public static async Task<List<UniLfsStatusEntry>> StatusAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var report = await StatusAsync(false, progress, ct).ConfigureAwait(false);
            return report.Files;
        }

        /// <summary>
        /// Status, optionally preceded by asking remote storage whether it
        /// really holds the blobs the manifest references (one request per
        /// distinct blob, which is why the caller opts in).
        /// </summary>
        public static async Task<UniLfsStatusReport> StatusAsync(bool verifyRemote, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire(verifyRemote ? "Verify" : "Status"))
            {
                var flags = verifyRemote ? new[] { "--verify" } : null;
                string json = await UniLfsMylfs.RunAsync("status", flags, null, progress, ct).ConfigureAwait(false);
                return MylfsStatusReport.Parse(json).ToReport();
            }
        }

        /// <summary>
        /// Stages files for tracking and hides them from git locally. Writes
        /// neither the manifest nor an upload - Push does that.
        /// </summary>
        public static async Task<UniLfsOpResult> TrackAsync(IEnumerable<string> paths, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Track"))
                return await RunResultAsync("track", null, paths.ToList(), progress, ct).ConfigureAwait(false);
        }

        /// <summary>Stages every project file <c>unilfs.track</c> matches that is not tracked yet.</summary>
        public static async Task<UniLfsOpResult> TrackMatchingAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Track"))
                return await RunResultAsync("track", new[] { "--matching" }, null, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Of <paramref name="paths"/>, stages the ones <c>unilfs.track</c>
        /// matches and nothing tracks yet; the rest are skipped silently. What
        /// Auto Track hands over after an import.
        /// </summary>
        public static async Task<UniLfsOpResult> TrackMatchingAsync(IEnumerable<string> paths, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Track"))
                return await RunResultAsync("track", new[] { "--matching" }, paths.ToList(), progress, ct).ConfigureAwait(false);
        }

        /// <summary>Removes files from the manifest and from staging. Files stay on disk.</summary>
        public static UniLfsOpResult Untrack(IEnumerable<string> paths)
        {
            using (UniLfsOperationLock.Acquire("Untrack"))
                return MylfsResult.Parse(UniLfsMylfs.Run("untrack", null, paths.ToList())).ToResult();
        }

        /// <summary>
        /// Records that the local copy of these files wins over what the
        /// manifest names ("keep mine"). The next Push consumes the decision.
        /// </summary>
        public static UniLfsOpResult KeepLocal(IEnumerable<string> paths)
        {
            using (UniLfsOperationLock.Acquire("Keep Mine"))
                return MylfsResult.Parse(UniLfsMylfs.Run("keep-mine", null, paths.ToList())).ToResult();
        }

        public static async Task<UniLfsOpResult> PushAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            return await PushAsync(false, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Uploads blobs storage is missing, and only then records them in the
        /// manifest. Staged paths become manifest entries here.
        /// </summary>
        /// <param name="requireBaseline">
        /// Skip files whose difference from the manifest cannot be attributed
        /// to either side. Callers acting on their own - Auto Push - pass true.
        /// </param>
        public static async Task<UniLfsOpResult> PushAsync(bool requireBaseline, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Push"))
                return await RunResultAsync("push", requireBaseline ? new[] { "--require-baseline" } : null, null, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads files that are missing or outdated. With
        /// <paramref name="restoreModified"/>, locally modified and conflicted
        /// files are overwritten too; otherwise they are reported and kept.
        /// </summary>
        public static async Task<UniLfsOpResult> PullAsync(bool restoreModified = false, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire(restoreModified ? "Restore" : "Pull"))
                return await RunResultAsync("pull", restoreModified ? new[] { "--restore-modified" } : null, null, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks every blob the manifest references exists in storage,
        /// without downloading. Missing blobs are reported in Errors.
        /// </summary>
        public static async Task<UniLfsOpResult> VerifyRemoteAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Verify"))
                return await RunResultAsync("verify", null, null, progress, ct).ConfigureAwait(false);
        }

        /// <summary>Returns a human-readable success message, or throws.</summary>
        public static async Task<string> TestConnectionAsync(CancellationToken ct = default(CancellationToken))
        {
            string json = await UniLfsMylfs.RunAsync("test", null, null, null, ct).ConfigureAwait(false);
            return JsonUtility.FromJson<MessageResult>(json).message;
        }

        /// <summary>
        /// Signs in to Google Drive in the system browser and stores the
        /// refresh token in UserSettings/UniLFS.json. Returns the account's
        /// address, or "" when it could not be read. Reload
        /// <see cref="UniLfsUserSettings"/> afterwards.
        /// </summary>
        public static async Task<string> SignInToGoogleAsync(CancellationToken ct = default(CancellationToken))
        {
            string json = await UniLfsMylfs.RunAsync("login", null, null, null, ct).ConfigureAwait(false);
            return JsonUtility.FromJson<SignInResult>(json).accountEmail ?? "";
        }

        /// <summary>Creates a folder in the signed-in account's My Drive and returns its ID.</summary>
        public static async Task<string> CreateDriveFolderAsync(string name, CancellationToken ct = default(CancellationToken))
        {
            string json = await UniLfsMylfs.RunAsync("drive-create-folder", new[] { name }, null, null, ct).ConfigureAwait(false);
            return JsonUtility.FromJson<FolderResult>(json).id;
        }

        /// <summary>The single wording for "sign-in succeeded".</summary>
        public static string DescribeSignIn(string accountEmail)
        {
            return string.IsNullOrEmpty(accountEmail)
                ? "Signed in to Google Drive. (Could not read the account's email address.)"
                : "Signed in to Google Drive as " + accountEmail + ".";
        }

        /// <summary>What <c>unilfs.track</c> currently says: how many patterns, and which lines could not be read.</summary>
        public static async Task<UniLfsPatternSummary> ReadPatternsAsync(CancellationToken ct = default(CancellationToken))
        {
            string json = await UniLfsMylfs.RunAsync("patterns", null, null, null, ct).ConfigureAwait(false);
            var summary = JsonUtility.FromJson<UniLfsPatternSummary>(json);
            if (summary.errors == null) summary.errors = new List<string>();
            return summary;
        }

        /// <summary>
        /// Rewrites the managed .gitignore block from the manifest, which also
        /// puts the credential and staging files under it. Best effort: a
        /// machine without Node.js gets the block on its first real operation.
        /// </summary>
        public static void RefreshIgnoreBlock()
        {
            try
            {
                UniLfsMylfs.Run("refresh-ignore", null, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not update the .gitignore block: " + e.Message);
            }
        }

        public static string GitRemoveHint(IEnumerable<string> newlyTrackedPaths)
        {
            var paths = newlyTrackedPaths.ToList();
            if (paths.Count == 0) return null;
            var lines = new List<string>
            {
                "If these files were previously committed to git, remove them from the index (files stay on disk):",
            };
            lines.AddRange(paths.Select(p => "  git rm --cached -- \"" + p + "\""));
            return string.Join("\n", lines);
        }

        static async Task<UniLfsOpResult> RunResultAsync(string command, string[] flags, List<string> paths, IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            string json = await UniLfsMylfs.RunAsync(command, flags, paths, progress, ct).ConfigureAwait(false);
            return MylfsResult.Parse(json).ToResult();
        }

        // ------------------------------------------------- mylfs JSON shapes

        [Serializable]
        class MessageResult
        {
            public string message;
        }

        [Serializable]
        class SignInResult
        {
            public string accountEmail;
        }

        [Serializable]
        class FolderResult
        {
            public string id;
        }

        [Serializable]
        class MylfsResult
        {
            public int uploaded;
            public int downloaded;
            public int skipped;
            public int trackedNew;
            public int promoted;
            public int untracked;
            public List<string> missingLocal;
            public List<string> keptModified;
            public List<string> newlyTracked;
            public List<string> notIgnored;
            public List<string> outdated;
            public List<string> conflicted;
            public List<string> unattributed;
            public List<string> errors;

            public static MylfsResult Parse(string json)
            {
                return JsonUtility.FromJson<MylfsResult>(json);
            }

            public UniLfsOpResult ToResult()
            {
                var r = new UniLfsOpResult
                {
                    Uploaded = uploaded,
                    Downloaded = downloaded,
                    Skipped = skipped,
                    TrackedNew = trackedNew,
                    Promoted = promoted,
                    Untracked = untracked,
                };
                Fill(r.MissingLocal, missingLocal);
                Fill(r.KeptModified, keptModified);
                Fill(r.NewlyTracked, newlyTracked);
                Fill(r.NotIgnored, notIgnored);
                Fill(r.Outdated, outdated);
                Fill(r.Conflicted, conflicted);
                Fill(r.Unattributed, unattributed);
                Fill(r.Errors, errors);
                return r;
            }
        }

        [Serializable]
        class MylfsStatusFile
        {
            public string path;
            public string hash;
            public long size;
            public string guid;
            public string state;
            public string currentHash;
            public long currentSize;
            public bool remoteKnown;
            public bool baselineKnown;
        }

        [Serializable]
        class MylfsStatusReport
        {
            public List<MylfsStatusFile> files;
            public bool verified;
            public int confirmed;
            public List<string> missingRemote;
            public List<string> failures;
            public List<string> notIgnored;

            public static MylfsStatusReport Parse(string json)
            {
                return JsonUtility.FromJson<MylfsStatusReport>(json);
            }

            public UniLfsStatusReport ToReport()
            {
                var report = new UniLfsStatusReport { Verified = verified, Confirmed = confirmed };
                Fill(report.MissingRemote, missingRemote);
                Fill(report.Failures, failures);
                Fill(report.NotIgnored, notIgnored);
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        report.Files.Add(new UniLfsStatusEntry
                        {
                            File = new UniLfsManifestFile
                            {
                                path = f.path,
                                hash = f.hash ?? "",
                                size = f.size,
                                guid = string.IsNullOrEmpty(f.guid) ? null : f.guid,
                            },
                            State = ParseState(f.state),
                            CurrentHash = string.IsNullOrEmpty(f.currentHash) ? null : f.currentHash,
                            CurrentSize = f.currentSize,
                            RemoteKnown = f.remoteKnown,
                            BaselineKnown = f.baselineKnown,
                        });
                    }
                }
                return report;
            }
        }

        static UniLfsFileState ParseState(string state)
        {
            UniLfsFileState parsed;
            if (!string.IsNullOrEmpty(state) && Enum.TryParse(state, out parsed)) return parsed;
            // A state this package does not know yet must not read as
            // anything an operation would act on.
            return UniLfsFileState.Conflicted;
        }

        static void Fill(List<string> target, List<string> source)
        {
            if (source != null) target.AddRange(source);
        }
    }

    /// <summary>What <c>unilfs.track</c> says, as reported by mylfs.</summary>
    [Serializable]
    public class UniLfsPatternSummary
    {
        public bool exists;
        public int count;
        public List<string> errors = new List<string>();
    }
}
