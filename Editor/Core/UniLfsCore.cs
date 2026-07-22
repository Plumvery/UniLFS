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
        // Each operation runs the progress bar from 0 to 100% exactly once. The
        // stages inside it get a fixed slice each, roughly proportional to how
        // long they take on a cold cache, so the bar never restarts mid-run.
        const float PushHashSpan = 0.25f;
        const float PushCheckSpan = 0.10f;
        const float PullStatusSpan = 0.20f;
        const float StatusVerifySpan = 0.40f;

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
            var report = await StatusAsync(false, progress, ct).ConfigureAwait(false);
            return report.Files;
        }

        /// <summary>
        /// Status, optionally preceded by asking remote storage whether it
        /// really holds the blobs the manifest references.
        ///
        /// Without that check the "confirmed in storage" flag is only as good as
        /// this machine's own record: a fresh clone has uploaded nothing, so
        /// every file reads as "not pushed", and a blob deleted from the bucket
        /// keeps reading as "up to date" forever. The check costs one existence
        /// request per distinct blob, which is why the caller opts into it —
        /// pressing Refresh does, the background status checks do not.
        /// </summary>
        public static async Task<UniLfsStatusReport> StatusAsync(bool verifyRemote, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire(verifyRemote ? "Verify" : "Status"))
                return await StatusUnlockedAsync(verifyRemote, progress, ct).ConfigureAwait(false);
        }

        static async Task<UniLfsStatusReport> StatusUnlockedAsync(bool verifyRemote, IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            var settings = UniLfsSettings.Load();
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var remote = UniLfsRemoteBlobCache.Load(settings);
            var reporter = new UniLfsProgressReporter(progress);
            var report = new UniLfsStatusReport();
            try
            {
                float start = 0f, span = 1f;
                if (verifyRemote && manifest.files.Count > 0)
                {
                    start = StatusVerifySpan;
                    span = 1f - StatusVerifySpan;
                    var pathsByHash = PathsByHash(manifest);
                    try
                    {
                        var check = await CheckRemoteAsync(pathsByHash, settings, UniLfsUserSettings.Load(), remote,
                            reporter, 0f, StatusVerifySpan, ct).ConfigureAwait(false);
                        report.Verified = true;
                        report.Confirmed = check.Present.Count;
                        foreach (var h in check.Missing) report.MissingRemote.AddRange(pathsByHash[h]);
                        report.Failures.AddRange(check.Failures);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        // A check that could not run at all (provider
                        // misconfigured, credentials rejected) must not cost the
                        // caller the local half of the answer.
                        report.Failures.Add(e.Message);
                    }
                }
                report.Files = await StatusInternalAsync(manifest, cache, remote, reporter, start, span, ct).ConfigureAwait(false);
                reporter.Finish();
                return report;
            }
            finally
            {
                cache.Save();
                // Status is the operation that runs most often, so it is where
                // the confirmation file gets kept to size.
                remote.Prune(manifest.files.Select(f => f.hash));
                remote.Save();
            }
        }

        static async Task<List<UniLfsStatusEntry>> StatusInternalAsync(UniLfsManifest manifest, UniLfsStateCache cache, UniLfsRemoteBlobCache remote, UniLfsProgressReporter reporter, float start, float span, CancellationToken ct)
        {
            reporter.BeginPhase("Checking files", start, span, manifest.files.Count, TotalSize(manifest.files));
            var result = new List<UniLfsStatusEntry>();
            foreach (var f in manifest.files)
            {
                ct.ThrowIfCancellationRequested();
                string baseline = cache.GetBaseline(f.path);
                var entry = new UniLfsStatusEntry
                {
                    File = f,
                    RemoteKnown = remote.Contains(f.hash),
                    BaselineKnown = !string.IsNullOrEmpty(baseline),
                };
                var info = new FileInfo(UniLfsPaths.ToAbsolute(f.path));
                using (var item = reporter.Begin(f.path, info.Exists ? info.Length : f.size))
                {
                    if (!info.Exists || UniLfsPlaceholder.IsPlaceholder(info.FullName))
                    {
                        // A placeholder stands in for content that is not here
                        // yet, so it has to read as missing. Reading as Modified
                        // would both hide it from Pull and offer it to Push.
                        entry.State = UniLfsFileState.MissingLocal;
                    }
                    else
                    {
                        entry.CurrentSize = info.Length;
                        entry.CurrentHash = await cache.GetHashAsync(f.path, item.Ratio(info.Length), ct).ConfigureAwait(false);
                        entry.State = UniLfsThreeWay.Classify(entry.CurrentHash, f.hash, baseline);
                        // Local and manifest agreeing is itself a synced state,
                        // so files that predate baselines - or whose Library/
                        // was wiped - adopt one here rather than staying
                        // unattributable until the next Push or Pull.
                        if (entry.State == UniLfsFileState.UpToDate)
                        {
                            cache.RecordSynced(f.path, f.hash);
                            entry.BaselineKnown = true;
                        }
                    }
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
            using (UniLfsOperationLock.Acquire("Track"))
                return await TrackUnlockedAsync(paths, progress, ct).ConfigureAwait(false);
        }

        static async Task<UniLfsOpResult> TrackUnlockedAsync(IEnumerable<string> paths, IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var result = new UniLfsOpResult();
            var list = paths.ToList();
            var reporter = new UniLfsProgressReporter(progress);
            try
            {
                reporter.BeginPhase("Hashing", 0f, 1f, list.Count, 0);
                foreach (var raw in list)
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = UniLfsPaths.ToProjectRelative(raw);
                    string reason = null;
                    if (rel == null || !UniLfsPaths.IsTrackablePath(rel, out reason))
                    {
                        if (string.IsNullOrEmpty(reason)) reason = "the path is outside the project";
                        result.Errors.Add(raw + ": " + reason);
                        reporter.Begin(raw, 0).Dispose();
                        continue;
                    }
                    string abs = UniLfsPaths.ToAbsolute(rel);
                    if (!File.Exists(abs))
                    {
                        result.Errors.Add(rel + ": file not found");
                        reporter.Begin(rel, 0).Dispose();
                        continue;
                    }
                    long size = new FileInfo(abs).Length;
                    var existing = manifest.Find(rel);
                    string hash;
                    using (var item = reporter.Begin(rel, size))
                        hash = await cache.GetHashAsync(rel, item.Ratio(size), ct).ConfigureAwait(false);
                    var state = existing == null
                        ? UniLfsFileState.UpToDate
                        : UniLfsThreeWay.Classify(hash, existing.hash, cache.GetBaseline(rel));
                    // Re-tracking a file whose manifest entry moved on while
                    // this copy stayed put would rewrite the entry back to this
                    // stale content - the same rollback Push refuses. Nothing is
                    // lost by declining: Pull delivers the newer version.
                    if (state == UniLfsFileState.Outdated)
                    {
                        result.Outdated.Add(rel);
                        continue;
                    }
                    // Conflicted falls through on purpose. Track is the "keep
                    // mine" half of resolving a conflict - Restore Modified is
                    // "take theirs" - and it is the only way to declare local
                    // content the winner. Recorded so the choice is not silent:
                    // it does overwrite whatever the manifest named.
                    if (state == UniLfsFileState.Conflicted) result.Conflicted.Add(rel);
                    // Recorded so a clone, which gets the .meta but not the
                    // gitignored asset, can put the GUID back instead of
                    // letting Unity mint a new one. See UniLfsMetaGuard.
                    string guid = UniLfsMetaFile.ReadGuid(UniLfsMetaFile.PathFor(abs));
                    if (existing == null)
                    {
                        manifest.Upsert(rel, hash, size).guid = guid;
                        result.TrackedNew++;
                        result.NewlyTracked.Add(rel);
                    }
                    else if (existing.hash != hash || existing.size != size
                             || (guid != null && existing.guid != guid))
                    {
                        // Never clears a recorded GUID: a missing .meta at this
                        // moment says nothing about the one everyone else has.
                        manifest.Upsert(rel, hash, size).guid = guid ?? existing.guid;
                        result.TrackedUpdated++;
                    }
                    else
                    {
                        result.Skipped++;
                    }
                    // Track always leaves local content and the manifest in
                    // agreement, whichever branch got here.
                    cache.RecordSynced(rel, hash);
                }
                reporter.Finish();
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
            using (UniLfsOperationLock.Acquire("Untrack"))
                return UntrackUnlocked(paths);
        }

        static UniLfsOpResult UntrackUnlocked(IEnumerable<string> paths)
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
                    // The path leaves the managed .gitignore block with this
                    // call, so a placeholder left behind would stop being
                    // ignored and could be committed as if it were the asset.
                    UniLfsPlaceholder.Clear(UniLfsPaths.ToAbsolute(rel));
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
            return await PushAsync(false, progress, ct).ConfigureAwait(false);
        }

        /// <param name="requireBaseline">
        /// Skip files whose difference from the manifest cannot be attributed to
        /// either side, instead of assuming the local copy is the newer one.
        /// Push always rewrites the manifest from what it hashed, so for a file
        /// with no baseline that assumption is a guess that can undo a
        /// teammate's push. Callers acting on their own — Auto Push — pass true;
        /// a human pressing Push has said which side they mean.
        /// </param>
        public static async Task<UniLfsOpResult> PushAsync(bool requireBaseline, IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Push"))
                return await PushUnlockedAsync(requireBaseline, progress, ct).ConfigureAwait(false);
        }

        static async Task<UniLfsOpResult> PushUnlockedAsync(bool requireBaseline, IProgress<UniLfsProgress> progress, CancellationToken ct)
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
            var reporter = new UniLfsProgressReporter(progress);
            var remote = UniLfsRemoteBlobCache.Load(settings);

            using (var provider = CreateProvider(settings, user))
            {
                try
                {
                    reporter.BeginPhase("Hashing", 0f, PushHashSpan, manifest.files.Count, TotalSize(manifest.files));
                    foreach (var f in manifest.files)
                    {
                        ct.ThrowIfCancellationRequested();
                        string abs = UniLfsPaths.ToAbsolute(f.path);
                        var info = new FileInfo(abs);
                        // Placeholders must never reach the upload path: the
                        // manifest is rewritten from what was hashed here, so
                        // pushing one would point every clone at the stand-in
                        // and orphan the real blob.
                        if (!info.Exists || UniLfsPlaceholder.IsPlaceholder(abs))
                        {
                            result.MissingLocal.Add(f.path);
                            reporter.Begin(f.path, f.size).Dispose();
                            continue;
                        }
                        long size = info.Length;
                        string hash;
                        using (var item = reporter.Begin(f.path, size))
                            hash = await cache.GetHashAsync(f.path, item.Ratio(size), ct).ConfigureAwait(false);

                        string baseline = cache.GetBaseline(f.path);
                        var state = UniLfsThreeWay.Classify(hash, f.hash, baseline);
                        // Guarded here rather than in the caller's file list:
                        // Push always walks the whole manifest, so filtering
                        // what made it *fire* would still let it rewrite
                        // everything else it found on the way.
                        if (requireBaseline && string.IsNullOrEmpty(baseline) && state != UniLfsFileState.UpToDate)
                        {
                            result.Unattributed.Add(f.path);
                            continue;
                        }
                        if (state == UniLfsFileState.Outdated)
                        {
                            // Someone else pushed a newer version that this
                            // machine has not pulled yet. The writeback below
                            // would drag the manifest back to this older copy
                            // and silently undo their change, so this file is
                            // not Push's to touch - it is Pull's.
                            result.Outdated.Add(f.path);
                            continue;
                        }
                        if (state == UniLfsFileState.Conflicted)
                        {
                            // Local content and the manifest moved apart in
                            // different directions. Either version could be the
                            // one worth keeping, so neither gets picked here.
                            result.Conflicted.Add(f.path);
                            continue;
                        }
                        current[f.path] = new CurrentFile { Hash = hash, Size = size };
                    }

                    var uploadSourceByHash = new Dictionary<string, string>();
                    foreach (var kv in current) uploadSourceByHash[kv.Value.Hash] = kv.Key;
                    var hashes = uploadSourceByHash.Keys.ToList();

                    var semaphore = new SemaphoreSlim(Math.Max(1, settings.parallelTransfers));

                    reporter.BeginPhase("Checking remote", PushHashSpan, PushCheckSpan, hashes.Count, 0);
                    var checkTasks = hashes.Select(async h =>
                    {
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        using (reporter.Begin(uploadSourceByHash[h], 0))
                        {
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
                            }
                        }
                    }).ToList();
                    await Task.WhenAll(checkTasks).ConfigureAwait(false);

                    List<string> toUpload;
                    lock (presentOnRemote)
                        toUpload = hashes.Where(h => !presentOnRemote.Contains(h) && !checkFailed.Contains(h)).ToList();

                    long uploadBytes = 0;
                    foreach (var h in toUpload) uploadBytes += current[uploadSourceByHash[h]].Size;
                    reporter.BeginPhase("Uploading", PushHashSpan + PushCheckSpan, 1f - PushHashSpan - PushCheckSpan, toUpload.Count, uploadBytes);
                    var uploadTasks = toUpload.Select(async h =>
                    {
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        string rel = uploadSourceByHash[h];
                        using (var item = reporter.Begin(rel, current[rel].Size))
                        {
                            try
                            {
                                await provider.UploadBlobAsync(h, UniLfsPaths.ToAbsolute(rel), item, ct).ConfigureAwait(false);
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
                            }
                        }
                    }).ToList();
                    await Task.WhenAll(uploadTasks).ConfigureAwait(false);
                    reporter.Finish();
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
                        // The blob is in storage and the manifest now names it,
                        // so this is the version this machine is in sync with.
                        cache.RecordSynced(f.path, cur.Hash);
                    }
                    manifest.Save(UniLfsPaths.ManifestPath);
                    UniLfsGitIgnore.Update(UniLfsPaths.GitIgnorePath, manifest.files.Select(f => f.path));
                    cache.Save();
                    // Everything in presentOnRemote was either uploaded just now
                    // or reported as already there, so both are proof.
                    lock (presentOnRemote) remote.AddRange(presentOnRemote);
                    remote.Save();
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
            using (UniLfsOperationLock.Acquire(restoreModified ? "Restore" : "Pull"))
                return await PullUnlockedAsync(restoreModified, progress, ct).ConfigureAwait(false);
        }

        static async Task<UniLfsOpResult> PullUnlockedAsync(bool restoreModified, IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            var settings = UniLfsSettings.Load();
            var user = UniLfsUserSettings.Load();
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var cache = new UniLfsStateCache(UniLfsPaths.StateCachePath);
            var result = new UniLfsOpResult();
            if (manifest.files.Count == 0) return result;
            var reporter = new UniLfsProgressReporter(progress);
            var remote = UniLfsRemoteBlobCache.Load(settings);

            try
            {
                var statuses = await StatusInternalAsync(manifest, cache, remote, reporter, 0f, PullStatusSpan, ct).ConfigureAwait(false);
                var targets = statuses.Where(s =>
                    s.State == UniLfsFileState.MissingLocal ||
                    // The manifest moved on and this copy is exactly the one
                    // this machine last synced, so replacing it loses nothing.
                    // Without this, a teammate updating an already-tracked file
                    // reached nobody: the file was on disk, so it never counted
                    // as missing, and Pull downloaded only missing files.
                    s.State == UniLfsFileState.Outdated ||
                    (restoreModified && (s.State == UniLfsFileState.Modified || s.State == UniLfsFileState.Conflicted))).ToList();
                foreach (var s in statuses)
                {
                    if (s.State == UniLfsFileState.UpToDate) result.Skipped++;
                    else if (s.State == UniLfsFileState.Modified && !restoreModified) result.KeptModified.Add(s.File.path);
                    else if (s.State == UniLfsFileState.Conflicted && !restoreModified) result.Conflicted.Add(s.File.path);
                }
                if (targets.Count == 0)
                {
                    reporter.Finish();
                    return result;
                }

                using (var provider = CreateProvider(settings, user))
                {
                    Directory.CreateDirectory(UniLfsPaths.TempDownloadDir);
                    var groups = targets.GroupBy(t => t.File.hash).ToList();
                    var semaphore = new SemaphoreSlim(Math.Max(1, settings.parallelTransfers));
                    long downloadBytes = 0;
                    foreach (var g in groups) downloadBytes += g.First().File.size;
                    reporter.BeginPhase("Downloading", PullStatusSpan, 1f - PullStatusSpan, groups.Count, downloadBytes);
                    var tasks = groups.Select(async group =>
                    {
                        await semaphore.WaitAsync(ct).ConfigureAwait(false);
                        string hash = group.Key;
                        string tmpBlob = UniLfsPaths.Combine(UniLfsPaths.TempDownloadDir, hash + ".blob");
                        var first = group.First().File;
                        using (var item = reporter.Begin(first.path, first.size))
                        {
                            try
                            {
                                await provider.DownloadBlobAsync(hash, tmpBlob, item, ct).ConfigureAwait(false);
                                // We just pulled it down, so it is definitely there.
                                remote.Add(hash);
                                foreach (var target in group)
                                {
                                    string abs = UniLfsPaths.ToAbsolute(target.File.path);
                                    Directory.CreateDirectory(Path.GetDirectoryName(abs));
                                    // Put a discarded .meta back before the
                                    // asset lands. Once the asset is there, the
                                    // next import mints a fresh GUID for it and
                                    // every reference to the old one breaks.
                                    // No-ops when a .meta is already present.
                                    UniLfsMetaFile.WriteMinimal(UniLfsMetaFile.PathFor(abs), target.File.guid);
                                    if (File.Exists(abs)) File.SetAttributes(abs, FileAttributes.Normal);
                                    File.Copy(tmpBlob, abs, true);
                                    cache.RecordKnownFromDisk(target.File.path, hash);
                                    // Local content and the manifest now agree,
                                    // which is what later runs compare against
                                    // to tell a local edit from a stale copy.
                                    cache.RecordSynced(target.File.path, hash);
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
                            }
                        }
                    }).ToList();
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    reporter.Finish();
                }
            }
            finally
            {
                cache.Save();
                remote.Save();
            }
            return result;
        }

        /// <summary>
        /// Checks that every blob the manifest references exists in remote
        /// storage, without downloading anything. Missing blobs are reported in
        /// Errors. Works from a bare checkout (local files are not needed), so
        /// CI can gate merges on it.
        /// </summary>
        public static async Task<UniLfsOpResult> VerifyRemoteAsync(IProgress<UniLfsProgress> progress = null, CancellationToken ct = default(CancellationToken))
        {
            using (UniLfsOperationLock.Acquire("Verify"))
                return await VerifyRemoteUnlockedAsync(progress, ct).ConfigureAwait(false);
        }

        static async Task<UniLfsOpResult> VerifyRemoteUnlockedAsync(IProgress<UniLfsProgress> progress, CancellationToken ct)
        {
            var settings = UniLfsSettings.Load();
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var result = new UniLfsOpResult();
            if (manifest.files.Count == 0) return result;

            var pathsByHash = PathsByHash(manifest);
            var reporter = new UniLfsProgressReporter(progress);
            var remote = UniLfsRemoteBlobCache.Load(settings);
            try
            {
                var check = await CheckRemoteAsync(pathsByHash, settings, UniLfsUserSettings.Load(), remote,
                    reporter, 0f, 1f, ct).ConfigureAwait(false);
                result.Skipped = check.Present.Count;
                foreach (var h in check.Missing)
                    result.Errors.Add("missing on remote: " + string.Join(", ", pathsByHash[h]) + " (" + h.Substring(0, 8) + "...)");
                result.Errors.AddRange(check.Failures);
                reporter.Finish();
            }
            finally
            {
                remote.Save();
            }
            return result;
        }

        /// <summary>What storage answered about a set of blob hashes.</summary>
        class RemoteCheck
        {
            public readonly List<string> Present = new List<string>();
            /// <summary>Hashes storage answered for and does not have.</summary>
            public readonly List<string> Missing = new List<string>();
            /// <summary>One message per hash we could not get an answer for — "unknown", not "absent".</summary>
            public readonly List<string> Failures = new List<string>();
        }

        /// <summary>
        /// Asks storage which of the given blobs it has, without downloading
        /// anything, and records the answers both ways: a blob storage has is
        /// proof, a blob it denies retracts an older confirmation. Checks that
        /// fail outright prove nothing, so they leave the record alone and are
        /// reported separately.
        /// </summary>
        static async Task<RemoteCheck> CheckRemoteAsync(Dictionary<string, List<string>> pathsByHash, UniLfsSettings settings, UniLfsUserSettings user, UniLfsRemoteBlobCache remote, UniLfsProgressReporter reporter, float start, float span, CancellationToken ct)
        {
            var check = new RemoteCheck();
            var hashes = pathsByHash.Keys.ToList();
            reporter.BeginPhase("Verifying remote", start, span, hashes.Count, 0);
            if (hashes.Count == 0) return check;

            using (var provider = CreateProvider(settings, user))
            {
                var semaphore = new SemaphoreSlim(Math.Max(1, settings.parallelTransfers));
                var tasks = hashes.Select(async h =>
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    using (reporter.Begin(pathsByHash[h][0], 0))
                    {
                        try
                        {
                            if (await provider.BlobExistsAsync(h, ct).ConfigureAwait(false))
                            {
                                remote.Add(h);
                                lock (check) check.Present.Add(h);
                            }
                            else
                            {
                                remote.Remove(h);
                                lock (check) check.Missing.Add(h);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            lock (check) check.Failures.Add("check " + string.Join(", ", pathsByHash[h]) + ": " + e.Message);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            return check;
        }

        /// <summary>
        /// Groups the manifest by blob, so duplicated content costs one
        /// existence check rather than one per file, while a report can still
        /// name every file that check speaks for.
        /// </summary>
        static Dictionary<string, List<string>> PathsByHash(UniLfsManifest manifest)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var f in manifest.files)
            {
                List<string> list;
                if (!map.TryGetValue(f.hash, out list))
                    map[f.hash] = list = new List<string>();
                list.Add(f.path);
            }
            return map;
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

        /// <summary>
        /// Manifest sizes, used to weight a phase by bytes instead of file
        /// count — otherwise a 4 KB file and a 4 GB file each move the bar by
        /// the same jump.
        /// </summary>
        static long TotalSize(IEnumerable<UniLfsManifestFile> files)
        {
            long total = 0;
            foreach (var f in files) total += f.size;
            return total;
        }
    }
}
