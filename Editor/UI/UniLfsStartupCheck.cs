using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Cheap existence check after domain reload: warns when tracked files are
    /// missing locally (e.g. right after cloning), without hashing anything.
    /// </summary>
    static class UniLfsStartupCheck
    {
        [InitializeOnLoadMethod]
        static void Init()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += Check;
        }

        static void Check()
        {
            if (!File.Exists(UniLfsPaths.ManifestPath)) return;
            UniLfsManifest manifest;
            try
            {
                manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            }
            catch (System.Exception)
            {
                return;
            }
            int missing = manifest.files.Count(f => !File.Exists(UniLfsPaths.ToAbsolute(f.path)));
            if (missing > 0)
                Debug.LogWarning("UniLFS: " + missing + " tracked file(s) are missing locally. Open Window > UniLFS and press Pull. "
                    + "(CI: Unity -batchmode -quit -executeMethod UniLFS.Editor.UniLfsCli.Pull)");
        }
    }
}
