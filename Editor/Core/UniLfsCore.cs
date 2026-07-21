using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UniLFS.Editor
{
    /// <summary>
    /// The UniLFS operations. All async methods are safe to block on from batch
    /// mode (they never marshal back to the Unity main thread internally) and
    /// never call AssetDatabase — callers refresh after Pull.
    /// </summary>
    public static class UniLfsCore
    {
        public static IUniLfsStorageProvider CreateProvider(UniLfsSettings settings, UniLfsUserSettings user)
        {
            switch (settings.ProviderKind)
            {
                case UniLfsProviderKind.GoogleDrive:
                    return new GoogleDriveProvider(
                        UniLfsCredentials.DriveClientId(settings, user),
                        UniLfsCredentials.DriveClientSecret(settings, user),
                        UniLfsCredentials.DriveRefreshToken(user),
                        settings.driveFolderId);
                case UniLfsProviderKind.S3Compatible:
                default:
                    return new S3CompatibleProvider(
                        settings.s3Endpoint, settings.s3Bucket, settings.s3Region, settings.s3Prefix,
                        UniLfsCredentials.S3AccessKeyId(user),
                        UniLfsCredentials.S3SecretAccessKey(user));
            }
        }

        public static async Task<List<UniLfsStatusEntry>> StatusAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            try
            {
                return await StatusInternalAsync(manifest, cache, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                cache.Save();
            }
        }

        static async Task<List<UniLfsStatusEntry>> StatusInternalAsync(UniLfsManifest manifest, UniLfsStateCache cache, IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            var result = new List<UniLfsStatusEntry>();
            int i = 0;
            foreach (var f in manifest.files)
            {
                ct.ThrowIfCancellationRequested();
                Report(progress, "Checking files", f.path, i++, manifest.files.Count, 0f);
                var entry = new UniLfsStatusEntry { File = f };
                var info = new FileInfo(UniLfsPaths.ToAbsolute(f.path));
                if (!info.Exists)
                {
                    entry.State = UniLfsFileState.MissingLocal;
                }
                else
                {
                    entry.CurrentSize = info.Length;
                    entry.CurrentHash = await cache.GetHashAsync(f.path, null, ct).ConfigureAwait(false);
                    entry.State = entry.CurrentHash == f.hash ? UniLfsFileState.UpToDate : UniLfsFileState.Modified;
                }
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// Adds files to the manifest (or refreshes their hash) and gitignores
        /// them. Does not upload — run Push afterwards.
        /// </summary>
        public static async Task<UniLfsOpResult> TrackAsync(IEnumerable<string> paths, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var result = new UniLfsOpResult();
            var list = paths.ToList();
            try
            {
                int i = 0;
                foreach (var raw in list)
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = UniLfsPaths.ToProjectRelative(raw);
                    string reason = null;
                    if (rel == null || !UniLfsPaths.IsTrackablePath(rel, out reason))
                    {
                        if (string.IsNullOrEmpty(reason)) reason = "the path is outside the project";
                        result.Errors.Add(raw + ": " + reason);
                        continue;
                    }
                    string abs = UniLfsPaths.ToAbsolute(rel);
                    if (!File.Exists(abs))
                    {
                        result.Errors.Add(rel + ": file not found");
                        continue;
                    }
                    Report(progress, "Hashing", rel, i++, list.Count, 0f);
                    var existing = manifest.Find(rel);
                    string hash = await cache.GetHashAsync(rel, null, ct).ConfigureAwait(false);
                    long size = new FileInfo(abs).Length;
                    if (existing == null)
                    {
                        manifest.Upsert(rel, hash, size);
                        result.TrackedNew++;
                        result.NewlyTracked.Add(rel);
                    }
                    else if (existing.hash != hash || existing.size != size)
                    {
                        manifest.Upsert(rel, hash, size);
                        result.TrackedUpdated++;
                    }
                    else
                    {
                        result.Skipped++;
                    }
                }
            }
            finally
            {
                manifest.Save(UniLfsPaths.ManifestPath);
                UniLfsGitIgnore.Update(UniLfsPaths.GitIgnorePath, manifest.files.Select(f => f.path));
                cache.Save();
            }
            return result;
        }

        /// <summary>Removes files from the manifest. Files stay on disk.</summary>
        public static UniLfsOpResult Untrack(IEnumerable<string> paths)
        {
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var result = new UniLfsOpResult();
            foreach (var raw in paths)
            {
                string rel = UniLfsPaths.ToProjectRelative(raw);
                if (rel == null) rel = UniLfsPaths.Normalize(raw);
                if (manifest.Remove(rel))
                {
                    cache.Forget(rel);
                    result.Untracked++;
                }
            }
            manifest.Save(UniLfsPaths.ManifestPath);
            UniLfsGitIgnore.Update(UniLfsPaths.GitIgnorePath, manifest.files.Select(f => f.path));
            cache.Save();
            return result;
        }

        class CurrentFile
        {
            public string Hash;
            public long Size;
        }

        /// <summary>
        /// Re-hashes tracked files, uploads blobs the remote is missing, and only
        /// then records new hashes in the manifest — so a committed manifest never
        /// references a blob that failed to upload.
        /// </summary>
        public static async Task<UniLfsOpResult> PushAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var settings = UniLfsSettings.Load();
            var user = UniLfsUserSettings.Load();
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var result = new UniLfsOpResult();
            if (manifest.files.Count == 0) return result;

            var current = new Dictionary<string, CurrentFile>();
            var presentOnRemote = new HashSet<string>();
            var checkFailed = new HashSet<string>();

            using (var provider = CreateProvider(settings, user))
            {
                try
                {
                    int hashed = 0;
                    foreach (var f in manifest.files)
                    {
                        ct.ThrowIfCancellationRequested();
                        Report(progress, "Hashing", f.path, hashed++, manifest.files.Count, 0f);
                        string abs = UniLfsPaths.ToAbsolute(f.path);
                        if (!File.Exists(abs))
                        {
                            result.MissingLocal.Add(f.path);
                            continue;
                        }
                        string hash = await cache.GetHashAsync(f.path, null, ct).ConfigureAwait(false);
                        current[f.path] = new CurrentFile { Hash = hash, Size = new FileInfo(abs).Length };
                    }

                    var uploadSourceByHash = new Dictionary<string, string>();
                    foreach (var kv in current) uploadSourceByHash[kv.Value.Hash] = kv.Key;
                    var hashes = uploadSourceByHash.Keys.ToList();

                    var semaphore = new SemaphoreSlim(Math.Max(1, settings.parallelTransfers));

                    int checksDone = 0;
                    var checkTasks = hashes.Select(async h =>
                    {
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (await provider.BlobExistsAsync(h, ct).ConfigureAwait(false))
                                lock (presentOnRemote) presentOnRemote.Add(h);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            lock (checkFailed) checkFailed.Add(h);
                            lock (result.Errors) result.Errors.Add("check " + uploadSourceByHash[h] + ": " + e.Message);
                        }
                        finally
                        {
                            semaphore.Release();
                            Report(progress, "Checking remote", uploadSourceByHash[h], Interlocked.Increment(ref checksDone), hashes.Count, 0f);
                        }
                    }).ToList();
                    await Task.WhenAll(checkTasks).ConfigureAwait(false);

                    List<string> toUpload;
                    lock (presentOnRemote)
                        toUpload = hashes.Where(h => !presentOnRemote.Contains(h) && !checkFailed.Contains(h)).ToList();

                    int uploadsDone = 0;
                    var uploadTasks = toUpload.Select(async h =>
                    {
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        string rel = uploadSourceByHash[h];
                        try
                        {
                            var byteProgress = ByteProgress(progress, "Uploading", rel, uploadsDone, toUpload.Count, current[rel].Size);
                            await provider.UploadBlobAsync(h, UniLfsPaths.ToAbsolute(rel), byteProgress, ct).ConfigureAwait(false);
                            lock (presentOnRemote) presentOnRemote.Add(h);
                            Interlocked.Increment(ref result.Uploaded);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            lock (result.Errors) result.Errors.Add("upload " + rel + ": " + e.Message);
                        }
                        finally
                        {
                            semaphore.Release();
                            Report(progress, "Uploading", rel, Interlocked.Increment(ref uploadsDone), toUpload.Count, 1f);
                        }
                    }).ToList();
                    await Task.WhenAll(uploadTasks).ConfigureAwait(false);
                }
                finally
                {
                    // Commit only entries whose blob is confirmed to exist remotely.
                    foreach (var f in manifest.files)
                    {
                        CurrentFile cur;
                        if (!current.TryGetValue(f.path, out cur)) continue;
                        bool present;
                        lock (presentOnRemote) present = presentOnRemote.Contains(cur.Hash);
                        if (!present) continue;
                        if (f.hash == cur.Hash && f.size == cur.Size) result.Skipped++;
                        else { f.hash = cur.Hash; f.size = cur.Size; }
                    }
                    manifest.Save(UniLfsPaths.ManifestPath);
                    UniLfsGitIgnore.Update(UniLfsPaths.GitIgnorePath, manifest.files.Select(f => f.path));
                    cache.Save();
                }
            }
            return result;
        }

        /// <summary>
        /// Downloads blobs for files that are missing locally (and, when
        /// <paramref name="restoreModified"/> is true, overwrites locally
        /// modified files with the manifest version). Locally modified files are
        /// otherwise left untouched and reported in KeptModified.
        /// </summary>
        public static async Task<UniLfsOpResult> PullAsync(bool restoreModified = false, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var settings = UniLfsSettings.Load();
            var user = UniLfsUserSettings.Load();
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var result = new UniLfsOpResult();
            if (manifest.files.Count == 0) return result;

            try
            {
                var statuses = await StatusInternalAsync(manifest, cache, progress, ct).ConfigureAwait(false);
                var targets = statuses.Where(s =>
                    s.State == UniLfsFileState.MissingLocal ||
                    (restoreModified && s.State == UniLfsFileState.Modified)).ToList();
                foreach (var s in statuses)
                {
                    if (s.State == UniLfsFileState.UpToDate) result.Skipped++;
                    else if (s.State == UniLfsFileState.Modified && !restoreModified) result.KeptModified.Add(s.File.path);
                }
                if (targets.Count == 0) return result;

                using (var provider = CreateProvider(settings, user))
                {
                    Directory.CreateDirectory(UniLfsPaths.TempDownloadDir);
                    var groups = targets.GroupBy(t => t.File.hash).ToList();
                    var semaphore = new SemaphoreSlim(Math.Max(1, settings.parallelTransfers));
                    int groupsDone = 0;
                    var tasks = groups.Select(async group =>
                    {
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        string hash = group.Key;
                        string tmpBlob = UniLfsPaths.Combine(UniLfsPaths.TempDownloadDir, hash + ".blob");
                        string firstPath = group.First().File.path;
                        try
                        {
                            var byteProgress = ByteProgress(progress, "Downloading", firstPath, groupsDone, groups.Count, group.First().File.size);
                            await provider.DownloadBlobAsync(hash, tmpBlob, byteProgress, ct).ConfigureAwait(false);
                            foreach (var target in group)
                            {
                                string abs = UniLfsPaths.ToAbsolute(target.File.path);
                                Directory.CreateDirectory(Path.GetDirectoryName(abs));
                                if (File.Exists(abs)) File.SetAttributes(abs, FileAttributes.Normal);
                                File.Copy(tmpBlob, abs, true);
                                cache.RecordKnownFromDisk(target.File.path, hash);
                                Interlocked.Increment(ref result.Downloaded);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            lock (result.Errors)
                                foreach (var target in group)
                                    result.Errors.Add("download " + target.File.path + ": " + e.Message);
                        }
                        finally
                        {
                            try { if (File.Exists(tmpBlob)) File.Delete(tmpBlob); } catch (Exception) { }
                            semaphore.Release();
                            Report(progress, "Downloading", firstPath, Interlocked.Increment(ref groupsDone), groups.Count, 1f);
                        }
                    }).ToList();
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
            }
            finally
            {
                cache.Save();
            }
            return result;
        }

        public static string GitRemoveHint(IEnumerable<string> newlyTrackedPaths)
        {
            var paths = newlyTrackedPaths.ToList();
            if (paths.Count == 0) return null;
            var lines = new List<string>
            {
                "If these files were previously committed to git, remove them from the index (files stay on disk):",
            };
            lines.AddRange(paths.Select(p => "  git rm --cached -- \"" + p + "\""));
            return string.Join("\n", lines);
        }

        static void Report(IProgress<UniLfsProgress> progress, string phase, string item, int done, int total, float itemProgress)
        {
            if (progress != null)
                progress.Report(new UniLfsProgress { Phase = phase, Item = item, Done = done, Total = total, ItemProgress = itemProgress });
        }

        static IProgress<long> ByteProgress(IProgress<UniLfsProgress> outer, string phase, string item, int done, int total, long sizeBytes)
        {
            return outer == null ? null : new ByteProgressAdapter(outer, phase, item, done, total, sizeBytes);
        }

        sealed class ByteProgressAdapter : IProgress<long>
        {
            readonly IProgress<UniLfsProgress> _outer;
            readonly string _phase;
            readonly string _item;
            readonly int _done;
            readonly int _total;
            readonly long _size;

            public ByteProgressAdapter(IProgress<UniLfsProgress> outer, string phase, string item, int done, int total, long size)
            {
                _outer = outer;
                _phase = phase;
                _item = item;
                _done = done;
                _total = total;
                _size = size;
            }

            public void Report(long bytes)
            {
                _outer.Report(new UniLfsProgress
                {
                    Phase = _phase,
                    Item = _item,
                    Done = _done,
                    Total = _total,
                    ItemProgress = _size > 0 ? (float)Math.Min(1.0, (double)bytes / _size) : 0f,
                });
            }
        }
    }
}
