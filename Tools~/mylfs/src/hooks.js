import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { findGit, writeBlock, readBlock } from './ignore.js';
import { norm } from './util.js';
import { Manifest } from './manifest.js';
import { MANIFEST_FILE } from './project.js';

export const HOOK_BEGIN = '# >>> UniLFS hook >>>';
export const HOOK_END = '# <<< UniLFS hook <<<';

/**
 * Outside Unity there is no editor to notice a `git pull` or a branch
 * switch, so git itself has to say so:
 *
 *   post-checkout, post-merge  pull what the new manifest names
 *   pre-push                   refuse to push a manifest naming missing blobs
 *   pre-commit (opt-in)        push changed files first, then stage the manifest
 */
export const DEFAULT_HOOKS = ['post-checkout', 'post-merge', 'pre-push'];
export const OPTIONAL_HOOKS = ['pre-commit'];

const CLI_PATH = norm(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'bin', 'mylfs.js'));

export function hooksDir(project) {
  const git = findGit(project.root);
  if (!git) return null;
  // Respect core.hooksPath (husky and friends set it).
  try {
    const r = spawnSync('git', ['config', '--get', 'core.hooksPath'], { cwd: project.root, encoding: 'utf8' });
    const configured = (r.stdout || '').trim();
    if (r.status === 0 && configured) return norm(path.resolve(git.repoRoot, configured));
  } catch { /* git missing: use the default location */ }
  return git.commonDir + '/hooks';
}

function shQuote(s) {
  return `"${String(s).replace(/(["\\$`])/g, '\\$1')}"`;
}

function hookBody(project, name) {
  const node = norm(process.execPath);
  const cmd = `${shQuote(node)} ${shQuote(CLI_PATH)} --root ${shQuote(project.root)} hook ${name} "$@"`;
  // post-* hooks cannot stop anything, so failing them only adds noise;
  // pre-* hooks exit non-zero to stop the commit or push.
  const tail = name.startsWith('pre-') ? ' || exit $?' : ' || true';
  return [HOOK_BEGIN, cmd + tail, HOOK_END];
}

export function installHooks(project, { preCommit = false } = {}) {
  const dir = hooksDir(project);
  if (!dir) throw new Error('This project is not inside a git checkout; there is nowhere to install hooks.');
  fs.mkdirSync(dir, { recursive: true });
  const names = [...DEFAULT_HOOKS, ...(preCommit ? OPTIONAL_HOOKS : [])];
  const installed = [];
  for (const name of names) {
    const file = path.join(dir, name);
    if (!fs.existsSync(file)) fs.writeFileSync(file, '#!/bin/sh\n', 'utf8');
    writeBlock(file, HOOK_BEGIN, HOOK_END, hookBody(project, name));
    try { fs.chmodSync(file, 0o755); } catch { /* Windows: git for Windows runs it anyway */ }
    installed.push(norm(file));
  }
  return installed;
}

export function uninstallHooks(project) {
  const dir = hooksDir(project);
  if (!dir) return [];
  const removed = [];
  for (const name of [...DEFAULT_HOOKS, ...OPTIONAL_HOOKS]) {
    const file = path.join(dir, name);
    if (!fs.existsSync(file)) continue;
    const text = fs.readFileSync(file, 'utf8');
    if (!text.includes(HOOK_BEGIN)) continue;
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const b = lines.findIndex((l) => l.trim() === HOOK_BEGIN);
    const e = lines.findIndex((l, i) => i > b && l.trim() === HOOK_END);
    if (b < 0 || e < 0) continue;
    lines.splice(b, e - b + 1);
    const rest = lines.join('\n').trim();
    if (rest === '' || rest === '#!/bin/sh') fs.rmSync(file, { force: true });
    else fs.writeFileSync(file, rest + '\n', 'utf8');
    removed.push(norm(file));
  }
  return removed;
}

export function installedHooks(project) {
  const dir = hooksDir(project);
  if (!dir) return [];
  return [...DEFAULT_HOOKS, ...OPTIONAL_HOOKS].filter((name) => {
    const file = path.join(dir, name);
    return fs.existsSync(file) && readBlock(file, HOOK_BEGIN, HOOK_END).length > 0;
  });
}

/**
 * The manifests of the commits a `git push` is about to send, read from the
 * pre-push hook's stdin ("<local ref> <local sha> <remote ref> <remote sha>"
 * per line). Branch deletions and commits without a manifest are skipped.
 */
export function manifestsBeingPushed(project, stdinText) {
  const manifests = [];
  const seen = new Set();
  for (const line of stdinText.split(/\r?\n/)) {
    const parts = line.trim().split(/\s+/);
    if (parts.length < 4) continue;
    const sha = parts[1];
    if (/^0+$/.test(sha) || seen.has(sha)) continue;
    seen.add(sha);
    const r = spawnSync('git', ['show', `${sha}:./${MANIFEST_FILE}`], { cwd: project.root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
    if (r.status !== 0) continue;
    manifests.push(Manifest.parse(r.stdout, `${MANIFEST_FILE} at ${sha.slice(0, 8)}`));
  }
  return manifests;
}

/** `git add` the files Push may have rewritten, for the pre-commit hook. */
export function stageCommittedFiles(project, files) {
  const existing = files.filter((f) => fs.existsSync(f));
  if (existing.length === 0) return true;
  const r = spawnSync('git', ['add', '--', ...existing], { cwd: project.root, encoding: 'utf8' });
  return r.status === 0;
}
