import fs from 'node:fs';
import path from 'node:path';
import { norm, cmpOrdinal } from './util.js';

// Markers are the Unity package's, so both tools edit the same block.
export const GITIGNORE_BEGIN = '# >>> UniLFS managed block - do not edit by hand >>>';
export const GITIGNORE_END = '# <<< UniLFS managed block <<<';
export const EXCLUDE_BEGIN = '# >>> UniLFS staged files - local to this checkout, never committed >>>';
export const EXCLUDE_END = '# <<< UniLFS staged files <<<';

/** Escapes gitignore pattern characters and anchors the path with a leading '/'. */
export function escapeIgnorePath(rel) {
  let s = '';
  for (const c of rel) {
    if ('\\*?[]#!'.includes(c)) s += '\\';
    s += c;
  }
  return '/' + s;
}

/**
 * Replaces the block between the markers, or appends one. The file's
 * existing line-ending style is kept.
 */
export function writeBlock(file, begin, end, blockLines) {
  let eol = '\n';
  let existing = [];
  if (fs.existsSync(file)) {
    const text = fs.readFileSync(file, 'utf8');
    if (text.includes('\r\n')) eol = '\r\n';
    existing = text.replace(/\r\n/g, '\n').split('\n');
    if (existing.length > 0 && existing[existing.length - 1] === '') existing.pop();
  }
  const b = existing.findIndex((l) => l.trim() === begin);
  let e = -1;
  if (b >= 0) {
    for (let i = b; i < existing.length; i++) {
      if (existing[i].trim() === end) { e = i; break; }
    }
  }
  let result;
  if (b >= 0 && e >= b) {
    result = [...existing.slice(0, b), ...blockLines, ...existing.slice(e + 1)];
  } else {
    result = [...existing];
    if (result.length > 0 && result[result.length - 1] !== '') result.push('');
    result.push(...blockLines);
  }
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const next = result.join(eol) + eol;
  // Skip identical rewrites so mtimes (and editors watching the file) stay quiet.
  if (fs.existsSync(file) && fs.readFileSync(file, 'utf8') === next) return;
  fs.writeFileSync(file, next, 'utf8');
}

export function readBlock(file, begin, end) {
  const out = [];
  if (!fs.existsSync(file)) return out;
  let inside = false;
  for (const l of fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n').split('\n')) {
    if (l.trim() === begin) { inside = true; continue; }
    if (l.trim() === end) inside = false;
    else if (inside) out.push(l);
  }
  return out;
}

/**
 * Rewrites the managed .gitignore block from the manifest's paths alone,
 * plus the two constant per-user lines. Committed, so it must never depend
 * on anything local.
 */
export function updateGitIgnore(project, manifestPaths) {
  const lines = [...new Set(manifestPaths.map(escapeIgnorePath))].sort(cmpOrdinal);
  writeBlock(project.gitignorePath, GITIGNORE_BEGIN, GITIGNORE_END, [
    GITIGNORE_BEGIN,
    project.credentialsIgnoreLine,
    project.stagedIgnoreLine,
    ...lines,
    GITIGNORE_END,
  ]);
}

function readGitDirPointer(dotGitFile) {
  try {
    for (const line of fs.readFileSync(dotGitFile, 'utf8').split(/\r?\n/)) {
      const t = line.trim();
      if (!t.startsWith('gitdir:')) continue;
      const target = t.slice('gitdir:'.length).trim();
      return target.length ? target : null;
    }
  } catch { /* fall through */ }
  return null;
}

/**
 * Locates the checkout around projectRoot: { repoRoot, gitDir, commonDir,
 * excludeFile, prefix } or null. Handles .git as a directory and as a
 * "gitdir:" file (worktrees, submodules). Paths in info/exclude are relative
 * to the repository root, hence `prefix` for a project in a subdirectory.
 */
export function findGit(projectRoot) {
  try {
    const root = norm(path.resolve(projectRoot));
    let repoRoot = null;
    let gitDir = null;
    for (let dir = root; ; dir = norm(path.dirname(dir))) {
      const candidate = path.join(dir, '.git');
      let st = null;
      try { st = fs.statSync(candidate); } catch { /* none here */ }
      if (st && st.isDirectory()) {
        repoRoot = dir;
        gitDir = candidate;
        break;
      }
      if (st && st.isFile()) {
        const target = readGitDirPointer(candidate);
        if (!target) return null;
        repoRoot = dir;
        gitDir = path.isAbsolute(target) ? target : path.join(dir, target);
        break;
      }
      if (norm(path.dirname(dir)) === dir) break;
    }
    if (!gitDir || !fs.existsSync(gitDir)) return null;

    // A linked worktree shares info/exclude (and hooks) with the main checkout.
    let commonDir = gitDir;
    const commonFile = path.join(gitDir, 'commondir');
    if (fs.existsSync(commonFile)) {
      const common = fs.readFileSync(commonFile, 'utf8').trim();
      if (common) commonDir = path.isAbsolute(common) ? common : path.join(gitDir, common);
    }

    let prefix = '';
    if (root.toLowerCase() !== repoRoot.toLowerCase()) {
      if (!root.toLowerCase().startsWith(repoRoot.toLowerCase() + '/')) return null;
      prefix = root.slice(repoRoot.length + 1) + '/';
    }
    return {
      repoRoot,
      gitDir: norm(path.resolve(gitDir)),
      commonDir: norm(path.resolve(commonDir)),
      excludeFile: norm(path.join(path.resolve(commonDir), 'info', 'exclude')),
      prefix,
    };
  } catch {
    return null;
  }
}

/**
 * Rewrites the .git/info/exclude block from the staged paths. Returns false
 * when there is no checkout to write to (the caller reports those paths as
 * not ignored rather than failing).
 */
export function updateGitExclude(project, stagedPaths) {
  const git = findGit(project.root);
  if (!git) return false;
  try {
    if (stagedPaths.length === 0 && readBlock(git.excludeFile, EXCLUDE_BEGIN, EXCLUDE_END).length === 0) return true;
    const lines = [...new Set(stagedPaths.map((p) => escapeIgnorePath(git.prefix + p)))].sort(cmpOrdinal);
    writeBlock(git.excludeFile, EXCLUDE_BEGIN, EXCLUDE_END, [EXCLUDE_BEGIN, ...lines, EXCLUDE_END]);
    return true;
  } catch {
    return false;
  }
}

/** Staged paths no ignore rule covers - one `git add -A` away from the history. */
export function stagedPathsNothingIgnores(project, stagedPaths) {
  if (stagedPaths.length === 0) return [];
  const git = findGit(project.root);
  if (!git) return [...stagedPaths];
  const lines = new Set(readBlock(git.excludeFile, EXCLUDE_BEGIN, EXCLUDE_END));
  return stagedPaths.filter((p) => !lines.has(escapeIgnorePath(git.prefix + p)));
}
