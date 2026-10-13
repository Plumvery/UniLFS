import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

/** Same order as C#'s string.CompareOrdinal: UTF-16 code units. */
export function cmpOrdinal(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function norm(p) {
  return p == null ? p : p.replace(/\\/g, '/');
}

export function sha256Hex(text) {
  return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
}

export function exists(p) {
  try {
    fs.statSync(p);
    return true;
  } catch {
    return false;
  }
}

export function isFile(p) {
  try {
    return fs.statSync(p).isFile();
  } catch {
    return false;
  }
}

export function isDir(p) {
  try {
    return fs.statSync(p).isDirectory();
  } catch {
    return false;
  }
}

/** Write to a sibling temp file, then rename over the target. */
export function writeFileAtomic(file, text) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = file + '.tmp';
  fs.writeFileSync(tmp, text, 'utf8');
  fs.renameSync(tmp, file);
}

export function readJsonOr(file, fallback) {
  try {
    if (!fs.existsSync(file)) return fallback;
    let text = fs.readFileSync(file, 'utf8');
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    return JSON.parse(text);
  } catch {
    return fallback;
  }
}

/**
 * Runs fn over items with at most `limit` in flight. fn is expected to
 * handle its own per-item failures; anything it throws (an abort) rejects
 * the whole pool.
 */
export async function pool(items, limit, fn) {
  let next = 0;
  const width = Math.min(Math.max(1, limit | 0), items.length);
  const workers = [];
  for (let w = 0; w < width; w++) {
    workers.push((async () => {
      while (next < items.length) {
        const item = items[next++];
        await fn(item);
      }
    })());
  }
  await Promise.all(workers);
}

export function isAbort(e) {
  return e && (e.name === 'AbortError' || e.code === 'ABORT_ERR');
}

export function throwIfAborted(signal) {
  if (signal && signal.aborted) {
    const e = new Error('Operation cancelled.');
    e.name = 'AbortError';
    throw e;
  }
}

export function formatBytes(n) {
  if (!Number.isFinite(n)) return '?';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024;
    i++;
  }
  return (i === 0 ? n.toString() : n.toFixed(1)) + ' ' + units[i];
}

/** Clear a read-only flag before overwriting, as UniLFS does with File.SetAttributes(Normal). */
export function makeWritable(p) {
  try {
    fs.chmodSync(p, 0o666);
  } catch {
    // not there, or not ours to change: the write that follows will say so
  }
}
