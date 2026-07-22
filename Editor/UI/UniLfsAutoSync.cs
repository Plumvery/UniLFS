using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Keeps the project in sync without git hooks.
    ///
    /// Pull side: when the editor starts or regains focus (which is what
    /// happens right after a git pull), a cheap existence check runs; if the
    /// manifest changed and tracked files are missing, UniLFS warns / asks /
    /// pulls depending on the Auto Pull setting.
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
            EditorApplication.delayCall += () =>
            {
                CheckPull();
                CheckPush(true, false);
            };
        }

        static void OnFocusChanged(bool focused)
        {
            if (focused) CheckPull();
            CheckPush(focused, false);
        }

        internal static void OnTrackedAssetsImported()
        {
            CheckPush(true, true);
        }

        // ---------- Pull ----------

        static void CheckPull()
        {
            if (_running || UniLfsOperationLock.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var manifestInfo = new FileInfo(UniLfsPaths.ManifestPath);
            if (!manifestInfo.Exists) return;
            string stamp = manifestInfo.LastWriteTimeUtc.Ticks + ":" + manifestInfo.Length;
            if (SessionState.GetString(HandledPullStampKey, "") == stamp) return;
            SessionState.SetString(HandledPullStampKey, stamp);

            UniLfsManifest manifest;
            try
            {
                manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not read the manifest: " + e.Message);
                return;
            }

            int missing = 0;
            foreach (var f in manifest.files)
            {
                string abs = UniLfsPaths.ToAbsolute(f.path);
                // A placeholder means the guard stood in for content that never
                // arrived, so it counts as missing - Pull is what resolves it.
                if (!File.Exists(abs) || UniLfsPlaceholder.IsPlaceholder(abs))
                    missing++;
            }
            if (missing == 0) return;

            switch (UniLfsSettings.Load().AutoPullMode)
            {
                case UniLfsAutoPullMode.Auto:
                    RunPull();
                    break;
                case UniLfsAutoPullMode.Ask:
                    // delayCall: never open a modal dialog from inside the focus event
                    int missingCount = missing;
                    EditorApplication.delayCall += () => PromptPull(missingCount);
                    break;
                default:
                    Debug.LogWarning("UniLFS: " + missing + " tracked file(s) are missing locally. Open Window > UniLFS and press Pull. "
                        + "(CI: Unity -batchmode -quit -executeMethod UniLFS.Editor.UniLfsCli.Pull)");
                    break;
            }
        }

        static void PromptPull(int missing)
        {
            if (_running || UniLfsOperationLock.IsBusy) return;
            bool pull = EditorUtility.DisplayDialog("UniLFS",
                missing + " tracked file(s) are missing locally - the UniLFS manifest changed, e.g. after a git pull.\n\nDownload them now?",
                "Pull", "Later");
            if (pull)
                RunPull();
            else
                Debug.LogWarning("UniLFS: skipped pulling " + missing + " missing file(s). Use Window > UniLFS > Pull when ready.");
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
                        + (result.KeptModified.Count > 0 ? ", kept " + result.KeptModified.Count + " locally modified file(s)" : "") + ".");
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

        static async void CheckPush(bool focused, bool fromImport)
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

            var modified = statuses.FindAll(s => s.State == UniLfsFileState.Modified);
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
                var result = await UniLfsCore.PushAsync(ProgressReporter(progressId), CancellationToken.None);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);
                if (result.HasErrors)
                    Debug.LogError("UniLFS auto push: uploaded " + result.Uploaded + " file(s), "
                        + result.Errors.Count + " error(s):\n- " + string.Join("\n- ", result.Errors));
                else if (result.Uploaded > 0)
                    Debug.Log("UniLFS auto push: uploaded " + result.Uploaded + " file(s). Remember to commit unilfs.manifest.json.");
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
