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
        /// <summary>
        /// How many confirmations for blobs outside the current manifest to
        /// keep. Hashes come back — a revert, a branch switch, or a teammate's
        /// change being undone all point the manifest at something it named
        /// before — and each confirmation costs a network round trip to earn
        /// again. Keeping a window of them is what stops a file that never left
        /// storage from reading as "not pushed" the moment the manifest moves.
        /// At 64 hex characters apiece this caps the file at roughly 280 KB.
        /// </summary>
        public const int MaxUnreferenced = 4096;

        [Serializable]
        class CacheFile
        {
            public int version = 1;
            public List<string> blobs = new List<string>();
        }

        readonly string _path;
        readonly object _lock = new object();
        /// <summary>
        /// Confirmed hashes in the order they were first confirmed. Order is
        /// what makes pruning drop the longest-standing entries rather than
        /// arbitrary ones; re-confirming a hash does not move it, which is fine
        /// for a heuristic about what to keep. <see cref="_blobs"/> mirrors this
        /// for lookup.
        /// </summary>
        List<string> _order = new List<string>();
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
                    if (!string.IsNullOrEmpty(h) && _blobs.Add(h)) _order.Add(h);
            }
            catch (Exception)
            {
                _order = new List<string>();
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
            lock (_lock) AddUnlocked(hash);
        }

        public void AddRange(IEnumerable<string> hashes)
        {
            if (hashes == null) return;
            lock (_lock)
                foreach (var h in hashes)
                    if (!string.IsNullOrEmpty(h)) AddUnlocked(h);
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
            {
                if (!_blobs.Remove(hash)) return;
                _order.Remove(hash);
                _dirty = true;
            }
        }

        /// <summary>
        /// Bounds the file's growth while keeping proof that is still true.
        /// Everything the manifest references survives, and so do the most
        /// recent <see cref="MaxUnreferenced"/> confirmations it does not.
        ///
        /// Dropping every unreferenced hash on sight — which is what this used
        /// to do — threw away answers that were still correct: a manifest that
        /// moves off a hash today may name it again tomorrow, and until some
        /// later Refresh re-earned the confirmation the file sat there reading
        /// "not pushed" despite never having left storage.
        /// </summary>
        public void Prune(IEnumerable<string> manifestHashes)
        {
            var keep = new HashSet<string>();
            if (manifestHashes != null)
                foreach (var h in manifestHashes)
                    if (!string.IsNullOrEmpty(h)) keep.Add(h);
            lock (_lock)
            {
                int unreferenced = 0;
                foreach (var h in _order)
                    if (!keep.Contains(h)) unreferenced++;
                if (unreferenced <= MaxUnreferenced) return;

                int drop = unreferenced - MaxUnreferenced;
                var kept = new List<string>(_order.Count - drop);
                foreach (var h in _order)
                {
                    // _order runs oldest first, so taking the drops from the
                    // front leaves the most recently added window behind.
                    if (drop > 0 && !keep.Contains(h)) { drop--; continue; }
                    kept.Add(h);
                }
                _order = kept;
                _blobs = new HashSet<string>(kept);
                _dirty = true;
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                if (!_dirty) return;
                var data = new CacheFile();
                // Written in confirmation order, not sorted: the order is what
                // Prune reads to decide what to drop, and it has to survive a
                // reload. Nothing diffs this file - it lives under Library/.
                data.blobs.AddRange(_order);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, JsonUtility.ToJson(data), new UTF8Encoding(false));
                _dirty = false;
            }
        }

        /// <summary>Caller must hold <see cref="_lock"/>.</summary>
        void AddUnlocked(string hash)
        {
            if (!_blobs.Add(hash)) return;
            _order.Add(hash);
            _dirty = true;
        }
    }
}
