import fs from 'node:fs';
import path from 'node:path';
import { Manifest } from './manifest.js';
import { StagedPaths } from './staged.js';
import { TrackPatterns } from './patterns.js';
import { StateCache } from './state-cache.js';
import { RemoteBlobCache } from './remote-cache.js';
import { updateGitIgnore, updateGitExclude, stagedPathsNothingIgnores } from './ignore.js';
import { isPlaceholder, clearPlaceholder, readMetaGuid, writeMinimalMeta } from './meta.js';
import { withLock } from './lock.js';
import { createProvider } from './providers/index.js';
import { downloadVerified } from './providers/http.js';
import { LocalSources } from './local-sources.js';
import { pool, isAbort, throwIfAborted, cmpOrdinal, makeWritable } from './util.js';

export const State = Object.freeze({
  UpToDate: 'UpToDate',
  Modified: 'Modified',
  Outdated: 'Outdated',
  Conflicted: 'Conflicted',
  MissingLocal: 'MissingLocal',
  Staged: 'Staged',
});

/**
 * What a tracked file's hashes mean, judged against the baseline - the
 * manifest hash this machine last agreed with. "I edited it" and "someone
 * pushed a newer one" both read as local != manifest; the baseline is the
 * merge base that says which side moved.
 */
export function classify(localHash, manifestHash, baselineHash) {
  if (localHash === manifestHash) return State.UpToDate;
  // No baseline: the divergence cannot be attributed, so report the reading
  // that makes every caller leave local content alone.
  if (!baselineHash) return State.Modified;
  if (localHash === baselineHash) return State.Outdated;
  if (manifestHash === baselineHash) return State.Modified;
  return State.Conflicted;
}

function newResult() {
  return {
    uploaded: 0,
    downloaded: 0,
    copiedLocal: 0,
    skipped: 0,
    trackedNew: 0,
    promoted: 0,
    untracked: 0,
    missingLocal: [],
    keptModified: [],
    newlyTracked: [],
    notIgnored: [],
    outdated: [],
    conflicted: [],
    unattributed: [],
    notTracked: [],
    errors: [],
  };
}

/**
 * Emits { type: 'phase' | 'start' | 'bytes' | 'done', ... } to an optional
 * callback. A phase carries the slice of the whole operation it covers
 * (start, span in 0..1), so a progress bar can run 0..100% exactly once
 * across phases; items carry an id, since parallel transfers interleave.
 */
class Reporter {
  constructor(onProgress) {
    this.cb = typeof onProgress === 'function' ? onProgress : null;
    this.phaseName = '';
    this.done = 0;
    this.total = 0;
    this.nextId = 0;
  }

  phase(name, total, totalBytes = 0, start = 0, span = 1) {
    this.phaseName = name;
    this.done = 0;
    this.total = total;
    if (this.cb) this.cb({ type: 'phase', phase: name, total, totalBytes, start, span });
  }

  item(p, size = 0) {
    const phase = this.phaseName;
    const id = ++this.nextId;
    if (this.cb) this.cb({ type: 'start', phase, id, path: p, size });
    let finished = false;
    return {
      bytes: (n) => {
        if (this.cb) this.cb({ type: 'bytes', phase, id, path: p, bytes: n, size });
      },
      done: () => {
        if (finished) return;
        finished = true;
        this.done++;
        if (this.cb) this.cb({ type: 'done', phase, id, path: p, done: this.done, total: this.total });
      },
    };
  }
}

// Each operation's progress slices, matching the Unity package's.
const PUSH_HASH = [0, 0.25];
const PUSH_CHECK = [0.25, 0.10];
const PUSH_UPLOAD = [0.35, 0.65];
const PULL_STATUS = [0, 0.20];
const PULL_DOWNLOAD = [0.20, 0.80];
const STATUS_VERIFY = [0, 0.40];
const STATUS_AFTER_VERIFY = [0.40, 0.60];

