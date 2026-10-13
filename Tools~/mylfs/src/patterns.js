import fs from 'node:fs';
import path from 'node:path';
import { norm } from './util.js';

/**
 * unilfs.track - committed patterns saying which files belong in storage.
 * Same gitignore-like dialect as the Unity package:
 *
 *   *.psd             no slash: matched against the file name, at any depth
 *   Assets/Movies/    trailing slash: everything under that folder
 *   Assets/**\/*.wav   ** crosses folders, * stays inside one name, ? is one character
 *   !Assets/UI/*.psd  ! excludes, and the last matching line wins
 *
 * Matching ignores case.
 */
export const TEMPLATE =
  '# UniLFS: files matching a line below are stored in external storage\n'
  + '# instead of git. Commit this file - it is how the team agrees on what\n'
  + '# counts as a large asset. Editing it tracks nothing on its own; run\n'
  + '# "mylfs track" (or Window > UniLFS > Track Matching in Unity).\n'
  + '#\n'
  + '#   *.psd             no slash: matched against the file name, at any depth\n'
  + '#   Assets/Movies/    trailing slash: everything under that folder\n'
  + '#   Assets/**/*.wav   ** crosses folders, * stays inside one name\n'
  + '#   !Assets/UI/*.psd  ! excludes, and the last matching line wins\n'
  + '#\n'
  + '# Case is ignored.\n';

export class TrackPatterns {
  constructor() {
    /** @type {{regex:RegExp, negate:boolean, nameOnly:boolean}[]} */
    this.rules = [];
    /** Lines that could not be read as a pattern, as "line N: text (why)". */
    this.errors = [];
  }

  get isEmpty() {
    return this.rules.length === 0;
  }

  static load(file) {
    try {
      if (!fs.existsSync(file)) return new TrackPatterns();
      return TrackPatterns.parse(fs.readFileSync(file, 'utf8'));
    } catch {
      return new TrackPatterns();
    }
  }

  static parse(text) {
    const p = new TrackPatterns();
    if (!text) return p;
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    text.replace(/\r\n/g, '\n').split('\n').forEach((line, i) => p.addLine(line, i + 1));
    return p;
  }

  addLine(rawLine, lineNumber) {
    let line = rawLine.trim();
    if (line.length === 0 || line[0] === '#') return;
    const negate = line[0] === '!';
    if (negate) line = line.slice(1).trim();
    let glob = norm(line);
    if (glob.length === 0) {
      this.errors.push(`line ${lineNumber}: ${rawLine.trim()} (empty pattern)`);
      return;
    }
    if (glob.split('/').some((seg) => seg === '..')) {
      this.errors.push(`line ${lineNumber}: ${rawLine.trim()} ('..' cannot appear in a pattern)`);
      return;
    }
    if (glob.startsWith('/')) glob = glob.slice(1);
    if (glob.endsWith('/')) glob += '**';
    if (glob.length === 0) {
      this.errors.push(`line ${lineNumber}: ${rawLine.trim()} (empty pattern)`);
      return;
    }
    this.rules.push({
      regex: new RegExp(globToRegex(glob), 'i'),
      negate,
      nameOnly: !glob.includes('/'),
    });
  }

  /**
   * Whether the project wants this path tracked. `untrackable` is the
   * project's own gate, checked first so no pattern can reach the manifest,
   * a cache directory or .git.
   */
  matches(rel, untrackable) {
    if (this.rules.length === 0 || !rel) return false;
    const p = norm(rel);
    if (untrackable && untrackable(p)) return false;
    const name = path.posix.basename(p);
    let matched = false;
    for (const rule of this.rules) {
      if (!rule.regex.test(rule.nameOnly ? name : p)) continue;
      matched = !rule.negate;
    }
    return matched;
  }

  static createIfMissing(file) {
    if (fs.existsSync(file)) return false;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, TEMPLATE, 'utf8');
    return true;
  }
}

function escapeRegex(c) {
  return c.replace(/[.*+?^${}()|[\]\\/-]/g, '\\$&');
}

export function globToRegex(glob) {
  let re = '^';
  let i = 0;
  while (i < glob.length) {
    const c = glob[i];
    if (c === '*') {
      if (glob[i + 1] === '*') {
        i += 2;
        if (glob[i] === '/') {
          // "**/" spans whole directory names, including none at all.
          re += '(?:[^/]+/)*';
          i++;
        } else {
          re += '.*';
        }
      } else {
        re += '[^/]*';
        i++;
      }
    } else if (c === '?') {
      re += '[^/]';
      i++;
    } else {
      re += escapeRegex(c);
      i++;
    }
  }
  return re + '$';
}
