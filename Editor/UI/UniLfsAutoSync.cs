using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Keeps the project in sync without git hooks.
    ///
    /// Pull side: when the editor starts or regains focus (which is what
    /// happens right after a git pull), and only if the manifest file itself
    /// changed since the last check, a status check runs; if tracked files are
    /// missing or superseded by a newer version, UniLFS warns / asks / pulls
    /// depending on the Auto Pull setting.
    ///
    /// Push side: when modified tracked files are detected (on focus changes,
    /// startup, or - in Auto mode - right after an asset import), UniLFS asks
    /// or uploads in the background depending on the Auto Push setting, so
    /// blobs are already in storage by the time the manifest gets committed.
    ///
    /// Each detected state is handled at most once per editor session
    /// (SessionState survives domain reloads).
    /// </summary>
    static class UniLfsAutoSync
    {
        const string HandledPullStampKey = "UniLFS.AutoSync.HandledManifestStamp";
        const string HandledPushStateKey = "UniLFS.AutoSync.HandledPushState";
        static bool _running;

        [InitializeOnLoadMethod]
        static void Init()
        {
            if (Application.isBatchMode) return;
            EditorApplication.focusChanged += OnFocusChanged;
            EditorApplication.delayCall += async () =>
            {
                await CheckPullAsync();
                await CheckPushAsync(true, false);
            };
        }

        // Awaited rather than fired side by side: both take the operation lock
        // for their status check, and whichever lost the race used to bail out
        // silently.
        static async void OnFocusChanged(bool focused)
        {
            if (focused) await CheckPullAsync();
            await CheckPushAsync(focused, false);
        }

        internal static async void OnTrackedAssetsImported()
        {
            await CheckPushAsync(true, true);
        }

        // ---------- Pull ----------

        static async Task CheckPullAsync()
        {
            if (_running || UniLfsOperationLock.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var manifestInfo = new FileInfo(UniLfsPaths.ManifestPath);
            if (!manifestInfo.Exists) return;
            string stamp = manifestInfo.LastWriteTimeUtc.Ticks + ":" + manifestInfo.Length;
            if (SessionState.GetString(HandledPullStampKey, "") == stamp) return;
            SessionState.SetString(HandledPullStampKey, stamp);

            List<UniLfsStatusEntry> statuses;
            try
            {
                // A full status rather than the existence check this used to
                // do: a teammate updating an already-tracked file leaves it
                // sitting on disk, so it never counted as missing and this
                // never fired at all. The manifest stamp above means the check
                // runs once per manifest version, and unchanged files answer
                // straight from the hash cache, so it stays cheap.
                statuses = await UniLfsCore.StatusAsync(null, CancellationToken.None);
            }
            catch (UniLfsBusyException)
            {
                // Something else holds the lock. Hand the stamp back so the next
                // focus change retries, rather than marking this manifest
                // version handled on the strength of a check that never ran.
                SessionState.SetString(HandledPullStampKey, "");
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not check tracked files: " + e.Message);
                return;
            }
            if (_running) return;

            // Missing (including placeholders the meta guard stood in) and
            // outdated are both "storage has content this project does not".
            var pending = statuses.FindAll(s =>
                s.State == UniLfsFileState.MissingLocal || s.State == UniLfsFileState.Outdated);
            if (pending.Count == 0) return;
            int outdated = pending.FindAll(s => s.State == UniLfsFileState.Outdated).Count;

            switch (UniLfsSettings.Load().AutoPullMode)
            {
                case UniLfsAutoPullMode.Auto:
                    RunPull();
                    break;
                case UniLfsAutoPullMode.Ask:
                    // delayCall: never open a modal dialog from inside the focus event
                    int pendingCount = pending.Count;
                    int outdatedCount = outdated;
                    EditorApplication.delayCall += () => PromptPull(pendingCount, outdatedCount);
                    break;
                default:
                    Debug.LogWarning("UniLFS: " + Describe(pending.Count, outdated) + ". Open Window > UniLFS and press Pull. "
                        + "(CI: Unity -batchmode -quit -executeMethod UniLFS.Editor.UniLfsCli.Pull)");
                    break;
            }
        }

        static void PromptPull(int pending, int outdated)
        {
            if (_running || UniLfsOperationLock.IsBusy) return;
            bool pull = EditorUtility.DisplayDialog("UniLFS",
                Describe(pending, outdated) + " - the UniLFS manifest changed, e.g. after a git pull.\n\nDownload them now?",
                "Pull", "Later");
            if (pull)
                RunPull();
            else
                Debug.LogWarning("UniLFS: skipped pulling " + pending + " file(s). Use Window > UniLFS > Pull when ready.");
        }

        static string Describe(int pending, int outdated)
        {
            string text = pending + " tracked file(s) need downloading";
            if (outdated > 0) text += " (" + outdated + " superseded by a newer version)";
            return text;
        }

        static async void RunPull()
        {
            if (_running || UniLfsOperationLock.IsBusy) return;
            _running = true;
            int progressId = Progress.Start("UniLFS Auto Pull");
            try
            {
                var result = await UniLfsCore.PullAsync(false, ProgressReporter(progressId), CancellationToken.None);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);
                AssetDatabase.Refresh();
                if (result.HasErrors)
                    Debug.LogError("UniLFS auto pull: downloaded " + result.Downloaded + " file(s), "
                        + result.Errors.Count + " error(s):\n- " + string.Join("\n- ", result.Errors));
                else
                    Debug.Log("UniLFS auto pull: downloaded " + result.Downloaded + " file(s)"
                        + (result.KeptModified.Count > 0 ? ", kept " + result.KeptModified.Count + " locally modified file(s)" : "")
                        + (result.Conflicted.Count > 0 ? ", left " + result.Conflicted.Count + " conflicting file(s) alone" : "") + ".");
                if (result.Conflicted.Count > 0)
                    Debug.LogWarning("UniLFS: " + result.Conflicted.Count + " file(s) changed here and in the manifest since this project last synced, "
                        + "so neither version was picked:\n- " + string.Join("\n- ", result.Conflicted.ToArray())
                        + "\nTake the manifest's version with Window > UniLFS > Restore Modified, "
                        + "or keep yours with Assets > UniLFS > Track Selected followed by Push.");
            }
            catch (UniLfsBusyException)
            {
                // The user started the same operation from the window first.
                Progress.Finish(progressId, Progress.Status.Canceled);
            }
            catch (UniLfsConfigException e)
            {
                Progress.Finish(progressId, Progress.Status.Failed);
                Debug.LogWarning("UniLFS auto pull skipped: " + e.Message);
            }
            catch (Exception e)
            {
                Progress.Finish(progressId, Progress.Status.Failed);
                Debug.LogException(e);
            }
            finally
            {
                _running = false;
            }
        }

        // ---------- Push ----------

        static async Task CheckPushAsync(bool focused, bool fromImport)
        {
            if (_running || UniLfsOperationLock.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!File.Exists(UniLfsPaths.ManifestPath)) return;

            var mode = UniLfsSettings.Load().AutoPushMode;
            if (mode == UniLfsAutoPushMode.Off) return;
            // Import-triggered checks only act in Auto mode; Ask mode only
            // prompts when the user comes back to the editor.
            if (fromImport && mode != UniLfsAutoPushMode.Auto) return;
            if (mode == UniLfsAutoPushMode.Ask && !focused) return;

            List<UniLfsStatusEntry> statuses;
            try
            {
                statuses = await UniLfsCore.StatusAsync(null, CancellationToken.None);
            }
            catch (Exception)
            {
                return;
            }
            if (_running) return;

            // Modified only, and only where a baseline actually says so.
            // Outdated files differ from the manifest too, but pushing one
            // rewrites the entry back to this machine's older copy - offering
            // that as "local changes to upload" is how a teammate's update got
            // undone with a single click. Files with no baseline read as
            // Modified on a guess rather than on evidence, so they are not
            // worth prompting about either; the window still lists them and an
            // explicit Push still takes them. This only decides whether to
            // offer - what the push itself touches is RunPush's requireBaseline.
            var modified = statuses.FindAll(s => s.State == UniLfsFileState.Modified && s.BaselineKnown);
            if (modified.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var m in modified) sb.Append(m.File.path).Append(':').Append(m.CurrentHash).Append(';');
            string state = sb.ToString();
            if (SessionState.GetString(HandledPushStateKey, "") == state) return;
            SessionState.SetString(HandledPushStateKey, state);

            if (mode == UniLfsAutoPushMode.Auto)
            {
                RunPush();
            }
            else
            {
                int modifiedCount = modified.Count;
                EditorApplication.delayCall += () => PromptPush(modifiedCount);
            }
        }

        static void PromptPush(int modified)
        {
            if (_running || UniLfsOperationLock.IsBusy) return;
            bool push = EditorUtility.DisplayDialog("UniLFS",
                modified + " tracked file(s) have local changes that are not uploaded yet.\n\nPush them now? (Do this before committing unilfs.manifest.json.)",
                "Push", "Later");
            if (push)
                RunPush();
            else
                Debug.LogWarning("UniLFS: skipped pushing " + modified + " modified file(s). Use Window > UniLFS > Push before you commit the manifest.");
        }

        static async void RunPush()
        {
            if (_running || UniLfsOperationLock.IsBusy) return;
            _running = true;
            int progressId = Progress.Start("UniLFS Auto Push");
            try
            {
                // requireBaseline: Push rewrites the manifest from whatever it
                // hashed, across every tracked file - not just the ones that
                // triggered this run. Without the flag, one ordinary local edit
                // firing Auto Push would drag every unattributable file along
                // and roll their entries back to this machine's copy.
                var result = await UniLfsCore.PushAsync(true, ProgressReporter(progressId), CancellationToken.None);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);
                if (result.HasErrors)
                    Debug.LogError("UniLFS auto push: uploaded " + result.Uploaded + " file(s), "
                        + result.Errors.Count + " error(s):\n- " + string.Join("\n- ", result.Errors));
                else if (result.Uploaded > 0)
                    Debug.Log("UniLFS auto push: uploaded " + result.Uploaded + " file(s). Remember to commit unilfs.manifest.json.");
                if (result.Unattributed.Count > 0)
                    Debug.LogWarning("UniLFS auto push: left " + result.Unattributed.Count + " file(s) alone - this project has no record of which "
                        + "version they were last synced from, so whether your copy or the manifest's is newer cannot be told apart:\n- "
                        + string.Join("\n- ", result.Unattributed.ToArray())
                        + "\nRun Pull to take the manifest's version, or Push from Window > UniLFS to upload yours.");
            }
            catch (UniLfsBusyException)
            {
                // The user started the same operation from the window first.
                Progress.Finish(progressId, Progress.Status.Canceled);
            }
            catch (UniLfsConfigException e)
            {
                Progress.Finish(progressId, Progress.Status.Failed);
                Debug.LogWarning("UniLFS auto push skipped: " + e.Message);
            }
            catch (Exception e)
            {
                Progress.Finish(progressId, Progress.Status.Failed);
                Debug.LogException(e);
            }
            finally
            {
                _running = false;
            }
        }

        static IProgress<UniLfsProgress> ProgressReporter(int progressId)
        {
            return new Progress<UniLfsProgress>(p => Progress.Report(progressId, p.Fraction, p.Label));
        }
    }

    /// <summary>
    /// In Auto Push mode, re-imports of tracked files (i.e. the user just
    /// saved/changed a big asset) trigger a push check without waiting for a
    /// focus change.
    /// </summary>
    class UniLfsAssetImportWatcher : AssetPostprocessor
    {
        static bool _scheduled;

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (Application.isBatchMode || _scheduled) return;
            if (importedAssets == null || importedAssets.Length == 0) return;
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                UniLfsAutoSync.OnTrackedAssetsImported();
            };
        }
    }
}