function openState(project) {
  return {
    manifest: Manifest.load(project.manifestPath),
    staged: StagedPaths.load(project.stagedPath),
    cache: new StateCache(project.stateCachePath),
  };
}

function fileInfo(abs) {
  try {
    const st = fs.statSync(abs);
    return st.isFile() ? { exists: true, size: st.size } : { exists: false, size: 0 };
  } catch {
    return { exists: false, size: 0 };
  }
}

function totalSize(files) {
  return files.reduce((n, f) => n + (f.size || 0), 0);
}

function pathsByHash(manifest) {
  const map = new Map();
  for (const f of manifest.files) {
    if (!map.has(f.hash)) map.set(f.hash, []);
    map.get(f.hash).push(f.path);
  }
  return map;
}

function parallelism(project) {
  const n = Number(project.settings.parallelTransfers);
  return Number.isFinite(n) && n >= 1 ? Math.floor(n) : 4;
}

// --------------------------------------------------------------------------
// Status
// --------------------------------------------------------------------------

/**
 * Status of every tracked file. With verifyRemote, storage is first asked
 * whether it holds each blob the manifest references (one request per
 * distinct blob), which is what turns "not pushed" into "up to date" on a
 * fresh clone and catches blobs deleted from the bucket.
 */
export async function status(project, { verifyRemote = false, onProgress, signal, provider } = {}) {
  return withLock(project, verifyRemote ? 'Verify' : 'Status', async () => {
    const { manifest, staged, cache } = openState(project);
    const remote = RemoteBlobCache.forProject(project);
    const reporter = new Reporter(onProgress);
    const report = { files: [], verified: false, confirmed: 0, missingRemote: [], failures: [], notIgnored: [] };
    try {
      if (verifyRemote && manifest.files.length > 0) {
        const byHash = pathsByHash(manifest);
        try {
          const check = await checkRemote(project, byHash, remote, reporter, signal, provider, STATUS_VERIFY);
          report.verified = true;
          report.confirmed = check.present.length;
          for (const h of check.missing) report.missingRemote.push(...byHash.get(h));
          report.failures.push(...check.failures);
        } catch (e) {
          if (isAbort(e)) throw e;
          // A check that could not run at all must not cost the local half.
          report.failures.push(e.message);
        }
      }
      report.files = await statusInternal(project, manifest, staged, cache, remote, reporter, signal,
        report.verified || report.failures.length ? STATUS_AFTER_VERIFY : [0, 1]);
      report.notIgnored = stagedPathsNothingIgnores(project, staged.paths);
      return report;
    } finally {
      cache.save();
      remote.prune(manifest.files.map((f) => f.hash));
      remote.save();
    }
  });
}

async function statusInternal(project, manifest, staged, cache, remote, reporter, signal, slice = [0, 1]) {
  const stagedOnly = staged.paths.filter((p) => !manifest.find(p));
  reporter.phase('Checking files', manifest.files.length + stagedOnly.length, totalSize(manifest.files), ...slice);
  const result = [];
  for (const f of manifest.files) {
    throwIfAborted(signal);
    const baseline = cache.getBaseline(f.path);
    const entry = {
      path: f.path,
      hash: f.hash,
      size: f.size,
      guid: f.guid,
      state: State.MissingLocal,
      currentHash: null,
      currentSize: 0,
      remoteKnown: remote.has(f.hash),
      baselineKnown: !!baseline,
    };
    const abs = project.abs(f.path);
    const info = fileInfo(abs);
    const item = reporter.item(f.path, info.exists ? info.size : f.size);
    try {
      if (info.exists && !isPlaceholder(abs)) {
        entry.currentSize = info.size;
        entry.currentHash = await cache.getHash(f.path, abs, item.bytes, signal);
        entry.state = classify(entry.currentHash, f.hash, baseline);
        // Staged as well as in the manifest: both sides tracked this path
        // independently, so nothing here may pick a winner.
        if (entry.state !== State.UpToDate && staged.contains(f.path)) entry.state = State.Conflicted;
        // Agreement is itself a synced state; adopt it as the baseline.
        if (entry.state === State.UpToDate) {
          cache.recordSynced(f.path, f.hash);
          entry.baselineKnown = true;
        }
      }
    } finally {
      item.done();
    }
    result.push(entry);
  }
  for (const p of stagedOnly) {
    throwIfAborted(signal);
    const abs = project.abs(p);
    const info = fileInfo(abs);
    reporter.item(p, 0).done();
    result.push({
      path: p,
      hash: '',
      size: info.size,
      state: info.exists && !isPlaceholder(abs) ? State.Staged : State.MissingLocal,
      currentHash: null,
      currentSize: info.size,
      remoteKnown: false,
      baselineKnown: false,
    });
  }
  for (const e of result) {
    e.needsDownload = (e.state === State.MissingLocal || e.state === State.Outdated) && !!e.hash;
  }
  result.sort((a, b) => cmpOrdinal(a.path, b.path));
  return result;
}

