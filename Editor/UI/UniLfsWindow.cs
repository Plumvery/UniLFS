using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    public class UniLfsWindow : EditorWindow
    {
        [MenuItem("Window/UniLFS")]
        public static void Open()
        {
            var window = GetWindow<UniLfsWindow>("UniLFS");
            window.minSize = new Vector2(520, 300);
            window.Show();
        }

        List<UniLfsStatusEntry> _statuses;
        bool _busy;
        string _busyLabel = "";
        UniLfsProgress _progress;
        string _lastMessage = "";
        Vector2 _scroll;
        CancellationTokenSource _cts;
        /// <summary>A refresh that lost the race for the operation lock, retried once it frees.</summary>
        bool _refreshPending;
        /// <summary>Whether that queued refresh was the toolbar one, which also verifies against storage.</summary>
        bool _refreshPendingVerify;

        // Provider config is read from disk, so it is cached rather than
        // reloaded every repaint.
        UniLfsSettings _settings;
        UniLfsUserSettings _user;
        List<string> _missingConfig = new List<string>();

        void OnEnable()
        {
            RefreshProviderInfo();
            if (File.Exists(UniLfsPaths.ManifestPath))
                RefreshStatus();
        }

        void OnFocus()
        {
            RefreshProviderInfo();
        }

        void RefreshProviderInfo()
        {
            _settings = UniLfsSettings.Load();
            _user = UniLfsUserSettings.Load();
            _missingConfig = UniLfsProviderStatus.MissingRequirements(_settings, _user);
        }

        void OnGUI()
        {
            if (_settings == null) RefreshProviderInfo();
            DrawToolbar();
            DrawHeader();
            if (_busy) DrawProgress();
            DrawList();
            DrawFooter();

            // An operation started elsewhere (Auto Push, the asset menu) also
            // disables the toolbar, so keep repainting until it releases — then
            // run whatever refresh was queued while it held the lock.
            if (!_busy)
            {
                if (UniLfsOperationLock.IsBusy) Repaint();
                else if (_refreshPending)
                {
                    _refreshPending = false;
                    bool verify = _refreshPendingVerify;
                    _refreshPendingVerify = false;
                    RefreshStatus(verify);
                }
            }
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            using (new EditorGUI.DisabledScope(_busy || UniLfsOperationLock.IsBusy))
            {
                if (GUILayout.Button(new GUIContent("Refresh", "Re-check tracked files, and ask storage whether it really has the manifest's blobs"), EditorStyles.toolbarButton, GUILayout.Width(60)))
                    RefreshStatus(true);
                if (GUILayout.Button(new GUIContent("Push", "Upload changed/new blobs and update the manifest"), EditorStyles.toolbarButton, GUILayout.Width(50)))
                    RunOperation("Push", (p, ct) => UniLfsCore.PushAsync(p, ct), false);
                if (GUILayout.Button(new GUIContent("Pull", "Download files that are missing locally or superseded by a newer version"), EditorStyles.toolbarButton, GUILayout.Width(50)))
                    RunOperation("Pull", (p, ct) => UniLfsCore.PullAsync(false, p, ct), true);
                if (GUILayout.Button(new GUIContent("Restore Modified", "Overwrite locally modified and conflicting files with the manifest version"), EditorStyles.toolbarButton, GUILayout.Width(110)))
                {
                    if (EditorUtility.DisplayDialog("UniLFS - Restore Modified",
                        "Overwrite locally modified and conflicting tracked files with the version recorded in the manifest?\nLocal changes will be lost.",
                        "Restore", "Cancel"))
                        RunOperation("Restore", (p, ct) => UniLfsCore.PullAsync(true, p, ct), true);
                }
                // The other half of the same decision, next to it: Restore
                // Modified is "take theirs", and until this existed the only way
                // to say "keep mine" was to re-run Track, which rewrote the
                // committed manifest to do it.
                if (GUILayout.Button(new GUIContent("Keep Mine", "Resolve conflicting files in favour of the local copy, then Push to upload it"), EditorStyles.toolbarButton, GUILayout.Width(75)))
                    KeepMine();
                GUILayout.Space(12);
                // Local-only refreshes: Track/Untrack change the manifest, not
                // what storage holds.
                if (GUILayout.Button("Track Selected", EditorStyles.toolbarButton, GUILayout.Width(95)))
                    UniLfsAssetMenu.TrackSelection(() => RefreshStatus());
                if (GUILayout.Button(new GUIContent("Track Matching", "Track every file in the project matching " + UniLfsPaths.TrackFileName + " that is not tracked yet"),
                    EditorStyles.toolbarButton, GUILayout.Width(100)))
                    UniLfsAssetMenu.TrackMatching(() => RefreshStatus());
                if (GUILayout.Button("Untrack Selected", EditorStyles.toolbarButton, GUILayout.Width(105)))
                    UniLfsAssetMenu.UntrackSelection(() => RefreshStatus());
            }
            GUILayout.FlexibleSpace();
            if (_busy && _cts != null)
            {
                if (GUILayout.Button("Cancel", EditorStyles.toolbarButton, GUILayout.Width(55)))
                    _cts.Cancel();
            }
            if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(60)))
                SettingsService.OpenProjectSettings("Project/UniLFS");
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Records "the local copy wins" for every conflicting file, which the
        /// next Push acts on. Nothing is uploaded here: the decision and the
        /// upload are separate on purpose, so an interrupted Push cannot leave
        /// half a resolution behind.
        /// </summary>
        void KeepMine()
        {
            var conflicted = new List<string>();
            if (_statuses != null)
                foreach (var s in _statuses)
                    if (s.State == UniLfsFileState.Conflicted) conflicted.Add(s.File.path);
            if (conflicted.Count == 0)
            {
                _lastMessage = "Keep Mine: no conflicting files.";
                return;
            }
            if (!EditorUtility.DisplayDialog("UniLFS - Keep Mine",
                "Resolve " + conflicted.Count + " conflicting file(s) in favour of the copy on this machine?\n\n"
                + "Nothing is uploaded yet - press Push afterwards. The version the manifest names now will stop being referenced, "
                + "but its blob stays in storage.",
                "Keep Mine", "Cancel"))
                return;
            try
            {
                var result = UniLfsCore.KeepLocal(conflicted);
                _lastMessage = "Keep Mine: " + result.Conflicted.Count + " file(s) will be pushed from this machine's copy. Press Push.";
                if (result.HasErrors)
                    Debug.LogWarning("UniLFS Keep Mine:\n- " + string.Join("\n- ", result.Errors));
                else
                    Debug.Log("UniLFS " + _lastMessage);
            }
            catch (UniLfsBusyException e)
            {
                _lastMessage = e.Message;
            }
            RefreshStatus();
        }

        void DrawHeader()
        {
            string provider = _settings.ProviderKind == UniLfsProviderKind.GoogleDrive
                ? "Google Drive"
                : "S3 compatible (" + (string.IsNullOrEmpty(_settings.s3Bucket) ? "not configured" : _settings.s3Bucket) + ")";
            GUILayout.Label("Provider: " + provider, EditorStyles.miniLabel);

            // Surfacing setup here as well as in the startup dialog: the sign-in
            // button is buried in Project Settings, and a failed Push is a poor
            // way to find out nobody ever signed in.
            if (_missingConfig.Count == 0)
            {
                if (UniLfsSignInPrompt.Muted)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Re-enable setup reminders",
                        "Setup reminders were muted for this project with \"Don't ask again\""), EditorStyles.miniButton))
                    {
                        UniLfsSignInPrompt.Muted = false;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                return;
            }

            bool signInOnly = UniLfsProviderStatus.NeedsGoogleSignInOnly(_settings, _user);
            EditorGUILayout.HelpBox(
                signInOnly
                    ? "Not signed in to Google Drive. Push and Pull will fail until you sign in."
                    : "Push and Pull need " + UniLfsProviderStatus.Describe(_missingConfig) + ".",
                MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_busy || UniLfsOperationLock.IsBusy))
            {
                if (signInOnly && GUILayout.Button("Sign in with Google", GUILayout.Width(150)))
                    SignIn();
                if (GUILayout.Button("Open Settings", GUILayout.Width(110)))
                    SettingsService.OpenProjectSettings("Project/UniLFS");
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        async void SignIn()
        {
            try
            {
                var tokens = await GoogleOAuth.SignInAsync(
                    UniLfsCredentials.DriveClientId(_settings, _user),
                    UniLfsCredentials.DriveClientSecret(_settings, _user),
                    CancellationToken.None);
                _user.driveRefreshToken = tokens.RefreshToken;
                _user.Save();
                _lastMessage = "Signed in to Google Drive.";
            }
            catch (Exception e)
            {
                _lastMessage = "Sign-in failed: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                RefreshProviderInfo();
                Repaint();
            }
        }

        void DrawProgress()
        {
            var barRect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(barRect, _progress.Fraction,
                string.IsNullOrEmpty(_progress.Label) ? _busyLabel + "..." : _progress.Label);

            // The detail line always reserves its row, even while empty: letting
            // it appear and vanish shifted the whole file list up and down.
            var detailRect = EditorGUILayout.GetControlRect(false, 14);
            if (_progress.TotalBytes > 0)
            {
                string detail = EditorUtility.FormatBytes(_progress.DoneBytes) + " / " + EditorUtility.FormatBytes(_progress.TotalBytes);
                if (_progress.Active > 1) detail += "   -   " + _progress.Active + " transfers in flight";
                GUI.Label(detailRect, detail, EditorStyles.miniLabel);
            }
        }

        void DrawList()
        {
            if (_statuses == null || _statuses.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No files are tracked yet.\n\n" +
                    "1. Configure a storage provider in Project Settings > UniLFS.\n" +
                    "2. Select large assets in the Project window and run Assets > UniLFS > Track Selected.\n" +
                    "3. Press Push: that uploads them and records them in unilfs.manifest.json. Commit it and .gitignore afterwards.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var s in _statuses)
            {
                EditorGUILayout.BeginHorizontal();
                Color color;
                string tooltip;
                string badge = StateBadge(s, out color, out tooltip);
                var previous = GUI.color;
                GUI.color = color;
                GUILayout.Label(new GUIContent("●", tooltip), GUILayout.Width(16));
                GUI.color = previous;
                GUILayout.Label(new GUIContent(s.File.path, s.File.path + "\n"
                    + (string.IsNullOrEmpty(s.File.hash) ? "not in the manifest yet - Push records it" : "sha256: " + s.File.hash)),
                    GUILayout.ExpandWidth(true));
                GUILayout.Label(EditorUtility.FormatBytes(s.File.size), EditorStyles.miniLabel, GUILayout.Width(80));
                GUILayout.Label(new GUIContent(badge, tooltip), EditorStyles.miniLabel, GUILayout.Width(90));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawFooter()
        {
            GUILayout.FlexibleSpace();
            if (_statuses != null && _statuses.Count > 0)
            {
                int upToDate = 0, notPushed = 0, modified = 0, outdated = 0, conflicted = 0, missing = 0, staged = 0;
                long totalSize = 0;
                foreach (var s in _statuses)
                {
                    totalSize += s.File.size;
                    switch (s.State)
                    {
                        case UniLfsFileState.UpToDate:
                            if (s.RemoteKnown) upToDate++; else notPushed++;
                            break;
                        case UniLfsFileState.Modified: modified++; break;
                        case UniLfsFileState.Outdated: outdated++; break;
                        case UniLfsFileState.Conflicted: conflicted++; break;
                        case UniLfsFileState.MissingLocal: missing++; break;
                        case UniLfsFileState.Staged: staged++; break;
                    }
                }
                // The four everyday states always show, so their numbers stay in
                // the same place; the three that mean "someone has to do
                // something" only take up room when there is something to say.
                string summary = _statuses.Count + " tracked (" + EditorUtility.FormatBytes(totalSize) + ")  |  " +
                    upToDate + " up to date, " + notPushed + " not pushed, " + modified + " modified, " + missing + " missing";
                if (staged > 0) summary += ", " + staged + " staged";
                if (outdated > 0) summary += ", " + outdated + " outdated";
                if (conflicted > 0) summary += ", " + conflicted + " conflicted";
                GUILayout.Label(summary, EditorStyles.miniLabel);
            }
            // GUILayout.Label, not LabelField: LabelField reserves a single line,
            // so the tail of a long summary was being cut off.
            if (!string.IsNullOrEmpty(_lastMessage))
                GUILayout.Label(_lastMessage, EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>
        /// Seven states. "Tracked here" and "in the manifest" are different
        /// questions — a staged file is one machine's decision until Push makes
        /// it everyone's — and so are "matches the manifest" and "exists in
        /// remote storage". A file that differs from the manifest still has to
        /// say which side moved, because your own edit and someone else's push
        /// need opposite buttons.
        /// </summary>
        static string StateBadge(UniLfsStatusEntry entry, out Color color, out string tooltip)
        {
            switch (entry.State)
            {
                case UniLfsFileState.Staged:
                    // The same blue as "not pushed" on purpose: to the person
                    // looking, both mean nobody else can see this yet. The
                    // difference is that this one is definite - there is no
                    // manifest entry - while "not pushed" is inferred from a
                    // per-machine record.
                    color = new Color(0.45f, 0.65f, 0.95f);
                    tooltip = "Tracked on this machine and not uploaded yet, so it is not in the manifest and nobody else can see it. "
                        + "Run Push: that is what records it for everyone.";
                    return "staged";
                case UniLfsFileState.Modified:
                    color = new Color(0.95f, 0.75f, 0.2f);
                    tooltip = "Locally changed since the last Push. Run Push to upload the new version.";
                    return "modified";
                case UniLfsFileState.Outdated:
                    color = new Color(0.3f, 0.8f, 0.85f);
                    tooltip = "Someone else pushed a newer version and this copy is the one you last synced. "
                        + "Run Pull to get theirs. Pushing would put your older copy back in the manifest.";
                    return "outdated";
                case UniLfsFileState.Conflicted:
                    color = new Color(0.9f, 0.45f, 0.85f);
                    tooltip = "Changed here and in the manifest since you last synced - or tracked separately on both sides - "
                        + "so neither version wins automatically. Take the manifest's with Restore Modified, or keep yours with "
                        + "Keep Mine and then Push.";
                    return "conflicted";
                case UniLfsFileState.MissingLocal:
                    color = new Color(0.95f, 0.35f, 0.3f);
                    tooltip = string.IsNullOrEmpty(entry.File.hash)
                        ? "Staged here but not on disk, and never uploaded - there is nothing to download it from. "
                          + "Put the file back, or untrack it."
                        : "Tracked but not on disk. Run Pull to download it.";
                    return "missing";
                default:
                    if (!entry.RemoteKnown)
                    {
                        color = new Color(0.45f, 0.65f, 0.95f);
                        tooltip = "Matches the manifest, but this machine has no proof the blob was uploaded "
                            + "(typically a file tracked but never pushed). Run Push to upload it.";
                        return "not pushed";
                    }
                    color = new Color(0.35f, 0.8f, 0.35f);
                    tooltip = "Matches the manifest and the blob is confirmed in remote storage.";
                    return "up to date";
            }
        }

        /// <param name="verifyRemote">
        /// Also ask storage whether it has the manifest's blobs. Pressed
        /// Refresh does; the automatic refreshes (window opened, operation
        /// finished) stay local-only so they cost no requests.
        /// </param>
        async void RefreshStatus(bool verifyRemote = false)
        {
            if (_busy) return;
            if (UniLfsOperationLock.IsBusy)
            {
                // Auto Push/Pull or the asset menu holds the lock. Queue the
                // refresh instead of failing, or the list would sit stale until
                // the user pressed Refresh themselves.
                _refreshPending = true;
                _refreshPendingVerify |= verifyRemote;
                return;
            }
            // Nothing to ask storage with: the check would only add one failure
            // per blob under the setup banner that is already on screen.
            bool verify = verifyRemote && _missingConfig.Count == 0;
            _busy = true;
            _busyLabel = verify ? "Verifying" : "Refreshing";
            _progress = default(UniLfsProgress);
            _cts = new CancellationTokenSource();
            try
            {
                var report = await UniLfsCore.StatusAsync(verify, UiProgress(), _cts.Token);
                _statuses = report.Files;
                if (verifyRemote && !verify)
                    _lastMessage = "Refreshed local state only - checking storage needs " + UniLfsProviderStatus.Describe(_missingConfig) + ".";
                else if (report.Verified)
                    ReportVerify(report);
                ReportNotIgnored(report.NotIgnored);
            }
            catch (OperationCanceledException)
            {
            }
            catch (UniLfsBusyException e)
            {
                // Benign: Auto Push/Pull got there first. Not worth a stack trace.
                _lastMessage = e.Message;
            }
            catch (Exception e)
            {
                _lastMessage = "Status failed: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                _busy = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                Repaint();
            }
        }

        async void RunOperation(string label, Func<IProgress<UniLfsProgress>, CancellationToken, Task<UniLfsOpResult>> operation, bool refreshAssets)
        {
            if (_busy) return;
            _busy = true;
            _busyLabel = label;
            // Without this the bar briefly shows the previous run's numbers.
            _progress = default(UniLfsProgress);
            _cts = new CancellationTokenSource();
            int progressId = Progress.Start("UniLFS " + label);
            Progress.RegisterCancelCallback(progressId, () =>
            {
                if (_cts != null) _cts.Cancel();
                return true;
            });
            try
            {
                var result = await operation(UiProgress(progressId), _cts.Token);
                _lastMessage = Summarize(label, result);
                if (result.HasErrors)
                    Debug.LogWarning("UniLFS " + label + ": " + _lastMessage + "\n- " + string.Join("\n- ", result.Errors));
                else
                    Debug.Log("UniLFS " + label + ": " + _lastMessage);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);
            }
            catch (OperationCanceledException)
            {
                _lastMessage = label + " cancelled.";
                Progress.Finish(progressId, Progress.Status.Canceled);
            }
            catch (UniLfsBusyException e)
            {
                _lastMessage = e.Message;
                Progress.Finish(progressId, Progress.Status.Canceled);
            }
            catch (Exception e)
            {
                _lastMessage = label + " failed: " + e.Message;
                Debug.LogException(e);
                Progress.Finish(progressId, Progress.Status.Failed);
            }
            finally
            {
                try
                {
                    if (refreshAssets) AssetDatabase.Refresh();
                    // Keep the bar live through the post-operation re-hash too;
                    // leaving it parked at 100% made the window look hung on
                    // projects where a cold status check takes a while.
                    _busyLabel = "Refreshing";
                    _progress = default(UniLfsProgress);
                    _statuses = await UniLfsCore.StatusAsync(UiProgress());
                }
                catch (Exception)
                {
                    // Most likely another operation grabbed the lock in between;
                    // OnGUI retries once it frees.
                    _refreshPending = true;
                }
                _busy = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                Repaint();
            }
        }

        IProgress<UniLfsProgress> UiProgress(int progressId = -1)
        {
            return new Progress<UniLfsProgress>(p =>
            {
                _progress = p;
                if (progressId >= 0) Progress.Report(progressId, p.Fraction, p.Label);
                Repaint();
            });
        }

        /// <summary>
        /// Reports what storage answered. The file list already shows the
        /// outcome per file, so the footer only carries the counts — but a blob
        /// the manifest references and storage does not have is worth a Console
        /// warning with the names, since it means a commit is about to reference
        /// something nobody can pull.
        /// </summary>
        void ReportVerify(UniLfsStatusReport report)
        {
            var parts = new List<string>();
            if (report.Confirmed > 0) parts.Add(report.Confirmed + " blob(s) confirmed in storage");
            if (report.MissingRemote.Count > 0) parts.Add(report.MissingRemote.Count + " file(s) missing from storage (run Push)");
            if (report.Failures.Count > 0) parts.Add(report.Failures.Count + " blob(s) could not be checked");
            if (parts.Count == 0) parts.Add("nothing to check");
            _lastMessage = "Verify: " + string.Join(", ", parts) + ".";

            if (report.MissingRemote.Count > 0)
                Debug.LogWarning("UniLFS Verify: storage does not have the blob for these tracked files; run Push before committing the manifest.\n- "
                    + string.Join("\n- ", report.MissingRemote));
            if (report.Failures.Count > 0)
                Debug.LogWarning("UniLFS Verify: some blobs could not be checked, so their state is unchanged.\n- "
                    + string.Join("\n- ", report.Failures));
        }

        /// <summary>
        /// A staged file that nothing ignores is the one failure mode this
        /// split introduces, so it gets said out loud rather than counted: it
        /// is not in the manifest, so the committed .gitignore does not cover
        /// it, and there was no checkout to write .git/info/exclude in.
        /// </summary>
        void ReportNotIgnored(List<string> notIgnored)
        {
            if (notIgnored == null || notIgnored.Count == 0) return;
            _lastMessage = notIgnored.Count + " staged file(s) are not hidden from git - see the Console.";
            Debug.LogWarning("UniLFS: " + notIgnored.Count + " staged file(s) are not covered by any ignore rule, because this project"
                + " is not in a git checkout UniLFS could write .git/info/exclude in. Until you Push them (which puts them in"
                + " unilfs.manifest.json and .gitignore), 'git add -A' would commit them for real:\n- "
                + string.Join("\n- ", notIgnored.ToArray()));
        }

        static string Summarize(string label, UniLfsOpResult r)
        {
            var parts = new List<string>();
            if (r.Uploaded > 0) parts.Add("uploaded " + r.Uploaded);
            if (r.Promoted > 0) parts.Add(r.Promoted + " now in the manifest");
            if (r.Downloaded > 0) parts.Add("downloaded " + r.Downloaded);
            if (r.Skipped > 0) parts.Add(r.Skipped + " up to date");
            if (r.MissingLocal.Count > 0) parts.Add(r.MissingLocal.Count + " missing locally (not pushed)");
            if (r.KeptModified.Count > 0) parts.Add(r.KeptModified.Count + " locally modified kept (use Restore Modified to overwrite)");
            if (r.Outdated.Count > 0) parts.Add(r.Outdated.Count + " outdated, not pushed (run Pull first)");
            if (r.Conflicted.Count > 0) parts.Add(r.Conflicted.Count + " conflicting, left alone");
            if (r.Errors.Count > 0) parts.Add(r.Errors.Count + " error(s)");
            if (parts.Count == 0) parts.Add("nothing to do");
            return label + ": " + string.Join(", ", parts) + ".";
        }
    }
}
