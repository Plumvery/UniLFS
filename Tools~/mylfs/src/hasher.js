import fs from 'node:fs';
import crypto from 'node:crypto';
import { throwIfAborted } from './util.js';

export const BUFFER_SIZE = 1024 * 1024;

export function sha256File(absPath, onBytes, signal) {
  return new Promise((resolve, reject) => {
    throwIfAborted(signal);
    const hash = crypto.createHash('sha256');
    const stream = fs.createReadStream(absPath, { highWaterMark: BUFFER_SIZE });
    let done = 0;
    const onAbort = () => {
      const e = new Error('Operation cancelled.');
      e.name = 'AbortError';
      stream.destroy(e);
    };
    if (signal) signal.addEventListener('abort', onAbort, { once: true });
    stream.on('data', (chunk) => {
      hash.update(chunk);
      done += chunk.length;
      if (onBytes) onBytes(done);
    });
    stream.on('error', (e) => {
      if (signal) signal.removeEventListener('abort', onAbort);
      reject(e);
    });
    stream.on('end', () => {
      if (signal) signal.removeEventListener('abort', onAbort);
      resolve(hash.digest('hex'));
    });
  });
}