// --------------------------------------------------------------------------
// Track / Untrack / Keep mine
// --------------------------------------------------------------------------

/**
 * Stages files for tracking and hides them from git locally. Writes neither
 * the manifest nor an upload: Push is what turns a staged path into a
 * manifest entry. Paths are absolute or project-relative.
 */
export async function track(project, paths, { onProgress, signal } = {}) {
  return withLock(project, 'Track', () => trackUnlocked(project, paths, onProgress, signal));
}

async function trackUnlocked(project, paths, onProgress, signal) {
  const { manifest, staged, cache } = openState(project);
  const result = newResult();
  const reporter = new Reporter(onProgress);
  try {
    reporter.phase('Tracking', paths.length);
    for (const raw of paths) {
      throwIfAborted(signal);
      const rel = project.toRel(raw);
      const reason = project.untrackableReason(rel);
      if (reason) {
        result.errors.push(`${raw}: ${reason}`);
        reporter.item(raw).done();
        continue;
      }
      const abs = project.abs(rel);
      if (!fileInfo(abs).exists) {
        result.errors.push(`${rel}: file not found`);
        reporter.item(rel).done();
        continue;
      }
      const existing = manifest.find(rel);
      if (!existing) {
        const item = reporter.item(rel);
        if (staged.add(rel)) {
          result.trackedNew++;
          result.newlyTracked.push(rel);
        } else {
          result.skipped++;
        }
        item.done();
        continue;
      }
      // Already in the manifest: nothing to start tracking. Only report a
      // state a person has to decide on - Track never rewrites the manifest.
      const size = fileInfo(abs).size;
      const item = reporter.item(rel, size);
      let hash;
      try {
        hash = await cache.getHash(rel, abs, item.bytes, signal);
      } finally {
        item.done();
      }
      let state = classify(hash, existing.hash, cache.getBaseline(rel));
      if (state !== State.UpToDate && staged.contains(rel)) state = State.Conflicted;
      if (state === State.Conflicted) result.conflicted.push(rel);
      else if (state === State.Outdated) result.outdated.push(rel);
      else result.skipped++;
    }
  } finally {
    staged.save(project.stagedPath);
    if (!updateGitExclude(project, staged.paths)) result.notIgnored.push(...staged.paths);
    updateGitIgnore(project, manifest.files.map((f) => f.path));
    cache.save();
  }
  return result;
}

/**
 * Stages every project file unilfs.track matches that is not tracked yet.
 * With `paths`, only those are considered (what Unity's Auto Track hands
 * over after an import); non-matching ones are skipped silently.
 */
export async function trackMatching(project, { paths, onProgress, signal } = {}) {
  return withLock(project, 'Track', async () => {
    const patterns = TrackPatterns.load(project.trackPath);
    const { matches, alreadyTracked } = findPatternMatches(project, patterns, paths);
    const result = await trackUnlocked(project, matches, onProgress, signal);
    result.skipped += alreadyTracked;
    for (const e of patterns.errors) result.errors.push(`unilfs.track, ${e}`);
    return result;
  });
}

