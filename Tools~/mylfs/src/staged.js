import fs from 'node:fs';
import { cmpOrdinal, writeFileAtomic, readJsonOr } from './util.js';

/**
 * unilfs.staged.json - what this machine asked to track and has not pushed
 * yet. Intent only: no hashes, sizes or GUIDs. Never committed.
 */
export class StagedPaths {
  constructor() {
    this.version = 1;
    /** @type {string[]} */
    this.paths = [];
    /** Paths whose local copy wins a conflict at the next Push ("keep mine"). */
    this.resolveLocal = [];
  }

  static load(file) {
    const s = new StagedPaths();
    // Unreadable staging costs the intent to track those paths, and
    // re-running Track restores it; failing here would block Push entirely.
    const data = readJsonOr(file, null);
    if (data && typeof data === 'object') {
      if (Array.isArray(data.paths)) s.paths = data.paths.filter((p) => typeof p === 'string' && p);
      if (Array.isArray(data.resolveLocal)) s.resolveLocal = data.resolveLocal.filter((p) => typeof p === 'string' && p);
    }
    s.paths = [...new Set(s.paths)];
    s.resolveLocal = [...new Set(s.resolveLocal)];
    s.sort();
    return s;
  }

  /** Writes the file, or deletes it once nothing is staged. */
  save(file) {
    if (this.paths.length === 0 && this.resolveLocal.length === 0) {
      try { fs.rmSync(file, { force: true }); } catch { /* ignore */ }
      return;
    }
    writeFileAtomic(file, this.toJsonString());
  }

  sort() {
    this.paths.sort(cmpOrdinal);
    this.resolveLocal.sort(cmpOrdinal);
  }

  contains(p) {
    return this.paths.includes(p);
  }

  resolvesLocal(p) {
    return this.resolveLocal.includes(p);
  }

  add(p) {
    if (!p || this.paths.includes(p)) return false;
    this.paths.push(p);
    return true;
  }

  keepLocal(p) {
    if (!p || this.resolveLocal.includes(p)) return false;
    this.resolveLocal.push(p);
    return true;
  }

  remove(p) {
    const before = this.paths.length + this.resolveLocal.length;
    this.paths = this.paths.filter((x) => x !== p);
    this.resolveLocal = this.resolveLocal.filter((x) => x !== p);
    return this.paths.length + this.resolveLocal.length !== before;
  }

  toJsonString() {
    this.sort();
    const list = (name, values, comma) => {
      let s = `  "${name}": [`;
      values.forEach((v, i) => {
        s += (i === 0 ? '\n    ' : ',\n    ') + JSON.stringify(v);
      });
      s += values.length > 0 ? '\n  ]' : ']';
      return s + (comma ? ',\n' : '\n');
    };
    return '{\n'
      + `  "version": ${this.version},\n`
      + list('paths', this.paths, true)
      + list('resolveLocal', this.resolveLocal, false)
      + '}\n';
  }
}
