using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Keeps tracked assets from coming back under a new GUID.
    ///
    /// Tracked files are gitignored, so anyone who clones the repository gets
    /// <c>Foo.mp4.meta</c> without <c>Foo.mp4</c>. Unity discards a .meta it
    /// cannot match to an asset and mints a fresh GUID once Pull brings the file
    /// back - breaking every scene, prefab and Addressables entry that
    /// referenced the old one. Auto Pull cannot prevent it: it runs from
    /// <see cref="EditorApplication.delayCall"/>, long after.
    ///
    /// This guard runs from <c>[InitializeOnLoadMethod]</c>, the earliest hook
    /// managed code gets. That is still not early enough: measured on 2022.3,
    /// both from a warm Library and from a deleted one, Unity has already
    /// discarded the orphaned .meta by the time it runs. So the order below is
    /// what makes it work, not the timing:
    ///
    /// 1. Recreate a missing .meta from the GUID recorded in the manifest. This
    ///    is what actually saves references. The recreated file carries the GUID
    ///    but not the original import settings, so a .meta restored from git is
    ///    always the better outcome - hence the warning when this happens.
    /// 2. Write a placeholder at the asset path, so the .meta stops being an
    ///    orphan. Without it every later refresh discards the .meta again; with
    ///    it the entry is stable until Pull overwrites the placeholder with real
    ///    content, under the same GUID.
    ///
    /// Downloading here instead is not an option - it would block startup on
    /// hundreds of megabytes.
    /// </summary>
    static class UniLfsMetaGuard
    {
        const string DriftReportedKey = "UniLFS.MetaGuard.DriftReported";

        internal class GuardReport
        {
            public int PlaceholdersWritten;
            public int MetaFilesRestored;
            public List<string> MetaMissingNoGuid = new List<string>();
            public List<string> GuidDrift = new List<string>();
        }

        [InitializeOnLoadMethod]
        static void Init()
        {
            try
            {
                var report = Run();
                LogReport(report);
            }
            catch (Exception e)
            {
                // Startup must never break because of this. Losing the guard
                // costs GUIDs; throwing here costs the whole editor session.
                Debug.LogWarning("UniLFS: could not protect tracked .meta files: " + e.Message);
            }
        }

        /// <summary>
        /// Runs the guard over every manifest entry. Safe to call repeatedly;
        /// it only ever creates files that are absent.
        /// </summary>
        internal static GuardReport Run()
        {
            var report = new GuardReport();
            if (!File.Exists(UniLfsPaths.ManifestPath)) return report;
            if (UniLfsOperationLock.IsBusy) return report;

            UniLfsManifest manifest;
            try
            {
                manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not read the manifest: " + e.Message);
                return report;
            }

            foreach (var f in manifest.files)
            {
                string abs = UniLfsPaths.ToAbsolute(f.path);
                string metaPath = UniLfsMetaFile.PathFor(abs);
                bool contentPresent = File.Exists(abs) && !UniLfsPlaceholder.IsPlaceholder(abs);

                if (contentPresent)
                {
                    // Nothing to protect, but this is the cheapest place to
                    // notice that the damage already happened on an earlier run.
                    string onDisk = UniLfsMetaFile.ReadGuid(metaPath);
                    if (UniLfsMetaFile.IsValidGuid(f.guid) && onDisk != null && onDisk != f.guid)
                        report.GuidDrift.Add(f.path);
                    continue;
                }

                if (!File.Exists(metaPath))
                {
                    if (UniLfsMetaFile.IsValidGuid(f.guid))
                    {
                        if (UniLfsMetaFile.WriteMinimal(metaPath, f.guid))
                            report.MetaFilesRestored++;
                    }
                    else
                    {
                        // No .meta and nothing recorded: Unity will mint a GUID
                        // for the placeholder and the real file will inherit it.
                        // Stable from here on, but not necessarily what the rest
                        // of the project references.
                        report.MetaMissingNoGuid.Add(f.path);
                    }
                }

                if (!File.Exists(abs) && UniLfsPlaceholder.Write(abs, f.hash, f.size))
                    report.PlaceholdersWritten++;
            }

            return report;
        }

        static void LogReport(GuardReport report)
        {
            if (report.PlaceholdersWritten > 0)
            {
                Debug.Log("UniLFS: stood in for " + report.PlaceholdersWritten
                    + " tracked file(s) that are not on disk, so Unity keeps their .meta GUIDs."
                    + " Run Pull to replace them with the real content."
                    + " Import errors for these paths until then are expected.");
            }

            if (report.MetaFilesRestored > 0)
            {
                // Worth a warning rather than a log line: the GUID is back, so
                // references resolve, but whatever import settings the .meta
                // carried are gone and only git still has them.
                Debug.LogWarning("UniLFS: Unity had already discarded the .meta file of "
                    + report.MetaFilesRestored + " tracked file(s); they were rebuilt from the GUIDs in the"
                    + " manifest, so references still resolve. Import settings were not part of that and are"
                    + " back to their defaults - restore those .meta files from git (git checkout -- <path>.meta)"
                    + " if they had any.");
            }

            if (report.MetaMissingNoGuid.Count > 0)
            {
                Debug.LogWarning("UniLFS: " + report.MetaMissingNoGuid.Count
                    + " tracked file(s) have neither a .meta nor a GUID in the manifest, so Unity will assign new ones."
                    + " Re-run Track on them after Pull to record the GUIDs for everyone else:\n- "
                    + string.Join("\n- ", report.MetaMissingNoGuid.ToArray()));
            }

            if (report.GuidDrift.Count > 0 && SessionState.GetString(DriftReportedKey, "") != "1")
            {
                SessionState.SetString(DriftReportedKey, "1");
                Debug.LogError("UniLFS: " + report.GuidDrift.Count
                    + " tracked file(s) have a .meta GUID that differs from the one recorded in the manifest."
                    + " Unity most likely re-imported them as new assets, which breaks existing scene, prefab and"
                    + " Addressables references. Restore the .meta files from git (git checkout -- <path>.meta)"
                    + " before committing, or re-run Track if the new GUIDs are intentional:\n- "
                    + string.Join("\n- ", report.GuidDrift.ToArray()));
            }
        }
    }
}