export function findPatternMatches(project, patterns, only) {
  const out = { matches: [], alreadyTracked: 0 };
  if (!patterns || patterns.isEmpty) return out;
  const manifest = Manifest.load(project.manifestPath);
  const staged = StagedPaths.load(project.stagedPath);
  const tracked = new Set([...manifest.files.map((f) => f.path.toLowerCase()), ...staged.paths.map((p) => p.toLowerCase())]);
  const gate = (rel) => project.untrackableReason(rel);
  const candidates = only
    ? [...new Set(only.map((p) => project.toRel(p)).filter((rel) => rel && fileInfo(project.abs(rel)).exists))]
    : enumerateProjectFiles(project);
  for (const rel of candidates) {
    if (!patterns.matches(rel, gate)) continue;
    if (tracked.has(rel.toLowerCase())) out.alreadyTracked++;
    else out.matches.push(rel);
  }
  out.matches.sort(cmpOrdinal);
  return out;
}

/**
 * Walks a directory (default: the project) without entering directories that
 * can never hold a tracked file. Hidden files and directories are skipped.
 * Yields project-relative paths.
 */
export function* enumerateProjectFiles(project, startAbs = project.root) {
  const pending = [startAbs];
  while (pending.length > 0) {
    const dir = pending.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const ent of entries) {
      if (ent.name.startsWith('.')) continue;
      const abs = path.join(dir, ent.name);
      const rel = project.toRel(abs);
      if (rel == null) continue;
      if (ent.isDirectory()) {
        if (!project.untrackableReason(rel)) pending.push(abs);
      } else if (ent.isFile()) {
        yield rel;
      }
    }
  }
}

/** Removes paths from the manifest and from staging. Files stay on disk. */
export async function untrack(project, paths) {
  return withLock(project, 'Untrack', () => {
    const { manifest, staged, cache } = openState(project);
    const result = newResult();
    for (const raw of paths) {
      const rel = project.toRel(raw) ?? raw.replace(/\\/g, '/');
      let removed = manifest.remove(rel);
      removed = staged.remove(rel) || removed;
      if (removed) {
        cache.forget(rel);
        clearPlaceholder(project.abs(rel));
        result.untracked++;
      } else {
        result.notTracked.push(rel);
      }
    }
    manifest.save(project.manifestPath);
    updateGitIgnore(project, manifest.files.map((f) => f.path));
    staged.save(project.stagedPath);
    updateGitExclude(project, staged.paths);
    cache.save();
    return result;
  });
}

/**
 * Records that the local copy wins a conflict ("keep mine"). Uploads nothing:
 * the next Push consumes the decision.
 */
export async function keepLocal(project, paths) {
  return withLock(project, 'Keep Mine', () => {
    const manifest = Manifest.load(project.manifestPath);
    const staged = StagedPaths.load(project.stagedPath);
    const result = newResult();
    for (const raw of paths) {
      const rel = project.toRel(raw) ?? raw.replace(/\\/g, '/');
      if (!manifest.find(rel) && !staged.contains(rel)) {
        result.errors.push(`${rel}: not tracked`);
        continue;
      }
      if (staged.keepLocal(rel)) result.conflicted.push(rel);
    }
    staged.save(project.stagedPath);
    return result;
  });
}

// --------------------------------------------------------------------------
// Push
// --------------------------------------------------------------------------

/**
 * Re-hashes tracked files, uploads blobs storage is missing, and only then
 * records them in the manifest - so a manifest entry always names a blob
 * storage has. Staged paths become manifest entries here.
 *
 * requireBaseline: skip files whose difference from the manifest cannot be
 * attributed to either side (for unattended runs such as git hooks).
 */
export async function push(project, { requireBaseline = false, onProgress, signal, provider } = {}) {
  return withLock(project, 'Push', () => pushUnlocked(project, requireBaseline, onProgress, signal, provider));
}

