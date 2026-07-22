using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Per-machine record for each tracked file, kept under Library/. Two
    /// things live here, both keyed by project-relative path:
    ///
    /// - A (mtime, size) -> SHA-256 cache, so repeated status checks do not
    ///   re-hash unchanged multi-gigabyte files. Pure optimisation.
    /// - The sync baseline: the manifest hash this machine last agreed with for
    ///   that file, recorded whenever Track, Push or Pull leaves the two in
    ///   sync. This is what lets UniLFS tell "I edited this" from "someone else
    ///   pushed a newer version" — two situations that look identical from the
    ///   hashes alone (local != manifest) and need opposite fixes.
    ///
    /// Deleting the file stays safe. Hashes come back on their own, and a file
    /// whose baseline is gone falls back to the conservative reading (Modified),
    /// so local content is never overwritten on a guess; the baseline restores
    /// itself the next time local and manifest agree.
    /// </summary>
    public class UniLfsStateCache
    {
        [Serializable]
        class Entry
        {
            public string path;
            public long mtimeTicks;
            public long size;
            public string hash;
            /// <summary>
            /// Manifest hash this file was last in sync with. Empty for entries
            /// written by UniLFS 0.3.2 and earlier, and for files this machine
            /// has never pushed or pulled.
            /// </summary>
            public string baseHash;
        }

        [Serializable]
        class CacheFile
        {
            public int version = 1;
            public List<Entry> entries = new List<Entry>();
        }

        readonly string _cachePath;
        readonly object _lock = new object();
        Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        bool _dirty;

        public UniLfsStateCache(string cachePath)
        {
            _cachePath = cachePath;
            Load();
        }

        void Load()
        {
            try
            {
                if (!File.Exists(_cachePath)) return;
                var data = JsonUtility.FromJson<CacheFile>(File.ReadAllText(_cachePath, Encoding.UTF8));
                if (data == null || data.entries == null) return;
                foreach (var e in data.entries)
                    if (e != null && !string.IsNullOrEmpty(e.path))
                        _entries[e.path] = e;
            }
            catch (Exception)
            {
                _entries = new Dictionary<string, Entry>();
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                if (!_dirty) return;
                var data = new CacheFile();
                data.entries.AddRange(_entries.Values);
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                File.WriteAllText(_cachePath, JsonUtility.ToJson(data), new UTF8Encoding(false));
                _dirty = false;
            }
        }

        public async Task<string> GetHashAsync(string projectRelativePath, IProgress<float> progress = null, CancellationToken ct = default(CancellationToken))
        {
            string abs = UniLfsPaths.ToAbsolute(projectRelativePath);
            var info = new FileInfo(abs);
            if (!info.Exists) throw new FileNotFoundException("File not found: " + abs, abs);
            long mtime = info.LastWriteTimeUtc.Ticks;
            long size = info.Length;
            lock (_lock)
            {
                Entry e;
                if (_entries.TryGetValue(projectRelativePath, out e) && e.mtimeTicks == mtime && e.size == size && !string.IsNullOrEmpty(e.hash))
                    return e.hash;
            }
            string hash = await UniLfsHasher.Sha256OfFileAsync(abs, progress, ct).ConfigureAwait(false);
            RecordKnown(projectRelativePath, mtime, size, hash);
            return hash;
        }

        /// <summary>
        /// Records what the file on disk currently hashes to. Leaves the
        /// baseline alone: re-hashing a file that changed says nothing about
        /// which version this machine last synced.
        /// </summary>
        public void RecordKnown(string projectRelativePath, long mtimeTicks, long size, string hash)
        {
            lock (_lock)
            {
                var e = GetOrCreate(projectRelativePath);
                e.mtimeTicks = mtimeTicks;
                e.size = size;
                e.hash = hash;
                _dirty = true;
            }
        }

        public void RecordKnownFromDisk(string projectRelativePath, string hash)
        {
            var info = new FileInfo(UniLfsPaths.ToAbsolute(projectRelativePath));
            if (info.Exists) RecordKnown(projectRelativePath, info.LastWriteTimeUtc.Ticks, info.Length, hash);
        }

        /// <summary>
        /// Records that local content and the manifest agree on
        /// <paramref name="manifestHash"/> — after Track, a successful Push, or
        /// a successful Pull. Everything after this compares against it.
        /// </summary>
        public void RecordSynced(string projectRelativePath, string manifestHash)
        {
            if (string.IsNullOrEmpty(manifestHash)) return;
            lock (_lock)
            {
                var e = GetOrCreate(projectRelativePath);
                if (e.baseHash == manifestHash) return;
                e.baseHash = manifestHash;
                _dirty = true;
            }
        }

        /// <summary>
        /// The manifest hash this file was last in sync with, or null when this
        /// machine has no record — in which case a divergence cannot be
        /// attributed to either side and callers must not overwrite anything.
        /// </summary>
        public string GetBaseline(string projectRelativePath)
        {
            lock (_lock)
            {
                Entry e;
                if (!_entries.TryGetValue(projectRelativePath, out e)) return null;
                return string.IsNullOrEmpty(e.baseHash) ? null : e.baseHash;
            }
        }

        public void Forget(string projectRelativePath)
        {
            lock (_lock)
            {
                if (_entries.Remove(projectRelativePath)) _dirty = true;
            }
        }

        /// <summary>Caller must hold <see cref="_lock"/>.</summary>
        Entry GetOrCreate(string projectRelativePath)
        {
            Entry e;
            if (!_entries.TryGetValue(projectRelativePath, out e))
                _entries[projectRelativePath] = e = new Entry { path = projectRelativePath };
            return e;
        }
    }
}
