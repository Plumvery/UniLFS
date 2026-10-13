import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { pipeline } from 'node:stream/promises';
import { Transform } from 'node:stream';
import { BUFFER_SIZE } from '../hasher.js';

export class StorageError extends Error {
  constructor(message) {
    super(message);
    this.name = 'StorageError';
  }
}

export class ConfigError extends Error {
  constructor(message) {
    super(message);
    this.name = 'ConfigError';
  }
}

const agents = {
  'http:': new http.Agent({ keepAlive: true }),
  'https:': new https.Agent({ keepAlive: true }),
};

const USER_AGENT = 'UniLFS-node/0.1';

function abortError() {
  const e = new Error('Operation cancelled.');
  e.name = 'AbortError';
  return e;
}

/**
 * One HTTP request. `body` is a string/Buffer, or { file, onBytes } to
 * stream a file (the caller sets Content-Length). Resolves once headers
 * arrive; the response stream is in `res`. Follows redirects for bodiless
 * requests only. No timeout of its own: multi-gigabyte transfers must not
 * hit one, and the abort signal is the only way to stop a request.
 */
export function request(method, url, { headers = {}, body, signal, redirects = 5 } = {}) {
  return new Promise((resolve, reject) => {
    if (signal && signal.aborted) return reject(abortError());
    const u = new URL(url);
    const lib = u.protocol === 'http:' ? http : https;
    const h = { 'user-agent': USER_AGENT, ...headers };
    if ((typeof body === 'string' || Buffer.isBuffer(body)) && h['content-length'] == null && h['Content-Length'] == null) {
      h['content-length'] = Buffer.byteLength(body);
    }
    const req = lib.request(u, { method, headers: h, agent: agents[u.protocol] });
    const onAbort = () => req.destroy(abortError());
    if (signal) signal.addEventListener('abort', onAbort, { once: true });
    const cleanup = () => signal && signal.removeEventListener('abort', onAbort);

    req.on('error', (e) => {
      cleanup();
      reject(e);
    });
    req.on('response', (res) => {
      const status = res.statusCode || 0;
      if (status >= 300 && status < 400 && res.headers.location && !(body && body.file) && redirects > 0) {
        res.resume();
        cleanup();
        const next = new URL(res.headers.location, u).toString();
        resolve(request(status === 303 ? 'GET' : method, next, { headers, body, signal, redirects: redirects - 1 }));
        return;
      }
      res.on('close', cleanup);
      resolve({ status, statusText: res.statusMessage || '', headers: res.headers, res });
    });

    if (body && body.file) {
      const rs = fs.createReadStream(body.file, { highWaterMark: BUFFER_SIZE });
      let total = 0;
      if (body.onBytes) rs.on('data', (c) => { total += c.length; body.onBytes(total); });
      pipeline(rs, req).catch((e) => req.destroy(e));
    } else {
      req.end(body);
    }
  });
}

export async function readText(r) {
  const chunks = [];
  for await (const c of r.res) chunks.push(c);
  return Buffer.concat(chunks).toString('utf8');
}

export function drain(r) {
  r.res.resume();
}

/**
 * Streams a body to dest while hashing it; only a verified file is renamed
 * into place, so a corrupted download never replaces anything.
 */
export async function downloadVerified(stream, expectedSha256, destAbsPath, onBytes, signal) {
  fs.mkdirSync(path.dirname(destAbsPath), { recursive: true });
  const tmp = destAbsPath + '.unilfs-partial';
  const sha = crypto.createHash('sha256');
  let total = 0;
  const tap = new Transform({
    transform(chunk, _enc, cb) {
      sha.update(chunk);
      total += chunk.length;
      if (onBytes) onBytes(total);
      cb(null, chunk);
    },
  });
  try {
    await pipeline(stream, tap, fs.createWriteStream(tmp), ...(signal ? [{ signal }] : []));
    const actual = sha.digest('hex');
    if (actual.toLowerCase() !== expectedSha256.toLowerCase()) {
      throw new StorageError(`Downloaded content hash mismatch (expected ${expectedSha256}, got ${actual}). The remote object may be corrupted.`);
    }
    fs.renameSync(tmp, destAbsPath);
  } finally {
    try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
  }
}