async function pushUnlocked(project, requireBaseline, onProgress, signal, providerOverride) {
  const { manifest, staged, cache } = openState(project);
  const result = newResult();
  const candidates = manifest.files.map((f) => ({ path: f.path, entry: f }));
  for (const p of staged.paths) if (!manifest.find(p)) candidates.push({ path: p, entry: null });
  if (candidates.length === 0) return result;

  const provider = providerOverride || createProvider(project);
  const current = new Map(); // path -> { hash, size }
  const present = new Set();
  const checkFailed = new Set();
  const remote = RemoteBlobCache.forProject(project);
  const reporter = new Reporter(onProgress);
  const limit = parallelism(project);

  try {
    reporter.phase('Hashing', candidates.length, totalSize(manifest.files), ...PUSH_HASH);
    for (const c of candidates) {
      throwIfAborted(signal);
      const abs = project.abs(c.path);
      const info = fileInfo(abs);
      // A placeholder must never be uploaded: the manifest is rewritten from
      // what is hashed here.
      if (!info.exists || isPlaceholder(abs)) {
        result.missingLocal.push(c.path);
        reporter.item(c.path, c.entry ? c.entry.size : 0).done();
        continue;
      }
      const item = reporter.item(c.path, info.size);
      let hash;
      try {
        hash = await cache.getHash(c.path, abs, item.bytes, signal);
      } finally {
        item.done();
      }
      if (!c.entry) {
        current.set(c.path, { hash, size: info.size });
        continue;
      }
      const baseline = cache.getBaseline(c.path);
      let state = classify(hash, c.entry.hash, baseline);
      if (state !== State.UpToDate && staged.contains(c.path)) state = State.Conflicted;
      const keepMine = state === State.Conflicted && staged.resolvesLocal(c.path);
      if (!keepMine && requireBaseline && !baseline && state !== State.UpToDate) {
        result.unattributed.push(c.path);
        continue;
      }
      if (state === State.Outdated) {
        // Someone else pushed a newer version; pushing this one would roll
        // the manifest back. That is Pull's file, not Push's.
        result.outdated.push(c.path);
        continue;
      }
      if (state === State.Conflicted && !keepMine) {
        result.conflicted.push(c.path);
        continue;
      }
      current.set(c.path, { hash, size: info.size });
    }

    const sourceByHash = new Map();
    for (const [p, cur] of current) sourceByHash.set(cur.hash, p);
    const hashes = [...sourceByHash.keys()];

    // A recorded confirmation is proof; only ask about the rest.
    const toCheck = [];
    for (const h of hashes) {
      if (remote.has(h)) present.add(h);
      else toCheck.push(h);
    }

    reporter.phase('Checking remote', toCheck.length, 0, ...PUSH_CHECK);
    await pool(toCheck, limit, async (h) => {
      const item = reporter.item(sourceByHash.get(h));
      try {
        if (await provider.blobExists(h, signal)) present.add(h);
      } catch (e) {
        if (isAbort(e)) throw e;
        checkFailed.add(h);
        result.errors.push(`check ${sourceByHash.get(h)}: ${e.message}`);
      } finally {
        item.done();
      }
    });

    const toUpload = hashes.filter((h) => !present.has(h) && !checkFailed.has(h));
    const uploadBytes = toUpload.reduce((n, h) => n + current.get(sourceByHash.get(h)).size, 0);
    reporter.phase('Uploading', toUpload.length, uploadBytes, ...PUSH_UPLOAD);
    await pool(toUpload, limit, async (h) => {
      const rel = sourceByHash.get(h);
      const item = reporter.item(rel, current.get(rel).size);
      try {
        await provider.uploadBlob(h, project.abs(rel), item.bytes, signal);
        present.add(h);
        result.uploaded++;
      } catch (e) {
        if (isAbort(e)) throw e;
        result.errors.push(`upload ${rel}: ${e.message}`);
      } finally {
        item.done();
      }
    });
  } finally {
    // Commit only entries whose blob is confirmed in storage. This is the
    // only place a manifest entry is ever written.
    for (const c of candidates) {
      const cur = current.get(c.path);
      if (!cur || !present.has(cur.hash)) continue;
      let entry = c.entry;
      if (!entry) {
        entry = manifest.upsert(c.path, cur.hash, cur.size);
        result.promoted++;
      } else if (entry.hash === cur.hash && entry.size === cur.size) {
        result.skipped++;
      } else {
        entry.hash = cur.hash;
        entry.size = cur.size;
      }
      if (project.mode === 'unity') {
        const guid = readMetaGuid(project.abs(c.path));
        if (guid) entry.guid = guid;
      }
      cache.recordSynced(c.path, cur.hash);
      staged.remove(c.path);
    }
    // Order matters: the committed ignore line goes in before the local
    // one comes out, so the file is never ignored by neither.
    manifest.save(project.manifestPath);
    updateGitIgnore(project, manifest.files.map((f) => f.path));
    staged.save(project.stagedPath);
    if (!updateGitExclude(project, staged.paths)) result.notIgnored.push(...staged.paths);
    cache.save();
    remote.addAll(present);
    remote.save();
  }
  return result;
}

