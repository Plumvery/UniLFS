using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Batch mode entry points for CI, e.g.:
    ///   Unity -batchmode -nographics -quit -projectPath . -executeMethod UniLFS.Editor.UniLfsCli.Pull
    /// Credentials come from environment variables (see UniLfsCredentials).
    /// A non-empty error list makes the method throw so the Unity process exits
    /// with a non-zero code and fails the CI step.
    /// </summary>
    public static class UniLfsCli
    {
        public static void Pull()
        {
            bool restoreModified = Environment.GetEnvironmentVariable("UNILFS_PULL_RESTORE_MODIFIED") == "1";
            Run("Pull", () => UniLfsCore.PullAsync(restoreModified, new ConsoleProgress(), CancellationToken.None), true);
        }

        public static void Push()
        {
            Run("Push", () => UniLfsCore.PushAsync(new ConsoleProgress(), CancellationToken.None), false);
        }

        /// <summary>
        /// Stages every file matching <c>unilfs.track</c> that is not tracked
        /// yet. Uploads nothing — follow it with Push, in that order, exactly
        /// as the editor buttons do. Reported on its own rather than through
        /// <see cref="Run"/>, whose counters are about transfers.
        /// </summary>
        public static void Track()
        {
            Debug.Log("UniLFS CLI: Track starting...");
            var result = UniLfsCore.TrackMatchingAsync(new ConsoleProgress(), CancellationToken.None).GetAwaiter().GetResult();
            Debug.Log("UniLFS CLI: Track finished. staged=" + result.TrackedNew
                + " alreadyTracked=" + result.Skipped
                + " errors=" + result.Errors.Count);
            foreach (var path in result.NewlyTracked) Debug.Log("UniLFS CLI: staged " + path);
            foreach (var path in result.NotIgnored) Debug.LogWarning("UniLFS CLI: staged but not hidden from git: " + path);
            foreach (var error in result.Errors) Debug.LogError("UniLFS CLI: " + error);
            if (result.HasErrors)
                throw new Exception("UniLFS Track finished with " + result.Errors.Count + " error(s); see the log above.");
        }

        /// <summary>
        /// Fails (non-zero exit) when the manifest references blobs that do not
        /// exist in remote storage. Push is the only thing that writes an entry,
        /// and only for content storage confirmed, so this is now about what
        /// that guarantee cannot cover: a hand-edited or badly merged manifest,
        /// and blobs deleted from the bucket afterwards. Needs no local asset
        /// files, only credentials.
        /// </summary>
        public static void Verify()
        {
            Debug.Log("UniLFS CLI: Verify starting...");
            var result = UniLfsCore.VerifyRemoteAsync(new ConsoleProgress(), CancellationToken.None).GetAwaiter().GetResult();
            Debug.Log("UniLFS CLI: Verify finished. present=" + result.Skipped + " problems=" + result.Errors.Count);
            foreach (var error in result.Errors) Debug.LogError("UniLFS CLI: " + error);
            if (result.HasErrors)
                throw new Exception("UniLFS Verify failed: " + result.Errors.Count + " blob(s) missing or unreachable."
                    + " The manifest names content storage does not have - most likely a merge or hand-edit that kept an entry"
                    + " nobody pushed, or a blob deleted from the bucket.");
        }

        public static void Status()
        {
            var statuses = UniLfsCore.StatusAsync(new ConsoleProgress(), CancellationToken.None).GetAwaiter().GetResult();
            if (statuses.Count == 0)
            {
                Debug.Log("UniLFS: no tracked files.");
                return;
            }
            var sb = new System.Text.StringBuilder("UniLFS status (" + statuses.Count + " tracked):\n");
            foreach (var s in statuses)
                sb.Append("  [").Append(s.State).Append("] ").Append(s.File.path).Append(" (").Append(s.File.size).Append(" bytes)\n");
            Debug.Log(sb.ToString());
        }

        static void Run(string label, Func<Task<UniLfsOpResult>> operation, bool refreshAssets)
        {
            Debug.Log("UniLFS CLI: " + label + " starting...");
            var result = operation().GetAwaiter().GetResult();
            if (refreshAssets) AssetDatabase.Refresh();
            Debug.Log("UniLFS CLI: " + label + " finished. uploaded=" + result.Uploaded
                + " downloaded=" + result.Downloaded
                + " promoted=" + result.Promoted
                + " upToDate=" + result.Skipped
                + " missingLocal=" + result.MissingLocal.Count
                + " keptModified=" + result.KeptModified.Count
                + " outdated=" + result.Outdated.Count
                + " conflicted=" + result.Conflicted.Count
                + " errors=" + result.Errors.Count);
            foreach (var path in result.MissingLocal) Debug.LogWarning("UniLFS CLI: missing locally: " + path);
            foreach (var path in result.KeptModified) Debug.LogWarning("UniLFS CLI: locally modified, kept: " + path);
            foreach (var path in result.Outdated) Debug.LogWarning("UniLFS CLI: local copy is older than the manifest, not pushed (run Pull): " + path);
            foreach (var path in result.Conflicted) Debug.LogWarning("UniLFS CLI: changed locally and in the manifest, left alone: " + path);
            foreach (var error in result.Errors) Debug.LogError("UniLFS CLI: " + error);
            if (result.HasErrors)
                throw new Exception("UniLFS " + label + " finished with " + result.Errors.Count + " error(s); see the log above.");
        }

        /// <summary>
        /// Logs one line per phase start and per finished item, so byte progress
        /// does not spam the log and parallel transfers still each get a line.
        /// </summary>
        class ConsoleProgress : IProgress<UniLfsProgress>
        {
            string _lastPhase;

            public void Report(UniLfsProgress p)
            {
                if (p.Phase != _lastPhase)
                {
                    _lastPhase = p.Phase;
                    if (!string.IsNullOrEmpty(p.Phase))
                        Debug.Log("UniLFS [" + p.Phase + "] " + p.Total + " item(s)");
                }
                if (!string.IsNullOrEmpty(p.Completed))
                    Debug.Log("UniLFS [" + p.Phase + "] (" + p.Done + "/" + p.Total + ") " + p.Completed);
            }
        }
    }
}
