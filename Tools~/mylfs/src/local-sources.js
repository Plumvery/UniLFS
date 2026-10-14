import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { StateCache } from './state-cache.js';
import { norm } from './util.js';

function git(cwd, ...args) {
  try {
    const r = spawnSync('git', args, { cwd, encoding: 'utf8' });
    return r.status === 0 ? r.stdout : null;
  } catch {
    return null;
  }
}

function samePath(a, b) {
  const x = norm(path.resolve(a)).replace(/\/+$/, '');
  const y = norm(path.resolve(b)).replace(/\/+$/, '');
  return process.platform === 'win32' || process.platform === 'darwin'
    ? x.toLowerCase() === y.toLowerCase()
    : x === y;
}

/** Working-tree roots of every other checkout of this repository (`git worktree list`). */
export function otherWorktrees(cwd) {
  const top = git(cwd, 'rev-parse', '--show-toplevel');
  const list = git(cwd, 'worktree', 'list', '--porcelain');
  if (!top || !list) return [];
  const roots = [];
  let current = null;
  for (const line of list.split(/\r?\n/)) {
    if (line.startsWith('worktree ')) {
      current = { root: line.slice('worktree '.length), bare: false };
      roots.push(current);
    } else if (line === 'bare' && current) {
      current.bare = true;
    }
  }
  return roots.filter((w) => !w.bare && !samePath(w.root, top.trim())).map((w) => w.root);
}

/**
 * Other git worktrees of the same repository, seen as read-only places a
 * blob may already sit on this disk - so a fresh `git worktree add` pulls
 * from the main checkout instead of the network. Nothing found here is
 * trusted: every candidate is hashed while it is copied, and a mismatch
 * just falls through to the next candidate or to storage.
 */
export class LocalSources {
  constructor(project) {
    /** @type {Map<string, string[]>} hash -> absolute paths that last hashed to it */
    this.byHash = new Map();
    /** @type {string[]} project roots inside the other worktrees */
    this.roots = [];
    const top = git(project.root, 'rev-parse', '--show-toplevel');
    if (!top) return;
    const rel = path.relative(path.resolve(top.trim()), project.root);
    const cacheRel = path.relative(project.root, project.stateCachePath);
    for (const wt of otherWorktrees(project.root)) {
      const root = path.join(wt, rel);
      if (!fs.existsSync(root)) continue;
      this.roots.push(root);
      const cache = new StateCache(path.join(root, cacheRel));
      for (const e of cache.entries.values()) {
        if (!e.hash) continue;
        if (!this.byHash.has(e.hash)) this.byHash.set(e.hash, []);
        this.byHash.get(e.hash).push(path.join(root, e.path));
      }
    }
  }

  get empty() {
    return this.roots.length === 0;
  }

  /**
   * Paths worth trying for a blob, most likely first: files the other
   * checkouts' state caches say hash to it, then the same paths in those
   * checkouts (a cache can be missing). Only files of the expected size.
   */
  candidates(hash, size, relPaths) {
    const seen = new Set();
    const out = [];
    const add = (p) => {
      const key = norm(p);
      if (seen.has(key)) return;
      seen.add(key);
      try {
        const st = fs.statSync(p);
        if (st.isFile() && st.size === size) out.push(p);
      } catch { /* not there */ }
    };
    for (const p of this.byHash.get(hash) || []) add(p);
    for (const root of this.roots) for (const rel of relPaths) add(path.join(root, rel));
    return out;
  }
}
