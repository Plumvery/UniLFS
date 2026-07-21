using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Per-machine record of which blob hashes are known to exist in remote
    /// storage.
    ///
    /// Without it the window can only compare a file against the manifest,
    /// which says nothing about whether the blob was ever uploaded: a freshly
    /// tracked file matches the manifest immediately and looked "up to date"
    /// while still existing nowhere but this disk.
    ///
    /// Entries are added when we have proof — a blob we just uploaded, one the
    /// remote reported as already present during Push, one we just downloaded
    /// during Pull, or one confirmed by Verify — and retracted when a check
    /// answers that the blob is not there after all. Safe to delete: files fall
    /// back to "not pushed" until the next Push or Pull confirms them again.
    ///
    /// The file name carries a provider fingerprint, so repointing the project
    /// at a different bucket does not inherit the old bucket's confirmations
    /// (and pointing back restores them).
    /// </summary>
    public class UniLfsRemoteBlobCache
    {
        [Serializable]
        class CacheFile
        {
            public int version = 1;
            public List<string> blobs = new List<string>();
        }

        readonly string _path;
        readonly object _lock = new object();
        HashSet<string> _blobs = new HashSet<string>();
        bool _dirty;

        public UniLfsRemoteBlobCache(string path)
        {
            _path = path;
            Load();
        }

        public static UniLfsRemoteBlobCache Load(UniLfsSettings settings)
        {
            return new UniLfsRemoteBlobCache(UniLfsPaths.Combine(
                UniLfsPaths.LibraryDir, "remote-" + Fingerprint(settings) + ".json"));
        }

        /// <summary>
        /// Identifies the storage location the confirmations belong to. Region
        /// is left out on purpose: it is a routing detail, not a different
        /// bucket.
        /// </summary>
        public static string Fingerprint(UniLfsSettings settings)
        {
            string identity = settings.ProviderKind == UniLfsProviderKind.GoogleDrive
                ? "drive|" + settings.driveFolderId
                : "s3|" + settings.s3Endpoint + "|" + settings.s3Bucket + "|" + settings.s3Prefix;
            return UniLfsHasher.Sha256OfString(identity).Substring(0, 12);
        }

        void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var data = JsonUtility.FromJson<CacheFile>(File.ReadAllText(_path, Encoding.UTF8));
                if (data == null || data.blobs == null) return;
                foreach (var h in data.blobs)
                    if (!string.IsNullOrEmpty(h)) _blobs.Add(h);
            }
            catch (Exception)
            {
                _blobs = new HashSet<string>();
            }
        }

        public bool Contains(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            lock (_lock) return _blobs.Contains(hash);
        }

        public void Add(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            lock (_lock)
                if (_blobs.Add(hash)) _dirty = true;
        }

        public void AddRange(IEnumerable<string> hashes)
        {
            if (hashes == null) return;
            lock (_lock)
                foreach (var h in hashes)
                    if (!string.IsNullOrEmpty(h) && _blobs.Add(h)) _dirty = true;
        }

        /// <summary>
        /// Retracts a confirmation, for when storage answers that it does not
        /// have the blob after all (someone emptied the bucket, or the blob only
        /// ever reached a different one). Call this only on a definitive "no" —
        /// a check that failed with a network error proves nothing, and
        /// forgetting on those would flip files to "not pushed" every time the
        /// connection hiccups.
        /// </summary>
        public void Remove(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            lock (_lock)
                if (_blobs.Remove(hash)) _dirty = true;
        }

        /// <summary>
        /// Drops confirmations for blobs the manifest no longer references, so
        /// the file does not grow without bound as assets churn.
        /// </summary>
        public void RetainOnly(IEnumerable<string> hashes)
        {
            var keep = new HashSet<string>();
            foreach (var h in hashes)
                if (!string.IsNullOrEmpty(h)) keep.Add(h);
            lock (_lock)
            {
                if (_blobs.Count == 0) return;
                var pruned = new HashSet<string>();
                foreach (var h in _blobs)
                    if (keep.Contains(h)) pruned.Add(h);
                if (pruned.Count == _blobs.Count) return;
                _blobs = pruned;
                _dirty = true;
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                if (!_dirty) return;
                var data = new CacheFile();
                data.blobs.AddRange(_blobs);
                data.blobs.Sort(StringComparer.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonUtility.ToJson(data), new UTF8Encoding(false));
                _dirty = false;
            }
        }
    }
}
