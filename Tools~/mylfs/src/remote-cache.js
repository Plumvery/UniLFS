import { readJsonOr, writeFileAtomic } from './util.js';

/**
 * Hashes known to exist in remote storage, recorded whenever a Push, Pull
 * or Verify proves it. Lets status say "up to date" vs "not pushed" without
 * a network call. Same file (remote-<fingerprint>.json) as the Unity package.
 */
export class RemoteBlobCache {
  static MAX_UNREFERENCED = 4096;

  constructor(file) {
    this.file = file;
    this.order = [];
    this.blobs = new Set();
    this.dirty = false;
    const data = readJsonOr(file, null);
    if (data && Array.isArray(data.blobs)) {
      for (const h of data.blobs) {
        if (typeof h === 'string' && h && !this.blobs.has(h)) {
          this.blobs.add(h);
          this.order.push(h);
        }
      }
    }
  }

  static forProject(project) {
    return new RemoteBlobCache(`${project.cacheDir}/remote-${project.storageFingerprint()}.json`);
  }

  has(hash) {
    return !!hash && this.blobs.has(hash);
  }

  add(hash) {
    if (!hash || this.blobs.has(hash)) return;
    this.blobs.add(hash);
    this.order.push(hash);
    this.dirty = true;
  }

  addAll(hashes) {
    for (const h of hashes) this.add(h);
  }

  /** Only on a definitive "not there" - a failed check proves nothing. */
  remove(hash) {
    if (!hash || !this.blobs.delete(hash)) return;
    this.order = this.order.filter((h) => h !== hash);
    this.dirty = true;
  }

  /** Keeps everything the manifest references plus the newest MAX_UNREFERENCED others. */
  prune(manifestHashes) {
    const keep = new Set(manifestHashes.filter(Boolean));
    let unreferenced = this.order.filter((h) => !keep.has(h)).length;
    if (unreferenced <= RemoteBlobCache.MAX_UNREFERENCED) return;
    let drop = unreferenced - RemoteBlobCache.MAX_UNREFERENCED;
    const kept = [];
    for (const h of this.order) {
      if (drop > 0 && !keep.has(h)) {
        drop--;
        continue;
      }
      kept.push(h);
    }
    this.order = kept;
    this.blobs = new Set(kept);
    this.dirty = true;
  }

  save() {
    if (!this.dirty) return;
    writeFileAtomic(this.file, JSON.stringify({ version: 1, blobs: this.order }));
    this.dirty = false;
  }
}
