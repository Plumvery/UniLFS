using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Keeps the project in sync without git hooks. Whenever the editor starts
    /// or regains focus (which is what happens right after a git pull in a
    /// terminal or git client), a cheap existence check runs. If the manifest
    /// changed and tracked files are missing, UniLFS warns / asks / pulls
    /// depending on the Auto Pull setting. Each manifest state is handled at
    /// most once per editor session (SessionState survives domain reloads).
    /// </summary>
    static class UniLfsAutoSync
    {
        const string HandledStampKey = "UniLFS.AutoSync.HandledManifestStamp";
        static bool _running;

        [InitializeOnLoadMethod]
        static void Init()
        {
            if (Application.isBatchMode) return;
            EditorApplication.focusChanged += OnFocusChanged;
            EditorApplication.delayCall += Check;
        }

        static void OnFocusChanged(bool focused)
        {
            if (focused) Check();
        }

        static void Check()
        {
            if (_running || EditorApplication.isPlayingOrWillChangePlaymode) return;

            var manifestInfo = new FileInfo(UniLfsPaths.ManifestPath);
            if (!manifestInfo.Exists) return;
            string stamp = manifestInfo.LastWriteTimeUtc.Ticks + ":" + manifestInfo.Length;
            if (SessionState.GetString(HandledStampKey, "") == stamp) return;
            SessionState.SetString(HandledStampKey, stamp);

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
                if (!File.Exists(UniLfsPaths.ToAbsolute(f.path)))
                    missing++;
            if (missing == 0) return;

            switch (UniLfsSettings.Load().AutoPullMode)
            {
                case UniLfsAutoPullMode.Auto:
                    RunPull();
                    break;
                case UniLfsAutoPullMode.Ask:
                    // delayCall: never open a modal dialog from inside the focus event
                    int missingCount = missing;
                    EditorApplication.delayCall += () => Prompt(missingCount);
                    break;
                default:
                    Debug.LogWarning("UniLFS: " + missing + " tracked file(s) are missing locally. Open Window > UniLFS and press Pull. "
                        + "(CI: Unity -batchmode -quit -executeMethod UniLFS.Editor.UniLfsCli.Pull)");
                    break;
            }
        }

        static void Prompt(int missing)
        {
            if (_running) return;
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
            if (_running) return;
            _running = true;
            int progressId = Progress.Start("UniLFS Auto Pull");
            try
            {
                var result = await UniLfsCore.PullAsync(false, new Progress<UniLfsProgress>(p =>
                {
                    float overall = p.Total > 0 ? Mathf.Clamp01((p.Done + p.ItemProgress) / p.Total) : 0f;
                    Progress.Report(progressId, overall, p.Phase + " " + p.Item);
                }), CancellationToken.None);
                Progress.Finish(progressId, result.HasErrors ? Progress.Status.Failed : Progress.Status.Succeeded);
                AssetDatabase.Refresh();
                if (result.HasErrors)
                    Debug.LogError("UniLFS auto pull: downloaded " + result.Downloaded + " file(s), "
                        + result.Errors.Count + " error(s):\n- " + string.Join("\n- ", result.Errors));
                else
                    Debug.Log("UniLFS auto pull: downloaded " + result.Downloaded + " file(s)"
                        + (result.KeptModified.Count > 0 ? ", kept " + result.KeptModified.Count + " locally modified file(s)" : "") + ".");
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
    }
}
