import fs from 'node:fs';
import path from 'node:path';

export class BusyError extends Error {
  constructor(running, pid) {
    super(`Another UniLFS operation (${running}, pid ${pid}) is already running in this project. Wait for it to finish and try again.`);
    this.code = 'UNILFS_BUSY';
  }
}

function alive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return e.code === 'EPERM';
  }
}

/**
 * Cross-process mutual exclusion for operations that rewrite the manifest,
 * the ignore blocks and the caches. Two git hooks firing back to back (or a
 * hook and a manual run) would otherwise each save the manifest and drop
 * the other's entries. A lock left by a dead process is taken over.
 *
 * Only covers this CLI: the Unity package's own lock is in-process, so do
 * not run the CLI on a checkout while the editor is pushing or pulling.
 */
export function acquireLock(project, name) {
  const file = project.lockPath;
  fs.mkdirSync(path.dirname(file), { recursive: true });
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const fd = fs.openSync(file, 'wx');
      fs.writeSync(fd, JSON.stringify({ pid: process.pid, op: name, at: new Date().toISOString() }));
      fs.closeSync(fd);
      let released = false;
      const release = () => {
        if (released) return;
        released = true;
        try { fs.rmSync(file, { force: true }); } catch { /* ignore */ }
        process.removeListener('exit', release);
      };
      process.on('exit', release);
      return release;
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      let holder = null;
      try { holder = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { /* half-written or gone */ }
      if (holder && holder.pid && holder.pid !== process.pid && alive(holder.pid)) {
        throw new BusyError(holder.op || 'unknown', holder.pid);
      }
      try { fs.rmSync(file, { force: true }); } catch { /* raced: retry decides */ }
    }
  }
  throw new BusyError('unknown', '?');
}

export async function withLock(project, name, fn) {
  const release = acquireLock(project, name);
  try {
    return await fn();
  } finally {
    release();
  }
}