// --------------------------------------------------------------------------
// Pull
// --------------------------------------------------------------------------

/**
 * Downloads files that are missing or outdated (someone pushed a newer
 * version and this copy is the one last synced). With restoreModified,
 * locally modified and conflicted files are overwritten too; otherwise they
 * are left alone and reported.
 *
 * A blob another git worktree of the same repository already has on disk
 * is copied from there (hash-verified) instead of downloaded; pass
 * fromWorktrees: false to always go to storage.
 */
export async function pull(project, { restoreModified = false, onProgress, signal, provider, fromWorktrees = true } = {}) {
  return withLock(project, restoreModified ? 'Restore' : 'Pull',
    () => pullUnlocked(project, restoreModified, onProgress, signal, provider, fromWorktrees));
}

async function pullUnlocked(project, restoreModified, onProgress, signal, providerOverride, fromWorktrees) {
  const { manifest, staged, cache } = openState(project);
  const result = newResult();
  if (manifest.files.length === 0) return result;
  const remote = RemoteBlobCache.forProject(project);
  const reporter = new Reporter(onProgress);
  try {
    const statuses = await statusInternal(project, manifest, staged, cache, remote, reporter, signal, PULL_STATUS);
    const targets = statuses.filter((s) => s.needsDownload
      || (restoreModified && !!s.hash && (s.state === State.Modified || s.state === State.Conflicted)));
    for (const s of statuses) {
      if (s.state === State.UpToDate) result.skipped++;
      else if (s.state === State.Modified && !restoreModified) result.keptModified.push(s.path);
      else if (s.state === State.Conflicted && !restoreModified) result.conflicted.push(s.path);
    }
    if (targets.length === 0) return result;

    // Created on first use: a pull served entirely from other worktrees
    // never needs storage.
    let provider = providerOverride || null;
    const local = fromWorktrees ? new LocalSources(project) : null;
    fs.mkdirSync(project.tmpDir, { recursive: true });
    const groups = new Map();
    for (const t of targets) {
      if (!groups.has(t.hash)) groups.set(t.hash, []);
      groups.get(t.hash).push(t);
    }
    const groupList = [...groups.entries()];
    reporter.phase('Downloading', groupList.length, groupList.reduce((n, [, g]) => n + g[0].size, 0), ...PULL_DOWNLOAD);
    await pool(groupList, parallelism(project), async ([hash, group]) => {
      const tmpBlob = `${project.tmpDir}/${hash}.blob`;
      const first = group[0];
      const item = reporter.item(first.path, first.size);
      try {
        let copied = false;
        if (local && !local.empty) {
          for (const src of local.candidates(hash, first.size, group.map((t) => t.path))) {
            try {
              await downloadVerified(fs.createReadStream(src), hash, tmpBlob, item.bytes, signal);
              copied = true;
              break;
            } catch (e) {
              if (isAbort(e)) throw e;
              // changed since it was hashed, or gone: try the next one
            }
          }
        }
        if (!copied) {
          if (!provider) provider = createProvider(project);
          await provider.downloadBlob(hash, tmpBlob, item.bytes, signal);
          remote.add(hash);
        }
        for (const target of group) {
          const abs = project.abs(target.path);
          fs.mkdirSync(path.dirname(abs), { recursive: true });
          // Put a discarded .meta back before the asset lands, or Unity
          // mints a new GUID on import and every reference breaks.
          if (project.mode === 'unity') writeMinimalMeta(abs, target.guid);
          if (fs.existsSync(abs)) makeWritable(abs);
          fs.copyFileSync(tmpBlob, abs);
          cache.recordKnownFromDisk(target.path, abs, hash);
          cache.recordSynced(target.path, hash);
          staged.remove(target.path);
          if (copied) result.copiedLocal++;
          else result.downloaded++;
        }
      } catch (e) {
        if (isAbort(e)) throw e;
        for (const target of group) result.errors.push(`download ${target.path}: ${e.message}`);
      } finally {
        try { fs.rmSync(tmpBlob, { force: true }); } catch { /* ignore */ }
        item.done();
      }
    });
  } finally {
    cache.save();
    remote.save();
    staged.save(project.stagedPath);
    updateGitExclude(project, staged.paths);
  }
  return result;
}

