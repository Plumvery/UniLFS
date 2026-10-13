import fs from 'node:fs';
import path from 'node:path';

// Unity helpers. Harmless outside Unity: nothing else writes .meta files
// with a guid line, and nothing else writes the placeholder marker.

export function isValidGuid(guid) {
  return typeof guid === 'string' && /^[0-9a-fA-F]{32}$/.test(guid);
}

export function readMetaGuid(assetAbsPath) {
  const metaPath = assetAbsPath + '.meta';
  try {
    if (!fs.existsSync(metaPath)) return null;
    const lines = fs.readFileSync(metaPath, 'utf8').split('\n').slice(0, 16);
    for (const line of lines) {
      const t = line.trim();
      if (!t.startsWith('guid:')) continue;
      const guid = t.slice('guid:'.length).trim();
      return isValidGuid(guid) ? guid : null;
    }
  } catch { /* unreadable: no guid */ }
  return null;
}

/** Puts back a .meta Unity discarded, so the asset keeps its GUID. Never overwrites. */
export function writeMinimalMeta(assetAbsPath, guid) {
  const metaPath = assetAbsPath + '.meta';
  if (!isValidGuid(guid) || fs.existsSync(metaPath)) return false;
  try {
    fs.mkdirSync(path.dirname(metaPath), { recursive: true });
    fs.writeFileSync(metaPath, `fileFormatVersion: 2\nguid: ${guid}\n`, 'utf8');
    return true;
  } catch {
    return false;
  }
}

const PLACEHOLDER_MARKER = 'UNILFS-PLACEHOLDER';
const PLACEHOLDER_MAX = 4096;

/** The stand-in the Unity package writes for a not-yet-pulled asset. Reads as missing. */
export function isPlaceholder(absPath) {
  try {
    const st = fs.statSync(absPath);
    if (!st.isFile() || st.size < PLACEHOLDER_MARKER.length || st.size > PLACEHOLDER_MAX) return false;
    const lines = fs.readFileSync(absPath, 'utf8').split('\n');
    if (lines.length < 3) return false;
    if (lines[0].replace(/\r$/, '') !== PLACEHOLDER_MARKER) return false;
    if (!lines[1].startsWith('sha256: ')) return false;
    if (!lines[2].startsWith('size: ')) return false;
    return /^\d+$/.test(lines[2].slice('size: '.length).replace(/\r$/, '').trim());
  } catch {
    return false;
  }
}

export function clearPlaceholder(absPath) {
  if (!isPlaceholder(absPath)) return false;
  try {
    fs.rmSync(absPath, { force: true });
    return true;
  } catch {
    return false;
  }
}
