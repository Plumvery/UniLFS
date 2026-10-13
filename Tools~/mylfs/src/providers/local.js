import fs from 'node:fs';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { downloadVerified, ConfigError } from './http.js';
import { BUFFER_SIZE } from '../hasher.js';

/**
 * Blobs in a plain directory - a NAS share, a synced folder, an external
 * disk - at <localPath>/objects/<aa>/<sha256>, the same layout as the S3
 * keys. Not understood by the Unity package (it only knows S3 and Drive).
 */
export class LocalProvider {
  constructor({ localPath, projectRoot }) {
    if (!localPath || !localPath.trim()) throw new ConfigError('Storage folder is not set (localPath). Run "mylfs config set localPath <dir>".');
    this.dir = path.resolve(projectRoot || process.cwd(), localPath.trim());
    this.displayName = `local folder (${this.dir})`;
  }

  blobPath(hash) {
    return path.join(this.dir, 'objects', hash.slice(0, 2), hash);
  }

  async blobExists(hash) {
    try {
      return (await fs.promises.stat(this.blobPath(hash))).isFile();
    } catch (e) {
      if (e.code === 'ENOENT' || e.code === 'ENOTDIR') return false;
      throw e;
    }
  }

  async uploadBlob(hash, srcAbsPath, onBytes, signal) {
    const dest = this.blobPath(hash);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    const tmp = `${dest}.${process.pid}.partial`;
    const rs = fs.createReadStream(srcAbsPath, { highWaterMark: BUFFER_SIZE });
    let total = 0;
    if (onBytes) rs.on('data', (c) => { total += c.length; onBytes(total); });
    try {
      await pipeline(rs, fs.createWriteStream(tmp), ...(signal ? [{ signal }] : []));
      fs.renameSync(tmp, dest);
    } finally {
      try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
    }
  }

  async downloadBlob(hash, destAbsPath, onBytes, signal) {
    const src = this.blobPath(hash);
    if (!fs.existsSync(src)) throw new Error(`Blob ${hash.slice(0, 8)}... was not found in ${this.dir}.`);
    await downloadVerified(fs.createReadStream(src, { highWaterMark: BUFFER_SIZE }), hash, destAbsPath, onBytes, signal);
  }

  async testConnection() {
    fs.mkdirSync(path.join(this.dir, 'objects'), { recursive: true });
    const probe = path.join(this.dir, 'objects', `.unilfs-probe-${process.pid}`);
    fs.writeFileSync(probe, 'ok');
    fs.rmSync(probe, { force: true });
    return `Connected to ${this.displayName}. The folder is writable.`;
  }
}