// --------------------------------------------------------------------------
// Verify
// --------------------------------------------------------------------------

/**
 * Checks every blob the manifest references exists in storage, without
 * downloading. Needs no local files, only credentials - a CI gate.
 * `manifests` checks those instead of the working tree's (the pre-push hook
 * passes the manifests of the commits being pushed).
 */
export async function verify(project, { onProgress, signal, provider, manifests } = {}) {
  return withLock(project, 'Verify', async () => {
    let manifest = Manifest.load(project.manifestPath);
    if (manifests) {
      manifest = new Manifest();
      for (const m of manifests) manifest.files.push(...m.files);
    }
    const result = newResult();
    if (manifest.files.length === 0) return result;
    const byHash = pathsByHash(manifest);
    const remote = RemoteBlobCache.forProject(project);
    try {
      const check = await checkRemote(project, byHash, remote, new Reporter(onProgress), signal, provider, [0, 1]);
      result.skipped = check.present.length;
      for (const h of check.missing) {
        result.errors.push(`missing on remote: ${byHash.get(h).join(', ')} (${h.slice(0, 8)}...)`);
      }
      result.errors.push(...check.failures);
    } finally {
      remote.save();
    }
    return result;
  });
}

async function checkRemote(project, byHash, remote, reporter, signal, providerOverride, slice) {
  const check = { present: [], missing: [], failures: [] };
  const hashes = [...byHash.keys()];
  reporter.phase('Verifying remote', hashes.length, 0, ...slice);
  if (hashes.length === 0) return check;
  const provider = providerOverride || createProvider(project);
  await pool(hashes, parallelism(project), async (h) => {
    const item = reporter.item(byHash.get(h)[0]);
    try {
      if (await provider.blobExists(h, signal)) {
        remote.add(h);
        check.present.push(h);
      } else {
        remote.remove(h);
        check.missing.push(h);
      }
    } catch (e) {
      if (isAbort(e)) throw e;
      check.failures.push(`check ${byHash.get(h).join(', ')}: ${e.message}`);
    } finally {
      item.done();
    }
  });
  return check;
}

export async function testConnection(project, { signal, provider } = {}) {
  const p = provider || createProvider(project);
  return p.testConnection(signal);
}

export function gitRemoveHint(paths) {
  if (!paths.length) return null;
  return ['If these files were previously committed to git, remove them from the index (files stay on disk):',
    ...paths.map((p) => `  git rm --cached -- "${p}"`)].join('\n');
}
