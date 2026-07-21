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

        void OnEnable()
        {
            if (File.Exists(UniLfsPaths.ManifestPath))
                RefreshStatus();
        }

        void OnGUI()
        {
            DrawToolbar();
            DrawHeader();
            if (_busy) DrawProgress();
            DrawList();
            DrawFooter();
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    RefreshStatus();
                if (GUILayout.Button(new GUIContent("Push", "Upload changed/new blobs and update the manifest"), EditorStyles.toolbarButton, GUILayout.Width(50)))
                    RunOperation("Push", (p, ct) => UniLfsCore.PushAsync(p, ct), false);
                if (GUILayout.Button(new GUIContent("Pull", "Download files that are missing locally"), EditorStyles.toolbarButton, GUILayout.Width(50)))
                    RunOperation("Pull", (p, ct) => UniLfsCore.PullAsync(false, p, ct), true);
                if (GUILayout.Button(new GUIContent("Restore Modified", "Overwrite locally modified files with the manifest version"), EditorStyles.toolbarButton, GUILayout.Width(110)))
                {
                    if (EditorUtility.DisplayDialog("UniLFS - Restore Modified",
                        "Overwrite locally modified tracked files with the version recorded in the manifest?\nLocal changes will be lost.",
                        "Restore", "Cancel"))
                        RunOperation("Restore", (p, ct) => UniLfsCore.PullAsync(true, p, ct), true);
                }
                GUILayout.Space(12);
                if (GUILayout.Button("Track Selected", EditorStyles.toolbarButton, GUILayout.Width(95)))
                    UniLfsAssetMenu.TrackSelection(RefreshStatus);
                if (GUILayout.Button("Untrack Selected", EditorStyles.toolbarButton, GUILayout.Width(105)))
                    UniLfsAssetMenu.UntrackSelection(RefreshStatus);
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

        void DrawHeader()
        {
            var settings = UniLfsSettings.Load();
            string provider = settings.ProviderKind == UniLfsProviderKind.GoogleDrive
                ? "Google Drive"
                : "S3 compatible (" + (string.IsNullOrEmpty(settings.s3Bucket) ? "not configured" : settings.s3Bucket) + ")";
            EditorGUILayout.LabelField("Provider: " + provider, EditorStyles.miniLabel);
        }

        void DrawProgress()
        {
            var rect = EditorGUILayout.GetControlRect(false, 18);
            float overall = _progress.Total > 0
                ? Mathf.Clamp01((_progress.Done + _progress.ItemProgress) / _progress.Total)
                : 0f;
            string label = _busyLabel;
            if (!string.IsNullOrEmpty(_progress.Phase))
                label = _progress.Phase + " (" + Mathf.Min(_progress.Done + 1, Mathf.Max(_progress.Total, 1)) + "/" + _progress.Total + ") " + _progress.Item;
            EditorGUI.ProgressBar(rect, overall, label);
        }

        void DrawList()
        {
            if (_statuses == null || _statuses.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No files are tracked yet.\n\n" +
                    "1. Configure a storage provider in Project Settings > UniLFS.\n" +
                    "2. Select large assets in the Project window and run Assets > UniLFS > Track Selected.\n" +
                    "3. Press Push to upload them, then commit unilfs.manifest.json and .gitignore.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var s in _statuses)
            {
                EditorGUILayout.BeginHorizontal();
                Color color;
                string badge = StateBadge(s.State, out color);
                var previous = GUI.color;
                GUI.color = color;
                GUILayout.Label("●", GUILayout.Width(16));
                GUI.color = previous;
                GUILayout.Label(new GUIContent(s.File.path, s.File.path + "\nsha256: " + s.File.hash), GUILayout.ExpandWidth(true));
                GUILayout.Label(EditorUtility.FormatBytes(s.File.size), EditorStyles.miniLabel, GUILayout.Width(80));
                GUILayout.Label(badge, EditorStyles.miniLabel, GUILayout.Width(90));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawFooter()
        {
            GUILayout.FlexibleSpace();
            if (_statuses != null && _statuses.Count > 0)
            {
                int upToDate = 0, modified = 0, missing = 0;
                long totalSize = 0;
                foreach (var s in _statuses)
                {
                    totalSize += s.File.size;
                    switch (s.State)
                    {
                        case UniLfsFileState.UpToDate: upToDate++; break;
                        case UniLfsFileState.Modified: modified++; break;
                        case UniLfsFileState.MissingLocal: missing++; break;
                    }
                }
                EditorGUILayout.LabelField(
                    _statuses.Count + " tracked (" + EditorUtility.FormatBytes(totalSize) + ")  |  " +
                    upToDate + " up to date, " + modified + " modified, " + missing + " missing",
                    EditorStyles.miniLabel);
            }
            if (!string.IsNullOrEmpty(_lastMessage))
                EditorGUILayout.LabelField(_lastMessage, EditorStyles.wordWrappedMiniLabel);
        }

        static string StateBadge(UniLfsFileState state, out Color color)
        {
            switch (state)
            {
                case UniLfsFileState.Modified:
                    color = new Color(0.95f, 0.75f, 0.2f);
                    return "modified";
                case UniLfsFileState.MissingLocal:
                    color = new Color(0.95f, 0.35f, 0.3f);
                    return "missing";
                default:
                    color = new Color(0.35f, 0.8f, 0.35f);
                    return "up to date";
            }
        }

        async void RefreshStatus()
        {
            if (_busy) return;
            _busy = true;
            _busyLabel = "Refreshing";
            _cts = new CancellationTokenSource();
            try
            {
                _statuses = await UniLfsCore.StatusAsync(UiProgress(), _cts.Token);
            }
            catch (OperationCanceledException)
            {
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
                    _statuses = await UniLfsCore.StatusAsync();
                }
                catch (Exception)
                {
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
                if (progressId >= 0)
                {
                    float overall = p.Total > 0 ? Mathf.Clamp01((p.Done + p.ItemProgress) / p.Total) : 0f;
                    Progress.Report(progressId, overall, p.Phase + " " + p.Item);
                }
                Repaint();
            });
        }

        static string Summarize(string label, UniLfsOpResult r)
        {
            var parts = new List<string>();
            if (r.Uploaded > 0) parts.Add("uploaded " + r.Uploaded);
            if (r.Downloaded > 0) parts.Add("downloaded " + r.Downloaded);
            if (r.Skipped > 0) parts.Add(r.Skipped + " up to date");
            if (r.MissingLocal.Count > 0) parts.Add(r.MissingLocal.Count + " missing locally (not pushed)");
            if (r.KeptModified.Count > 0) parts.Add(r.KeptModified.Count + " locally modified kept (use Restore Modified to overwrite)");
            if (r.Errors.Count > 0) parts.Add(r.Errors.Count + " error(s)");
            if (parts.Count == 0) parts.Add("nothing to do");
            return label + ": " + string.Join(", ", parts) + ".";
        }
    }
}
