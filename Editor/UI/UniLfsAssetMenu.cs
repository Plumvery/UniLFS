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
                + "They will be staged on this machine and hidden from git. Push uploads them and records them in "
                + "unilfs.manifest.json, which is what the rest of the team sees. Their .meta files stay in git.",
                "Track", "Cancel"))
                return;

            int progressId = Progress.Start("UniLFS Track");
            try
            {
                var result = await UniLfsCore.TrackAsync(files, new Progress<UniLfsProgress>(p =>
                    Progress.Report(progressId, p.Fraction, p.Label)), CancellationToken.None);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);

                string message = "UniLFS: staged " + result.TrackedNew + " new file(s), " + result.Skipped
                    + " already tracked. Press Push in Window > UniLFS to upload them and record them in the manifest.";
                if (result.Outdated.Count > 0)
                    Debug.LogWarning("UniLFS: " + result.Outdated.Count + " file(s) are already tracked and the manifest has a newer version "
                        + "than the copy here, so there was nothing to track. Run Pull to get it:\n- "
                        + string.Join("\n- ", result.Outdated.ToArray()));
                if (result.Conflicted.Count > 0)
                    Debug.LogWarning("UniLFS: " + result.Conflicted.Count + " file(s) are already tracked and their local content and the manifest "
                        + "disagree with no shared history to go on, so neither version wins automatically:\n- "
                        + string.Join("\n- ", result.Conflicted.ToArray())
                        + "\nKeep this machine's copy with Window > UniLFS > Keep Mine followed by Push, "
                        + "or take the manifest's with Restore Modified.");
                if (result.NotIgnored.Count > 0)
                    Debug.LogWarning("UniLFS: " + result.NotIgnored.Count + " staged file(s) could not be hidden from git - this project is not in a "
                        + "git checkout UniLFS could write .git/info/exclude in. Until they are pushed, 'git add -A' would commit them:\n- "
                        + string.Join("\n- ", result.NotIgnored.ToArray()));
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
            // Staged files are tracked too - they are simply tracked here only -
            // so untracking has to reach them, or a file staged by mistake could
            // never be un-staged.
            var staged = UniLfsStagedPaths.Load(UniLfsPaths.StagedPath);
            var all = manifest.files.Select(f => f.path).Concat(staged.paths).Distinct().ToList();
            var selected = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (AssetDatabase.IsValidFolder(assetPath))
                    selected.AddRange(all.Where(p => p.StartsWith(assetPath + "/", StringComparison.Ordinal)));
                else
                    selected.Add(UniLfsPaths.Normalize(assetPath));
            }
            var tracked = selected.Distinct().Where(p => all.Contains(p)).ToList();
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
