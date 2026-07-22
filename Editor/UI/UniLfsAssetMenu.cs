using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    public static class UniLfsAssetMenu
    {
        const string TrackMenu = "Assets/UniLFS/Track Selected";
        const string UntrackMenu = "Assets/UniLFS/Untrack Selected";

        [MenuItem(TrackMenu, true)]
        static bool ValidateTrack()
        {
            return Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0;
        }

        [MenuItem(TrackMenu, false, 1200)]
        static void TrackMenuItem()
        {
            TrackSelection(null);
        }

        [MenuItem(UntrackMenu, true)]
        static bool ValidateUntrack()
        {
            return Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0;
        }

        [MenuItem(UntrackMenu, false, 1201)]
        static void UntrackMenuItem()
        {
            UntrackSelection(null);
        }

        public static async void TrackSelection(Action onDone)
        {
            var files = CollectSelectedFiles();
            if (files.Count == 0)
            {
                EditorUtility.DisplayDialog("UniLFS", "Select files or folders in the Project window first.", "OK");
                return;
            }

            long totalSize = 0;
            foreach (var f in files)
            {
                var info = new FileInfo(UniLfsPaths.ToAbsolute(f));
                if (info.Exists) totalSize += info.Length;
            }
            if (!EditorUtility.DisplayDialog("UniLFS - Track",
                "Track " + files.Count + " file(s) (" + EditorUtility.FormatBytes(totalSize) + ") with UniLFS?\n\n"
                + "They will be recorded in unilfs.manifest.json and added to .gitignore. Their .meta files stay in git. Run Push afterwards to upload.",
                "Track", "Cancel"))
                return;

            int progressId = Progress.Start("UniLFS Track");
            try
            {
                var result = await UniLfsCore.TrackAsync(files, new Progress<UniLfsProgress>(p =>
                    Progress.Report(progressId, p.Fraction, p.Label)), CancellationToken.None);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);

                string message = "UniLFS: tracked " + result.TrackedNew + " new file(s), updated " + result.TrackedUpdated
                    + ", unchanged " + result.Skipped + ". Press Push in Window > UniLFS to upload.";
                if (result.Outdated.Count > 0)
                    Debug.LogWarning("UniLFS: left " + result.Outdated.Count + " file(s) alone - the manifest already has a newer version than the copy here, "
                        + "and re-tracking would have replaced it with this older one. Run Pull first:\n- "
                        + string.Join("\n- ", result.Outdated.ToArray()));
                if (result.Conflicted.Count > 0)
                    Debug.LogWarning("UniLFS: " + result.Conflicted.Count + " file(s) had changed both here and in the manifest since this project last synced. "
                        + "Tracking them resolved that in favour of the local copy - the version the manifest named is no longer referenced:\n- "
                        + string.Join("\n- ", result.Conflicted.ToArray())
                        + "\nPress Push to upload them. To take the manifest's version instead, use Window > UniLFS > Restore Modified.");
                if (result.HasErrors)
                    Debug.LogWarning(message + "\nErrors:\n- " + string.Join("\n- ", result.Errors));
                else
                    Debug.Log(message);

                string hint = UniLfsCore.GitRemoveHint(result.NewlyTracked);
                if (hint != null) Debug.Log("UniLFS: " + hint);
            }
            catch (UniLfsBusyException e)
            {
                Progress.Finish(progressId, Progress.Status.Canceled);
                EditorUtility.DisplayDialog("UniLFS", e.Message, "OK");
            }
            catch (Exception e)
            {
                Progress.Finish(progressId, Progress.Status.Failed);
                Debug.LogException(e);
            }
            finally
            {
                if (onDone != null) onDone();
            }
        }

        public static void UntrackSelection(Action onDone)
        {
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var selected = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (AssetDatabase.IsValidFolder(assetPath))
                    selected.AddRange(manifest.files
                        .Where(f => f.path.StartsWith(assetPath + "/", StringComparison.Ordinal))
                        .Select(f => f.path));
                else
                    selected.Add(UniLfsPaths.Normalize(assetPath));
            }
            var tracked = selected.Distinct().Where(p => manifest.Find(p) != null).ToList();
            if (tracked.Count == 0)
            {
                EditorUtility.DisplayDialog("UniLFS", "The selection contains no UniLFS-tracked files.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("UniLFS - Untrack",
                "Untrack " + tracked.Count + " file(s)?\n\nFiles stay on disk but will no longer be managed by UniLFS. Add them back to git yourself if needed.",
                "Untrack", "Cancel"))
                return;

            try
            {
                var result = UniLfsCore.Untrack(tracked);
                Debug.Log("UniLFS: untracked " + result.Untracked + " file(s). They remain on disk; run 'git add <file>' if you want git to manage them again.");
            }
            catch (UniLfsBusyException e)
            {
                EditorUtility.DisplayDialog("UniLFS", e.Message, "OK");
                return;
            }
            if (onDone != null) onDone();
        }

        static List<string> CollectSelectedFiles()
        {
            var files = new List<string>();
            if (Selection.assetGUIDs == null) return files;
            foreach (var guid in Selection.assetGUIDs)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (AssetDatabase.IsValidFolder(assetPath))
                {
                    string absDir = UniLfsPaths.ToAbsolute(assetPath);
                    if (!Directory.Exists(absDir)) continue;
                    foreach (var file in Directory.GetFiles(absDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = UniLfsPaths.ToProjectRelative(file);
                        if (IsCandidate(rel)) files.Add(rel);
                    }
                }
                else
                {
                    string rel = UniLfsPaths.Normalize(assetPath);
                    if (File.Exists(UniLfsPaths.ToAbsolute(rel)) && IsCandidate(rel)) files.Add(rel);
                }
            }
            return files.Distinct().ToList();
        }

        static bool IsCandidate(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return false;
            if (projectRelative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(projectRelative);
            if (name.StartsWith(".", StringComparison.Ordinal)) return false;
            string reason;
            return UniLfsPaths.IsTrackablePath(projectRelative, out reason);
        }
    }
}
