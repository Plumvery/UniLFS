import fs from 'node:fs';
import { writeFileAtomic } from './util.js';
import { sha256File } from './hasher.js';

// .NET DateTime ticks (100 ns since 0001-01-01) at the Unix epoch.
const EPOCH_TICKS = 621355968000000000n;

/** mtime as .NET ticks, the unit the Unity package's cache stores. */
export function mtimeTicks(statBig) {
  return statBig.mtimeNs / 100n + EPOCH_TICKS;
}

/**
 * Per-machine record per tracked file:
 *  - (mtime, size) -> SHA-256, so unchanged files are not re-hashed;
 *  - the sync baseline: the manifest hash this machine last agreed with,
 *    which is what tells "I edited it" (Modified) from "someone pushed a
 *    newer one" (Outdated).
 *
 * Same JSON shape as Library/UniLFS/statecache.json. mtimeTicks exceeds
 * 2^53, so it is kept as a BigInt and read/written without going through
 * a lossy JS number.
 */
export class StateCache {
  constructor(file) {
    this.file = file;
    /** @type {Map<string, {path:string, mtimeTicks:bigint, size:number, hash:string, baseHash:string}>} */
    this.entries = new Map();
    this.dirty = false;
    this.load();
  }

  load() {
    try {
      if (!fs.existsSync(this.file)) return;
      const text = fs.readFileSync(this.file, 'utf8').replace(/"mtimeTicks"\s*:\s*(-?\d+)/g, '"mtimeTicks":"$1"');
      const data = JSON.parse(text);
      if (!data || !Array.isArray(data.entries)) return;
      for (const e of data.entries) {
        if (!e || typeof e.path !== 'string' || !e.path) continue;
        let ticks = 0n;
        try { ticks = BigInt(e.mtimeTicks || 0); } catch { /* keep 0 */ }
        this.entries.set(e.path, {
          path: e.path,
          mtimeTicks: ticks,
          size: Number(e.size) || 0,
          hash: e.hash || '',
          baseHash: e.baseHash || '',
        });
      }
    } catch {
      this.entries = new Map();
    }
  }

  save() {
    if (!this.dirty) return;
    const parts = [];
    for (const e of this.entries.values()) {
      parts.push(`{"path":${JSON.stringify(e.path)},"mtimeTicks":${e.mtimeTicks.toString()},"size":${e.size},`
        + `"hash":${JSON.stringify(e.hash || '')},"baseHash":${JSON.stringify(e.baseHash || '')}}`);
    }
    writeFileAtomic(this.file, `{"version":1,"entries":[${parts.join(',')}]}`);
    this.dirty = false;
  }

  getOrCreate(p) {
    let e = this.entries.get(p);
    if (!e) {
      e = { path: p, mtimeTicks: 0n, size: 0, hash: '', baseHash: '' };
      this.entries.set(p, e);
    }
    return e;
  }

  /** SHA-256 of the file, from the cache when mtime and size still match. */
  async getHash(p, absPath, onBytes, signal) {
    const st = fs.statSync(absPath, { bigint: true });
    const ticks = mtimeTicks(st);
    const size = Number(st.size);
    const e = this.entries.get(p);
    if (e && e.mtimeTicks === ticks && e.size === size && e.hash) return e.hash;
    const hash = await sha256File(absPath, onBytes, signal);
    this.recordKnown(p, ticks, size, hash);
    return hash;
  }

  recordKnown(p, ticks, size, hash) {
    const e = this.getOrCreate(p);
    e.mtimeTicks = ticks;
    e.size = size;
    e.hash = hash;
    this.dirty = true;
  }

  recordKnownFromDisk(p, absPath, hash) {
    try {
      const st = fs.statSync(absPath, { bigint: true });
      this.recordKnown(p, mtimeTicks(st), Number(st.size), hash);
    } catch { /* gone again: nothing to record */ }
  }

  /** Local content and the manifest agree on this hash (after Push or Pull). */
  recordSynced(p, manifestHash) {
    if (!manifestHash) return;
    const e = this.getOrCreate(p);
    if (e.baseHash === manifestHash) return;
    e.baseHash = manifestHash;
    this.dirty = true;
  }

  getBaseline(p) {
    const e = this.entries.get(p);
    return e && e.baseHash ? e.baseHash : null;
  }

  forget(p) {
    if (this.entries.delete(p)) this.dirty = true;
  }
}
