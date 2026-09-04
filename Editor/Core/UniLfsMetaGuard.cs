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
    /// 1. Ask git for the .meta files it has and the working tree does not
    ///    (<see cref="UniLfsGitRestore"/>). The .meta is a committed file, so on
    ///    a fresh clone the one Unity just deleted is still in the index — import
    ///    settings and all. Getting it back is both the right GUID and the right
    ///    settings, and it leaves the working tree clean.
    /// 2. Recreate whatever is still missing from the GUID recorded in the
    ///    manifest. This is what actually saves references when git cannot help
    ///    (no git, a .meta never committed). The recreated file carries the GUID
    ///    but not the original import settings - hence the warning when this
    ///    happens, where step 1 only logs.
    /// 3. Write a placeholder at the asset path, so the .meta stops being an
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
            /// <summary>.meta files git handed back, with their import settings.</summary>
            public int MetaFilesRestoredFromGit;
            /// <summary>.meta files rebuilt from the manifest GUID, losing their import settings.</summary>
            public int MetaFilesRebuilt;
            public List<string> MetaMissingNoGuid = new List<string>();
            public List<string> GuidDrift = new List<string>();
            /// <summary>Manifest entries refused because they do not name a path inside the project.</summary>
            public List<string> RejectedPaths = new List<string>();
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

            // Two passes, because the .meta files come back in one git call
            // rather than one per entry: a project tracking hundreds of files
            // would otherwise launch hundreds of processes at editor start.
            var guarded = new List<Guarded>();
            var lostMetaPaths = new List<string>();
            foreach (var f in manifest.files)
            {
                string abs = UniLfsPaths.ToAbsolute(f.path);

                // The manifest is committed, hand-editable and merge-resolved,
                // and this runs unattended at editor start. An entry that
                // escapes the project - ".." segments, an absolute path - would
                // otherwise have us create files outside it with nobody asking
                // for anything. ToAbsolute only concatenates, and the trackable
                // check has no opinion on "..", so both are needed.
                string reason;
                if (!UniLfsPaths.IsTrackablePath(f.path, out reason) ||
                    UniLfsPaths.ToProjectRelative(abs) == null)
                {
                    report.RejectedPaths.Add(f.path);
                    continue;
                }

                var entry = new Guarded
                {
                    Entry = f,
                    Abs = abs,
                    MetaPath = UniLfsMetaFile.PathFor(abs),
                    // Real content is here: nothing to protect.
                    ContentPresent = File.Exists(abs) && !UniLfsPlaceholder.IsPlaceholder(abs),
                };
                guarded.Add(entry);

                if (!entry.ContentPresent && !File.Exists(entry.MetaPath))
                    lostMetaPaths.Add(UniLfsMetaFile.PathFor(f.path));
            }

            // Before anything is rebuilt: WriteMinimal no-ops on a .meta that
            // exists, so whatever git hands back wins, which is the point - it
            // is the file the rest of the project was authored against.
            report.MetaFilesRestoredFromGit =
                UniLfsGitRestore.Restore(UniLfsPaths.ProjectRoot, lostMetaPaths).Count;

            foreach (var entry in guarded)
            {
                var f = entry.Entry;

                // Checked before anything branches on whether the content is
                // here: a tracked file that is missing *and* whose .meta already
                // carries the wrong GUID is exactly the damage this is meant to
                // surface, and nothing downstream would. Pull's WriteMinimal
                // no-ops on an existing .meta, so the asset would come back
                // under that wrong GUID in silence. Read after the restore, so
                // a .meta git just brought back is the one being judged.
                string onDisk = UniLfsMetaFile.ReadGuid(entry.MetaPath);
                if (UniLfsMetaFile.IsValidGuid(f.guid) && onDisk != null && onDisk != f.guid)
                    report.GuidDrift.Add(f.path);

                if (entry.ContentPresent) continue;

                if (!File.Exists(entry.MetaPath))
                {
                    if (UniLfsMetaFile.IsValidGuid(f.guid))
                    {
                        if (UniLfsMetaFile.WriteMinimal(entry.MetaPath, f.guid))
                            report.MetaFilesRebuilt++;
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

                if (!File.Exists(entry.Abs) && UniLfsPlaceholder.Write(entry.Abs, f.hash, f.size))
                    report.PlaceholdersWritten++;
            }

            return report;
        }

        /// <summary>A manifest entry that named a path inside the project, with what the first pass already looked up.</summary>
        class Guarded
        {
            public UniLfsManifestFile Entry;
            public string Abs;
            public string MetaPath;
            public bool ContentPresent;
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

            if (report.MetaFilesRestoredFromGit > 0)
            {
                // A log line rather than a warning: nothing was lost. Unity
                // discarded them, git had them, they are back as they were
                // committed - import settings included - and the working tree
                // is clean again.
                Debug.Log("UniLFS: Unity had discarded the .meta file of " + report.MetaFilesRestoredFromGit
                    + " tracked file(s); they were restored from git, with their import settings, so nothing"
                    + " needs re-setting and the working tree is unchanged.");
            }

            if (report.MetaFilesRebuilt > 0)
            {
                // Worth a warning rather than a log line: the GUID is back, so
                // references resolve, but whatever import settings the .meta
                // carried are gone and only git still has them.
                Debug.LogWarning("UniLFS: Unity had already discarded the .meta file of "
                    + report.MetaFilesRebuilt + " tracked file(s), and git could not hand them back (no git on"
                    + " PATH, or they were never committed); they were rebuilt from the GUIDs in the"
                    + " manifest, so references still resolve. Import settings were not part of that and are"
                    + " back to their defaults - restore those .meta files from git (git checkout -- <path>.meta)"
                    + " if they had any.");
            }

            if (report.RejectedPaths.Count > 0)
            {
                Debug.LogError("UniLFS: " + report.RejectedPaths.Count
                    + " manifest entry/entries do not name a path inside this project and were ignored."
                    + " Check unilfs.manifest.json - this is what a bad merge or a hand-edit looks like:\n- "
                    + string.Join("\n- ", report.RejectedPaths.ToArray()));
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
