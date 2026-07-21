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
                  .Append(", \"size\": ").Append(f.size).Append("}");
            }
            sb.Append(files.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
