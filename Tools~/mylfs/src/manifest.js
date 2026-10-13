import fs from 'node:fs';
import { cmpOrdinal, writeFileAtomic } from './util.js';

/**
 * unilfs.manifest.json - the committed map from project-relative path to
 * SHA-256. Written one line per file, sorted, byte-identical to what the
 * Unity package writes, so the two never produce a diff against each other.
 */
export class Manifest {
  constructor() {
    this.version = 1;
    /** @type {{path:string, hash:string, size:number, guid?:string}[]} */
    this.files = [];
  }

  static load(file) {
    if (!fs.existsSync(file)) return new Manifest();
    return Manifest.parse(fs.readFileSync(file, 'utf8'), file);
  }

  static parse(text, source = 'manifest') {
    const m = new Manifest();
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    let data;
    try {
      data = JSON.parse(text);
    } catch (e) {
      throw new Error(`UniLFS: failed to parse ${source}: ${e.message}`);
    }
    if (data && typeof data === 'object') {
      if (Number.isInteger(data.version)) m.version = data.version;
      if (Array.isArray(data.files)) {
        for (const f of data.files) {
          if (!f || typeof f.path !== 'string' || f.path === '') continue;
          const entry = { path: f.path, hash: typeof f.hash === 'string' ? f.hash : '', size: Number(f.size) || 0 };
          if (typeof f.guid === 'string' && f.guid) entry.guid = f.guid;
          m.files.push(entry);
        }
      }
    }
    m.sort();
    return m;
  }

  save(file) {
    writeFileAtomic(file, this.toJsonString());
  }

  sort() {
    this.files.sort((a, b) => cmpOrdinal(a.path, b.path));
  }

  find(p) {
    return this.files.find((f) => f.path === p) || null;
  }

  upsert(p, hash, size) {
    let entry = this.find(p);
    if (!entry) {
      entry = { path: p, hash: '', size: 0 };
      this.files.push(entry);
    }
    entry.hash = hash;
    entry.size = size;
    return entry;
  }

  remove(p) {
    const before = this.files.length;
    this.files = this.files.filter((f) => f.path !== p);
    return this.files.length !== before;
  }

  toJsonString() {
    this.sort();
    let s = '{\n';
    s += `  "version": ${this.version},\n`;
    s += '  "files": [';
    this.files.forEach((f, i) => {
      s += i === 0 ? '\n' : ',\n';
      s += `    {"path": ${JSON.stringify(f.path)}, "hash": ${JSON.stringify(f.hash || '')}, "size": ${f.size}`;
      // Omitted rather than written empty, exactly as the Unity package does.
      if (f.guid) s += `, "guid": ${JSON.stringify(f.guid)}`;
      s += '}';
    });
    s += this.files.length > 0 ? '\n  ]\n' : ']\n';
    s += '}\n';
    return s;
  }
}
