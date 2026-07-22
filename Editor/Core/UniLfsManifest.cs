using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UniLFS.Editor
{
    [Serializable]
    public class UniLfsManifestFile
    {
        public string path;
        public string hash;
        public long size;
        /// <summary>
        /// The asset's Unity GUID, copied from its .meta at track time.
        ///
        /// Tracked files are gitignored, so a clone has the .meta but not the
        /// asset - and Unity discards a .meta it cannot match to an asset,
        /// taking the GUID (and every reference to it) with it. Recording the
        /// GUID next to the hash means it can be put back. Empty for entries
        /// written by UniLFS 0.3.1 and earlier.
        /// </summary>
        public string guid;
    }

    /// <summary>
    /// The manifest is the only UniLFS file that gets committed to git. It maps
    /// project-relative paths to SHA-256 content hashes. The writer emits one
    /// line per file, sorted by path, so diffs and merges stay small.
    /// </summary>
    [Serializable]
    public class UniLfsManifest
    {
        public int version = 1;
        public List<UniLfsManifestFile> files = new List<UniLfsManifestFile>();

        public static UniLfsManifest Load(string manifestPath)
        {
            if (!File.Exists(manifestPath)) return new UniLfsManifest();
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            UniLfsManifest manifest = null;
            try
            {
                manifest = JsonUtility.FromJson<UniLfsManifest>(json);
            }
            catch (Exception e)
            {
                throw new InvalidDataException("UniLFS: failed to parse " + manifestPath + ": " + e.Message, e);
            }
            if (manifest == null) manifest = new UniLfsManifest();
            if (manifest.files == null) manifest.files = new List<UniLfsManifestFile>();
            manifest.files.RemoveAll(f => f == null || string.IsNullOrEmpty(f.path));
            manifest.Sort();
            return manifest;
        }

        public void Save(string manifestPath)
        {
            var tmp = manifestPath + ".tmp";
            File.WriteAllText(tmp, ToJsonString(), new UTF8Encoding(false));
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            File.Move(tmp, manifestPath);
        }

        public void Sort()
        {
            files.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
        }

        public UniLfsManifestFile Find(string projectRelativePath)
        {
            return files.Find(f => f.path == projectRelativePath);
        }

        public UniLfsManifestFile Upsert(string projectRelativePath, string hash, long size)
        {
            var entry = Find(projectRelativePath);
            if (entry == null)
            {
                entry = new UniLfsManifestFile { path = projectRelativePath };
                files.Add(entry);
            }
            entry.hash = hash;
            entry.size = size;
            return entry;
        }

        public bool Remove(string projectRelativePath)
        {
            return files.RemoveAll(f => f.path == projectRelativePath) > 0;
        }

        public string ToJsonString()
        {
            Sort();
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"version\": ").Append(version).Append(",\n");
            sb.Append("  \"files\": [");
            for (int i = 0; i < files.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                var f = files[i];
                sb.Append("    {\"path\": ").Append(UniLfsJsonUtil.Quote(f.path))
                  .Append(", \"hash\": ").Append(UniLfsJsonUtil.Quote(f.hash))
                  .Append(", \"size\": ").Append(f.size);
                // Omitted rather than written empty, so manifests from before
                // GUIDs were recorded round-trip unchanged.
                if (!string.IsNullOrEmpty(f.guid))
                    sb.Append(", \"guid\": ").Append(UniLfsJsonUtil.Quote(f.guid));
                sb.Append("}");
            }
            sb.Append(files.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
