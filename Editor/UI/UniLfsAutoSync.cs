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
    /// Track side: an imported file matching the project's unilfs.track
    /// patterns is staged straight away, so nothing large reaches git while
    /// waiting to be noticed (Auto Track setting).
    ///
    /// Each detected state is handled at most once per editor session
    /// (SessionState survives domain reloads) — and only once it has actually
    /// been handled: a state is marked as such when the outcome is known, never
    /// in advance, or a check that never ran would hide the manifest version it
    /// failed on for the rest of the session.
    ///
    /// Asking is <see cref="UniLfsPrompt"/>'s job and deliberately not a modal
    /// dialog: these prompts open during editor startup, where nobody has
    /// promised there is a human to answer them.
    /// </summary>
    static class UniLfsAutoSync
    {
        const string HandledPullStampKey = "UniLFS.AutoSync.HandledManifestStamp";
        /// <summary>
        /// The manifest version the "could not check" warning was already
        /// written for. A check that threw is retried on the next focus change,
        /// so without this a failure that keeps failing would write a Console
        /// line every time the editor is alt-tabbed back into.
        /// </summary>
        const string WarnedPullStampKey = "UniLFS.AutoSync.WarnedManifestStamp";
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

        internal static async void OnAssetsImported(List<string> importedPaths)
        {
            // Tracking first: a file the patterns claim should be staged before
            // the push check runs, or it waits for the next import to be
            // noticed as something to upload.
            await TrackMatchingImportsAsync(importedPaths);
            await CheckPushAsync(true, true);
        }

        // ---------- Track ----------

        /// <summary>
        /// Stages imported files that <c>unilfs.track</c> matches, so a new
        /// asset is out of git from the moment it lands rather than from
        /// whenever someone remembers to track it — which is the whole point of
        /// writing the patterns down.
        ///
        /// Only the paths Unity just handed us are considered; the sweep over
        /// the whole project is Track Matching's job.
        /// </summary>
        static async Task TrackMatchingImportsAsync(List<string> importedPaths)
        {
            if (importedPaths == null || importedPaths.Count == 0) return;
            if (!UniLfsSettings.Load().autoTrack) return;
            if (!File.Exists(UniLfsPaths.TrackPath)) return;

            var patterns = UniLfsTrackPatterns.Load(UniLfsPaths.TrackPath);
            if (patterns.IsEmpty) return;

            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var staged = UniLfsStagedPaths.Load(UniLfsPaths.StagedPath);
            var matched = new List<string>();
            foreach (var imported in importedPaths)
            {
                string rel = UniLfsPaths.Normalize(imported);
                if (!patterns.Matches(rel)) continue;
                if (staged.Contains(rel) || manifest.Find(rel) != null) continue;
                // Imports include folders, and a Pull's refresh re-imports files
                // that were just downloaded - the manifest check above covers
                // those, this covers everything that is not a file at all.
                if (!File.Exists(UniLfsPaths.ToAbsolute(rel))) continue;
                matched.Add(rel);
            }
            if (matched.Count == 0) return;

            try
            {
                var result = await UniLfsCore.TrackAsync(matched, null, CancellationToken.None);
                if (result.TrackedNew > 0)
                    Debug.Log("UniLFS auto track: staged " + result.TrackedNew + " newly imported file(s) matching "
                        + UniLfsPaths.TrackFileName + ". Push uploads them and records them in the manifest.");
                if (result.NotIgnored.Count > 0)
                    Debug.LogWarning("UniLFS: " + result.NotIgnored.Count + " staged file(s) could not be hidden from git - this project is not in a "
                        + "git checkout UniLFS could write .git/info/exclude in. Until they are pushed, 'git add -A' would commit them:\n- "
                        + string.Join("\n- ", result.NotIgnored.ToArray()));
                if (result.HasErrors)
                    Debug.LogWarning("UniLFS auto track: " + result.Errors.Count + " error(s):\n- " + string.Join("\n- ", result.Errors));
            }
            catch (UniLfsBusyException)
            {
                // A Push or Pull the user started holds the lock. Say so rather
                // than retrying blindly: the import is not coming round again,
                // and the sweep picks these up whenever it is next run.
                Debug.LogWarning("UniLFS: " + matched.Count + " newly imported file(s) match " + UniLfsPaths.TrackFileName
                    + " but another UniLFS operation was running. Use Window > UniLFS > Track Matching when it finishes.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS auto track failed: " + e.Message);
            }
        }

        // ---------- Pull ----------

        static async Task CheckPullAsync()
        {
            if (_running || UniLfsOperationLock.IsBusy || EditorApplication.isPlayingOrWillChangePlaymode) return;
            // A prompt raised by an earlier check is still on screen. It owns
            // the decision until it is answered — and because the prompt is not
            // modal, focus changes keep arriving while it waits.
            if (UniLfsPrompt.IsOpen) return;

            var manifestInfo = new FileInfo(UniLfsPaths.ManifestPath);
            if (!manifestInfo.Exists) return;
            string stamp = manifestInfo.LastWriteTimeUtc.Ticks + ":" + manifestInfo.Length;
            if (SessionState.GetString(HandledPullStampKey, "") == stamp) return;

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
                // Something else holds the lock. Nothing about this manifest
                // version was decided, so it stays unhandled and the next focus
                // change retries.
                return;
            }
            catch (Exception e)
            {
                // Same: a check that threw decided nothing, so it is retried
                // too - but only warned about once per manifest version, or a
                // failure that keeps failing writes a line on every alt-tab.
                if (SessionState.GetString(WarnedPullStampKey, "") != stamp)
                {
                    SessionState.SetString(WarnedPullStampKey, stamp);
                    Debug.LogWarning("UniLFS: could not check tracked files: " + e.Message);
                }
                return;
            }
            if (_running) return;

            // Missing (including placeholders the meta guard stood in) and
            // outdated are both "storage has content this project does not".
            // A staged file gone from disk is missing too, but storage has
            // nothing for it, and counting it would ask again on every check.
            var pending = statuses.FindAll(s => s.NeedsDownload);
            if (pending.Count == 0)
            {
                // Nothing to download is an outcome like any other: this
                // manifest version needs no second look.
                SessionState.SetString(HandledPullStampKey, stamp);
                return;
            }
            int outdated = pending.FindAll(s => s.State == UniLfsFileState.Outdated).Count;

            switch (UniLfsSettings.Load().AutoPullMode)
            {
                case UniLfsAutoPullMode.Auto:
                    SessionState.SetString(HandledPullStampKey, stamp);
                    RunPull();
                    break;
                case UniLfsAutoPullMode.Ask:
                    // delayCall: open the prompt from an editor tick of its own
                    // rather than from inside the focus event.
                    int pendingCount = pending.Count;
                    int outdatedCount = outdated;
                    EditorApplication.delayCall += () => PromptPull(stamp, pendingCount, outdatedCount);
                    break;
                default:
                    SessionState.SetString(HandledPullStampKey, stamp);
                    WarnPullNeeded(pending.Count, outdated);
                    break;
            }
        }

        /// <summary>
        /// Asks whether to pull, from a window that does not stop the editor
        /// while it waits. The manifest version is recorded as handled when the
        /// answer arrives - not before - so a prompt that never gets one (the
        /// editor is busy, a domain reload took the window with it) leaves the
        /// check to run again rather than swallowing it for the session.
        /// </summary>
        static void PromptPull(string stamp, int pending, int outdated)
        {
            // Something started between the check and this tick. Say nothing
            // and record nothing: the next focus change asks again.
            if (_running || UniLfsOperationLock.IsBusy) return;
            if (UniLfsPrompt.IsOpen) return;

            if (UniLfsPrompt.Suppressed)
            {
                SessionState.SetString(HandledPullStampKey, stamp);
                WarnPullNeeded(pending, outdated);
                return;
            }

            UniLfsPrompt.Ask("UniLFS",
                Describe(pending, outdated) + " - the UniLFS manifest changed, e.g. after a git pull.\n\nDownload them now?",
                "Pull", "Later", null,
                choice =>
                {
                    SessionState.SetString(HandledPullStampKey, stamp);
                    if (choice == 0)
                        RunPull();
                    else
                        Debug.LogWarning("UniLFS: skipped pulling " + pending + " file(s). Use Window > UniLFS > Pull when ready.");
                });
        }

        /// <summary>What Auto Pull says when it is not going to ask: Off mode, and prompts turned off.</summary>
        static void WarnPullNeeded(int pending, int outdated)
        {
            Debug.LogWarning("UniLFS: " + Describe(pending, outdated) + ". Open Window > UniLFS and press Pull. "
                + "(CI: Unity -batchmode -quit -executeMethod UniLFS.Editor.UniLfsCli.Pull)");
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
                        + "or keep yours with Window > UniLFS > Keep Mine followed by Push.");
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
            // A prompt is already waiting for an answer - including Auto Pull's,
            // which asks about the same files from the other direction.
            if (UniLfsPrompt.IsOpen) return;
            // Staging counts as something to push: a file tracked but never
            // uploaded is exactly what Auto Push is for, and before its first
            // Push there is no manifest for it to appear in.
            if (!File.Exists(UniLfsPaths.ManifestPath) && !File.Exists(UniLfsPaths.StagedPath)) return;

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
            // Staged files join them: nothing else has ever seen that content,
            // so there is no entry to roll back and nothing to guess about.
            var modified = statuses.FindAll(s =>
                (s.State == UniLfsFileState.Modified && s.BaselineKnown) || s.State == UniLfsFileState.Staged);
            if (modified.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var m in modified) sb.Append(m.File.path).Append(':').Append(m.CurrentHash).Append(';');
            string state = sb.ToString();
            if (SessionState.GetString(HandledPushStateKey, "") == state) return;

            if (mode == UniLfsAutoPushMode.Auto)
            {
                SessionState.SetString(HandledPushStateKey, state);
                RunPush();
            }
            else
            {
                int modifiedCount = modified.Count;
                EditorApplication.delayCall += () => PromptPush(state, modifiedCount);
            }
        }

        /// <summary>
        /// The push half of <see cref="PromptPull"/>, and it records the state
        /// it asked about on the same terms: when the answer arrives.
        /// </summary>
        static void PromptPush(string state, int modified)
        {
            if (_running || UniLfsOperationLock.IsBusy) return;
            if (UniLfsPrompt.IsOpen) return;

            if (UniLfsPrompt.Suppressed)
            {
                SessionState.SetString(HandledPushStateKey, state);
                Debug.LogWarning("UniLFS: " + modified + " tracked file(s) have local changes that are not uploaded yet. "
                    + "Use Window > UniLFS > Push before you commit unilfs.manifest.json.");
                return;
            }

            UniLfsPrompt.Ask("UniLFS",
                modified + " tracked file(s) have local changes that are not uploaded yet.\n\nPush them now? (Do this before committing unilfs.manifest.json.)",
                "Push", "Later", null,
                choice =>
                {
                    SessionState.SetString(HandledPushStateKey, state);
                    if (choice == 0)
                        RunPush();
                    else
                        Debug.LogWarning("UniLFS: skipped pushing " + modified + " modified file(s). Use Window > UniLFS > Push before you commit the manifest.");
                });
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
    /// Imports are where both automatic decisions about a new file start: an
    /// asset matching <c>unilfs.track</c> gets staged (Auto Track), and in Auto
    /// Push mode a re-import of a tracked file (i.e. the user just
    /// saved/changed a big asset) triggers a push check without waiting for a
    /// focus change. One postprocessor for both, so they cannot race each other
    /// for the operation lock.
    /// </summary>
    class UniLfsAssetImportWatcher : AssetPostprocessor
    {
        static bool _scheduled;

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (Application.isBatchMode || _scheduled) return;
            // Moved assets count as much as imported ones: dragging a file into
            // a folder the patterns cover is the same decision as creating it
            // there, and Unity reports it here and nowhere else.
            var touched = new List<string>();
            if (importedAssets != null) touched.AddRange(importedAssets);
            if (movedAssets != null) touched.AddRange(movedAssets);
            if (touched.Count == 0) return;
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                UniLfsAutoSync.OnAssetsImported(touched);
            };
        }
    }
}
