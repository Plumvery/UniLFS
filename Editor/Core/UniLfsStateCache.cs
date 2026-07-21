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
    /// Per-machine cache under Library/ mapping (mtime, size) to a known SHA-256,
    /// so repeated status checks do not re-hash unchanged multi-gigabyte files.
    /// Safe to delete at any time; it is rebuilt on demand.
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

        public void RecordKnown(string projectRelativePath, long mtimeTicks, long size, string hash)
        {
            lock (_lock)
            {
                _entries[projectRelativePath] = new Entry { path = projectRelativePath, mtimeTicks = mtimeTicks, size = size, hash = hash };
                _dirty = true;
            }
        }

        public void RecordKnownFromDisk(string projectRelativePath, string hash)
        {
            var info = new FileInfo(UniLfsPaths.ToAbsolute(projectRelativePath));
            if (info.Exists) RecordKnown(projectRelativePath, info.LastWriteTimeUtc.Ticks, info.Length, hash);
        }

        public void Forget(string projectRelativePath)
        {
            lock (_lock)
            {
                if (_entries.Remove(projectRelativePath)) _dirty = true;
            }
        }
    }
}
