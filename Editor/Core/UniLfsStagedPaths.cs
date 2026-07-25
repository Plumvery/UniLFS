using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// What this machine has asked UniLFS to track and has not pushed yet.
    ///
    /// The manifest is committed, and an entry in it says "this content is in
    /// storage" — Push is the only thing that can truthfully write one. Track
    /// runs long before that, so what it records goes here instead: a local
    /// file, holding intent rather than facts.
    ///
    /// Deliberately no hashes, sizes or GUIDs. Push re-hashes the file and
    /// re-reads its .meta at push time, so anything else kept here would be a
    /// second copy of the truth with its own way of going stale — which is the
    /// bug this whole split exists to remove, one file over.
    ///
    /// Lives next to the manifest rather than under <c>Library/</c>: that
    /// folder is documented as safe to delete, and this is the one piece of
    /// per-machine state nothing can recompute.
    /// </summary>
    [Serializable]
    public class UniLfsStagedPaths
    {
        public int version = 1;

        /// <summary>Paths waiting for their first Push.</summary>
        public List<string> paths = new List<string>();

        /// <summary>
        /// Paths whose local content should win over what the manifest names,
        /// recorded when someone answers a conflict with "keep mine". Push
        /// consumes it: without an explicit decision on record, neither side of
        /// a conflict gets picked automatically.
        /// </summary>
        public List<string> resolveLocal = new List<string>();

        public static UniLfsStagedPaths Load(string stagedPath)
        {
            if (!File.Exists(stagedPath)) return new UniLfsStagedPaths();
            UniLfsStagedPaths staged = null;
            try
            {
                staged = JsonUtility.FromJson<UniLfsStagedPaths>(File.ReadAllText(stagedPath, Encoding.UTF8));
            }
            catch (Exception)
            {
                // Unreadable staging costs the intent to track those paths, and
                // re-running Track restores it. Throwing here would instead stop
                // Push from working at all, which is worse for the files that
                // are already in the manifest.
                staged = null;
            }
            if (staged == null) staged = new UniLfsStagedPaths();
            if (staged.paths == null) staged.paths = new List<string>();
            if (staged.resolveLocal == null) staged.resolveLocal = new List<string>();
            staged.paths.RemoveAll(string.IsNullOrEmpty);
            staged.resolveLocal.RemoveAll(string.IsNullOrEmpty);
            staged.Sort();
            return staged;
        }

        /// <summary>
        /// Writes the file, or deletes it once nothing is staged — the project
        /// root only carries this file while it has something to say.
        /// </summary>
        public void Save(string stagedPath)
        {
            if (paths.Count == 0 && resolveLocal.Count == 0)
            {
                try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
                catch (Exception) { }
                return;
            }
            var tmp = stagedPath + ".tmp";
            File.WriteAllText(tmp, ToJsonString(), new UTF8Encoding(false));
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
            File.Move(tmp, stagedPath);
        }

        public void Sort()
        {
            paths.Sort(StringComparer.Ordinal);
            resolveLocal.Sort(StringComparer.Ordinal);
        }

        public bool Contains(string projectRelativePath)
        {
            return paths.Contains(projectRelativePath);
        }

        public bool ResolvesLocal(string projectRelativePath)
        {
            return resolveLocal.Contains(projectRelativePath);
        }

        /// <summary>Returns false when the path was already staged.</summary>
        public bool Add(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath) || paths.Contains(projectRelativePath)) return false;
            paths.Add(projectRelativePath);
            return true;
        }

        /// <summary>Returns false when the path was already marked.</summary>
        public bool KeepLocal(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath) || resolveLocal.Contains(projectRelativePath)) return false;
            resolveLocal.Add(projectRelativePath);
            return true;
        }

        /// <summary>
        /// Drops every trace of a path: what Push does once the manifest names
        /// it, and what Untrack does when it stops being tracked at all.
        /// </summary>
        public bool Remove(string projectRelativePath)
        {
            bool removed = paths.Remove(projectRelativePath);
            return resolveLocal.Remove(projectRelativePath) || removed;
        }

        public string ToJsonString()
        {
            Sort();
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"version\": ").Append(version).Append(",\n");
            Append(sb, "paths", paths, true);
            Append(sb, "resolveLocal", resolveLocal, false);
            sb.Append("}\n");
            return sb.ToString();
        }

        static void Append(StringBuilder sb, string name, List<string> values, bool comma)
        {
            sb.Append("  \"").Append(name).Append("\": [");
            for (int i = 0; i < values.Count; i++)
                sb.Append(i == 0 ? "\n    " : ",\n    ").Append(UniLfsJsonUtil.Quote(values[i]));
            sb.Append(values.Count > 0 ? "\n  ]" : "]").Append(comma ? ",\n" : "\n");
        }
    }
}
