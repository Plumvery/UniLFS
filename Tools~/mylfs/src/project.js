import fs from 'node:fs';
import path from 'node:path';
import { norm, exists, readJsonOr, writeFileAtomic, sha256Hex } from './util.js';

export const MANIFEST_FILE = 'unilfs.manifest.json';
export const STAGED_FILE = 'unilfs.staged.json';
export const TRACK_FILE = 'unilfs.track';
export const CONFIG_FILE = 'unilfs.config.json';
export const UNITY_SETTINGS_FILE = 'ProjectSettings/UniLFSSettings.json';
const UNITY_VERSION_FILE = 'ProjectSettings/ProjectVersion.txt';

export const PROVIDER_S3 = 's3';
export const PROVIDER_DRIVE = 'googledrive';
export const PROVIDER_LOCAL = 'local';

export const ENV = {
  s3AccessKeyId: 'UNILFS_S3_ACCESS_KEY_ID',
  s3SecretAccessKey: 'UNILFS_S3_SECRET_ACCESS_KEY',
  driveClientId: 'UNILFS_DRIVE_CLIENT_ID',
  driveClientSecret: 'UNILFS_DRIVE_CLIENT_SECRET',
  driveRefreshToken: 'UNILFS_DRIVE_REFRESH_TOKEN',
};

/**
 * Field names match ProjectSettings/UniLFSSettings.json, so a config can be
 * copied between a Unity project and any other one. The Unity-only fields
 * (autoPull/autoPush/autoTrack) are kept when present and otherwise unused.
 */
export function defaultSettings() {
  return {
    version: 1,
    provider: PROVIDER_S3,
    s3Endpoint: '',
    s3Bucket: '',
    s3Region: 'auto',
    s3Prefix: 'unilfs',
    driveFolderId: '',
    driveClientId: '',
    driveClientSecret: '',
    localPath: '',
    parallelTransfers: 4,
  };
}

export function defaultGenericSettings() {
  return { ...defaultSettings(), excludeDirs: ['node_modules'] };
}

export function defaultCredentials() {
  return {
    version: 1,
    s3AccessKeyId: '',
    s3SecretAccessKey: '',
    driveClientId: '',
    driveClientSecret: '',
    driveRefreshToken: '',
    driveAccountEmail: '',
  };
}

const UNITY_FORBIDDEN = ['Library', 'Temp', 'Logs', 'obj', 'UserSettings', '.git'];
const GENERIC_FORBIDDEN = ['.git', '.unilfs'];

/**
 * Everything the CLI needs to know about where things live.
 *
 * Two layouts share every committed file:
 *   unity   - a Unity project already using the UniLFS package. Settings,
 *             credentials and caches stay exactly where the package keeps
 *             them, so the editor and this CLI can work on one checkout.
 *   generic - anything else. Settings in unilfs.config.json, everything
 *             per-machine under .unilfs/.
 */
export class Project {
  constructor(root, mode) {
    this.root = norm(path.resolve(root)).replace(/\/+$/, '');
    this.mode = mode;
    const unity = mode === 'unity';
    this.manifestPath = this.abs(MANIFEST_FILE);
    this.stagedPath = this.abs(STAGED_FILE);
    this.trackPath = this.abs(TRACK_FILE);
    this.gitignorePath = this.abs('.gitignore');
    this.settingsPath = this.abs(unity ? UNITY_SETTINGS_FILE : CONFIG_FILE);
    this.credentialsPath = this.abs(unity ? 'UserSettings/UniLFS.json' : '.unilfs/credentials.json');
    this.cacheDir = this.abs(unity ? 'Library/UniLFS' : '.unilfs/cache');
    this.stateCachePath = this.cacheDir + '/statecache.json';
    this.tmpDir = this.cacheDir + '/tmp';
    this.lockPath = this.cacheDir + '/operation.lock';
    // The two constant lines of the managed .gitignore block. In a Unity
    // project they have to be the package's own, byte for byte, or the
    // editor and the CLI would rewrite each other's block forever.
    this.credentialsIgnoreLine = unity ? '/UserSettings/UniLFS.json' : '/.unilfs/';
    this.stagedIgnoreLine = '/' + STAGED_FILE;
    this.settings = this.loadSettings();
    this.forbiddenTop = unity
      ? UNITY_FORBIDDEN
      : [...GENERIC_FORBIDDEN, ...(Array.isArray(this.settings.excludeDirs) ? this.settings.excludeDirs : [])
        .map((d) => norm(String(d)).replace(/^\/+|\/+$/g, ''))
        .filter(Boolean)];
  }

  abs(rel) {
    return this.root + '/' + rel;
  }

  /** Absolute (or already root-relative) path -> normalized project-relative path, or null when outside. */
  toRel(p) {
    if (p == null || p === '') return null;
    let full;
    try {
      full = norm(path.isAbsolute(p) ? path.resolve(p) : path.resolve(this.root, p));
    } catch {
      return null;
    }
    const root = this.root;
    if (full.toLowerCase() === root.toLowerCase()) return '';
    if (!full.toLowerCase().startsWith(root.toLowerCase() + '/')) return null;
    return full.slice(root.length + 1);
  }

  /** Returns null when the path may be tracked, otherwise the reason it may not. */
  untrackableReason(rel) {
    if (rel == null || rel === '') return 'the path is outside the project';
    const lower = rel.toLowerCase();
    if (lower.endsWith('.meta') && this.mode === 'unity') return '.meta files must stay in git';
    if (lower === MANIFEST_FILE) return 'the UniLFS manifest itself cannot be tracked';
    if (lower === STAGED_FILE) return 'the UniLFS staging file itself cannot be tracked';
    if (lower === TRACK_FILE) return 'the UniLFS pattern file itself cannot be tracked';
    if (lower === CONFIG_FILE) return 'the UniLFS config file itself cannot be tracked';
    if (lower === '.gitignore') return '.gitignore must stay in git';
    for (const top of this.forbiddenTop) {
      const t = top.toLowerCase();
      if (lower === t || lower.startsWith(t + '/')) return `files under ${top}/ cannot be tracked`;
    }
    return null;
  }

  loadSettings() {
    const loaded = readJsonOr(this.settingsPath, {});
    const base = this.mode === 'unity' ? defaultSettings() : defaultGenericSettings();
    return { ...base, ...(loaded && typeof loaded === 'object' ? loaded : {}) };
  }

  saveSettings(settings) {
    this.settings = settings;
    writeFileAtomic(this.settingsPath, JSON.stringify(settings, null, 4) + '\n');
  }

  loadCredentials() {
    const loaded = readJsonOr(this.credentialsPath, {});
    return { ...defaultCredentials(), ...(loaded && typeof loaded === 'object' ? loaded : {}) };
  }

  saveCredentials(creds) {
    writeFileAtomic(this.credentialsPath, JSON.stringify(creds, null, 4) + '\n');
    if (process.platform !== 'win32') {
      try { fs.chmodSync(this.credentialsPath, 0o600); } catch { /* best effort */ }
    }
  }

  /** Environment variable > per-user credentials file > project settings (Drive client only). */
  credentials() {
    const user = this.loadCredentials();
    const s = this.settings;
    const env = (k) => process.env[ENV[k]] || '';
    const first = (...v) => v.find((x) => typeof x === 'string' && x.length > 0) || '';
    return {
      s3AccessKeyId: first(env('s3AccessKeyId'), user.s3AccessKeyId),
      s3SecretAccessKey: first(env('s3SecretAccessKey'), user.s3SecretAccessKey),
      driveClientId: first(env('driveClientId'), user.driveClientId, s.driveClientId),
      driveClientSecret: first(env('driveClientSecret'), user.driveClientSecret, s.driveClientSecret),
      driveRefreshToken: first(env('driveRefreshToken'), user.driveRefreshToken),
      driveAccountEmail: user.driveAccountEmail || '',
    };
  }

  /**
   * Identifies the storage location remote confirmations belong to. Same
   * formula as UniLfsRemoteBlobCache.Fingerprint, so a Unity project's
   * confirmations carry over.
   */
  storageFingerprint() {
    const s = this.settings;
    let identity;
    if (s.provider === PROVIDER_DRIVE) identity = 'drive|' + s.driveFolderId;
    else if (s.provider === PROVIDER_LOCAL) identity = 'local|' + s.localPath;
    else identity = 's3|' + s.s3Endpoint + '|' + s.s3Bucket + '|' + s.s3Prefix;
    return sha256Hex(identity).slice(0, 12);
  }
}

function looksLikeProjectRoot(dir) {
  return exists(path.join(dir, CONFIG_FILE))
    || exists(path.join(dir, UNITY_SETTINGS_FILE))
    || exists(path.join(dir, MANIFEST_FILE));
}

export function detectMode(root) {
  if (exists(path.join(root, CONFIG_FILE))) return 'generic';
  if (exists(path.join(root, UNITY_SETTINGS_FILE)) || exists(path.join(root, UNITY_VERSION_FILE))) return 'unity';
  return 'generic';
}

/** Walks up from startDir to the nearest UniLFS project, or throws. */
export function findProject(startDir = process.cwd()) {
  let dir = path.resolve(startDir);
  for (;;) {
    if (looksLikeProjectRoot(dir)) return new Project(dir, detectMode(dir));
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  const e = new Error('Not inside a UniLFS project (no ' + CONFIG_FILE + ', ' + UNITY_SETTINGS_FILE
    + ' or ' + MANIFEST_FILE + ' found in this directory or any parent). Run "mylfs init" first.');
  e.code = 'UNILFS_NO_PROJECT';
  throw e;
}

export function openProject(root) {
  const abs = path.resolve(root);
  if (!fs.existsSync(abs) || !fs.statSync(abs).isDirectory()) {
    const e = new Error(`Project directory not found: ${abs}`);
    e.code = 'UNILFS_NO_PROJECT';
    throw e;
  }
  return new Project(abs, detectMode(abs));
}

/** What still has to be configured before Push/Pull can work. Empty when ready. */
export function missingRequirements(project) {
  const s = project.settings;
  const c = project.credentials();
  const missing = [];
  if (s.provider === PROVIDER_DRIVE) {
    if (!c.driveClientId) missing.push('a client ID (driveClientId)');
    if (!c.driveClientSecret) missing.push('a client secret (driveClientSecret)');
    if (!s.driveFolderId) missing.push('a folder ID (driveFolderId)');
    if (!c.driveRefreshToken) missing.push('a signed-in Google account (run "mylfs login")');
  } else if (s.provider === PROVIDER_LOCAL) {
    if (!s.localPath) missing.push('a storage folder (localPath)');
  } else {
    if (!s.s3Endpoint) missing.push('an endpoint (s3Endpoint)');
    if (!s.s3Bucket) missing.push('a bucket (s3Bucket)');
    if (!c.s3AccessKeyId) missing.push('an access key ID (run "mylfs login" or set ' + ENV.s3AccessKeyId + ')');
    if (!c.s3SecretAccessKey) missing.push('a secret access key (run "mylfs login" or set ' + ENV.s3SecretAccessKey + ')');
  }
  return missing;
}
