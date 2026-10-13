#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  findProject, openProject, detectMode, missingRequirements,
  CONFIG_FILE, UNITY_SETTINGS_FILE, PROVIDER_S3, PROVIDER_DRIVE, PROVIDER_LOCAL,
} from '../src/project.js';
import { Manifest } from '../src/manifest.js';
import { TrackPatterns } from '../src/patterns.js';
import { updateGitIgnore } from '../src/ignore.js';
import * as core from '../src/core.js';
import { installHooks, uninstallHooks, installedHooks, stageCommittedFiles, manifestsBeingPushed } from '../src/hooks.js';
import { signIn } from '../src/providers/google-oauth.js';
import { GoogleDriveProvider } from '../src/providers/gdrive.js';
import { formatBytes, isAbort, isDir, norm } from '../src/util.js';

const VERSION = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version;

const HELP = `mylfs ${VERSION} - large files in your own storage instead of Git LFS

Usage: mylfs [--root <dir>] [--quiet] <command> [options]

Setup
  init [options]                 Create the config, unilfs.track and the .gitignore block
      --provider s3|googledrive|local
      --endpoint <url> --bucket <name> --region <r> --prefix <p>      (s3)
      --folder-id <id> --client-id <id> --client-secret <secret>      (googledrive)
      --local-path <dir>                                              (local)
  config [list | get <key> | set <key> <value>]
  login                          Store credentials for this machine (S3 keys, or Google sign-in)
  logout                         Forget this machine's credentials
  test                           Check that storage is reachable with these settings
  hooks install [--pre-commit]   Pull after checkout/merge, verify before push
  hooks uninstall | hooks status

Daily use
  track [paths...] [--matching]  Stage files (or folders) for storage; no paths = everything unilfs.track matches
                                 (--matching: of the given paths, only those unilfs.track matches)
  untrack <paths...>             Stop tracking (files stay on disk)
  push [--require-baseline]      Upload new/changed files, then record them in the manifest
  pull [--restore-modified]      Download missing/outdated files
  status [--verify]              State of every tracked file (--verify asks storage too)
  verify                         Fail when the manifest names blobs storage does not have
  keep-mine <paths...>           Resolve a conflict in favour of your copy (uploaded by the next push)

Machine interface (for tools such as the UniLFS Unity package):
  --json            one JSON result object on stdout ({"error": {kind, message}} on failure)
  --progress-json   JSON progress events, one per line, on stderr
  --cancel-on-stdin a line "cancel" on stdin cancels gracefully
  --paths-file <f>  read paths (one per line) instead of arguments
  extra commands: patterns, refresh-ignore, drive-create-folder [name], version

Credentials can also come from environment variables (CI):
  UNILFS_S3_ACCESS_KEY_ID, UNILFS_S3_SECRET_ACCESS_KEY,
  UNILFS_DRIVE_CLIENT_ID, UNILFS_DRIVE_CLIENT_SECRET, UNILFS_DRIVE_REFRESH_TOKEN
`;

const STATE_LABEL = {
  UpToDate: 'up to date',
  Modified: 'modified',
  Outdated: 'outdated',
  Conflicted: 'conflicted',
  MissingLocal: 'missing',
  Staged: 'staged',
};

// ---------------------------------------------------------------- arguments

function parseArgs(argv) {
  const opts = {};
  const positional = [];
  const valued = new Set(['root', 'provider', 'endpoint', 'bucket', 'region', 'prefix', 'folder-id', 'client-id', 'client-secret', 'local-path', 'paths-file']);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--') {
      positional.push(...argv.slice(i + 1));
      break;
    }
    if (a.startsWith('--')) {
      const eq = a.indexOf('=');
      const key = eq > 0 ? a.slice(2, eq) : a.slice(2);
      if (eq > 0) opts[key] = a.slice(eq + 1);
      else if (valued.has(key)) opts[key] = argv[++i];
      else opts[key] = true;
    } else if (a === '-h') {
      opts.help = true;
    } else if (a === '-q') {
      opts.quiet = true;
    } else {
      positional.push(a);
    }
  }
  return { opts, positional };
}

// ------------------------------------------------------------------ output

const out = (s = '') => process.stdout.write(s + '\n');
const err = (s = '') => process.stderr.write(s + '\n');

function makeProgress(quiet) {
  if (quiet) return undefined;
  const tty = process.stderr.isTTY;
  let lastDraw = 0;
  let drawn = false;
  const clear = () => {
    if (drawn) {
      process.stderr.write('\r\x1b[K');
      drawn = false;
    }
  };
  const verbosePhases = new Set(['Uploading', 'Downloading', 'Tracking']);
  return (ev) => {
    if (ev.type === 'phase') {
      clear();
      if (ev.total > 0) err(`mylfs [${ev.phase}] ${ev.total} item(s)${ev.totalBytes ? ', ' + formatBytes(ev.totalBytes) : ''}`);
    } else if (ev.type === 'done') {
      if (verbosePhases.has(ev.phase)) {
        clear();
        err(`  (${ev.done}/${ev.total}) ${ev.path}`);
      } else if (tty) {
        clear();
        process.stderr.write(`  ${ev.phase} ${ev.done}/${ev.total}`);
        drawn = true;
      }
    } else if (ev.type === 'bytes' && tty && ev.size > 4 * 1024 * 1024) {
      const now = Date.now();
      if (now - lastDraw < 200) return;
      lastDraw = now;
      clear();
      const pct = Math.floor((ev.bytes / ev.size) * 100);
      process.stderr.write(`  ${ev.phase} ${ev.path} ${pct}% (${formatBytes(ev.bytes)} / ${formatBytes(ev.size)})`);
      drawn = true;
    }
    if (ev.type === 'phase' || ev.type === 'done') lastDraw = 0;
  };
}

function finishProgress() {
  if (process.stderr.isTTY) process.stderr.write('\r\x1b[K');
}

function reportResult(label, r) {
  const parts = [];
  if (r.uploaded) parts.push(`uploaded=${r.uploaded}`);
  if (r.downloaded) parts.push(`downloaded=${r.downloaded}`);
  if (r.promoted) parts.push(`promoted=${r.promoted}`);
  if (r.trackedNew) parts.push(`staged=${r.trackedNew}`);
  if (r.untracked) parts.push(`untracked=${r.untracked}`);
  parts.push(`upToDate=${r.skipped}`);
  if (r.errors.length) parts.push(`errors=${r.errors.length}`);
  out(`mylfs: ${label} finished. ${parts.join(' ')}`);
  for (const p of r.missingLocal) err(`  warning: missing locally: ${p}`);
  for (const p of r.keptModified) err(`  warning: locally modified, kept (use --restore-modified to overwrite): ${p}`);
  for (const p of r.outdated) err(`  warning: local copy is older than the manifest, not pushed (run "mylfs pull"): ${p}`);
  for (const p of r.conflicted) err(`  warning: changed locally and in the manifest, left alone: ${p}`);
  if (r.conflicted.length) err('    resolve with "mylfs pull --restore-modified" (take theirs) or "mylfs keep-mine <path>" then push (keep yours)');
  for (const p of r.unattributed) err(`  warning: cannot tell which side is newer (no sync record on this machine), skipped: ${p}`);
  for (const p of r.notIgnored) err(`  warning: staged but not hidden from git (no git checkout found): ${p}`);
  for (const e of r.errors) err(`  error: ${e}`);
}

function stateLabel(f) {
  if (f.state === 'UpToDate' && !f.remoteKnown) return 'not pushed';
  return STATE_LABEL[f.state] || f.state;
}

// ---------------------------------------------------------------- prompts

async function readStdin() {
  if (process.stdin.isTTY) return '';
  const chunks = [];
  for await (const c of process.stdin) chunks.push(c);
  return Buffer.concat(chunks).toString('utf8');
}

function ask(question, { hidden = false } = {}) {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, output: process.stderr, terminal: !!process.stdin.isTTY });
    if (hidden && process.stdin.isTTY) {
      rl._writeToOutput = (s) => {
        if (s.startsWith(question)) rl.output.write(question);
        else if (s === '\r\n' || s === '\n') rl.output.write(s);
      };
    }
    rl.question(question, (answer) => {
      rl.close();
      if (hidden && process.stdin.isTTY) process.stderr.write('\n');
      resolve(answer.trim());
    });
  });
}

// ---------------------------------------------------------------- commands

function resolveProject(opts) {
  if (opts.root) return openProject(opts.root);
  return findProject(process.cwd());
}

/** CLI paths are relative to the current directory; folders expand to the files inside. */
function expandPaths(project, args) {
  const result = [];
  for (const a of args) {
    const abs = path.resolve(process.cwd(), a);
    if (isDir(abs)) {
      for (const rel of core.enumerateProjectFiles(project, abs)) result.push(project.abs(rel));
    } else {
      result.push(abs);
    }
  }
  return result;
}

async function cmdInit(opts) {
  const root = path.resolve(opts.root || process.cwd());
  const mode = opts.generic ? 'generic' : detectMode(root);
  if (mode === 'unity') {
    out(`Unity project detected: using ${UNITY_SETTINGS_FILE} (shared with the UniLFS Unity package).`);
  }
  if (opts.generic && detectMode(root) !== 'generic') {
    fs.writeFileSync(path.join(root, CONFIG_FILE), '{}\n');
  }
  const p = openProject(root);
  const s = { ...p.settings };
  if (opts.provider) {
    const v = String(opts.provider).toLowerCase();
    s.provider = v === 'gdrive' || v === 'drive' || v === 'google' ? PROVIDER_DRIVE : v;
    if (![PROVIDER_S3, PROVIDER_DRIVE, PROVIDER_LOCAL].includes(s.provider)) {
      throw new Error(`Unknown provider "${opts.provider}". Use s3, googledrive or local.`);
    }
  }
  if (opts.endpoint) s.s3Endpoint = opts.endpoint;
  if (opts.bucket) s.s3Bucket = opts.bucket;
  if (opts.region) s.s3Region = opts.region;
  if (opts.prefix != null && opts.prefix !== true) s.s3Prefix = opts.prefix;
  if (opts['folder-id']) s.driveFolderId = opts['folder-id'];
  if (opts['client-id']) s.driveClientId = opts['client-id'];
  if (opts['client-secret']) s.driveClientSecret = opts['client-secret'];
  if (opts['local-path']) s.localPath = opts['local-path'];
  p.saveSettings(s);
  out(`Wrote ${path.relative(process.cwd(), p.settingsPath) || p.settingsPath}`);
  if (TrackPatterns.createIfMissing(p.trackPath)) out('Wrote unilfs.track (add patterns such as *.psd, then run "mylfs track")');
  updateGitIgnore(p, Manifest.load(p.manifestPath).files.map((f) => f.path));
  out('Updated the UniLFS block in .gitignore');
  const missing = missingRequirements(p);
  if (missing.length) out(`Still needed: ${missing.join(', ')}`);
  else out('Storage is configured. Try "mylfs test".');
  return 0;
}

const SETTING_KEYS = ['provider', 's3Endpoint', 's3Bucket', 's3Region', 's3Prefix', 'driveFolderId', 'driveClientId', 'driveClientSecret', 'localPath', 'parallelTransfers', 'excludeDirs'];

async function cmdConfig(project, args) {
  const [action = 'list', key, ...rest] = args;
  if (action === 'list') {
    out(`# ${project.settingsPath} (${project.mode} project)`);
    for (const [k, v] of Object.entries(project.settings)) out(`${k} = ${JSON.stringify(v)}`);
    return 0;
  }
  if (action === 'get') {
    if (!key) throw new Error('usage: mylfs config get <key>');
    const v = project.settings[key];
    out(typeof v === 'string' ? v : JSON.stringify(v));
    return 0;
  }
  if (action === 'set') {
    if (!key || rest.length === 0) throw new Error('usage: mylfs config set <key> <value>');
    if (!SETTING_KEYS.includes(key) && !(key in project.settings)) {
      throw new Error(`Unknown setting "${key}". Known: ${SETTING_KEYS.join(', ')}`);
    }
    const s = { ...project.settings };
    const raw = rest.join(' ');
    if (key === 'parallelTransfers') s[key] = Math.max(1, parseInt(raw, 10) || 4);
    else if (key === 'excludeDirs') s[key] = raw.split(',').map((x) => x.trim()).filter(Boolean);
    else if (typeof s[key] === 'boolean') s[key] = raw === 'true';
    else s[key] = raw;
    project.saveSettings(s);
    out(`${key} = ${JSON.stringify(s[key])}`);
    return 0;
  }
  throw new Error('usage: mylfs config [list | get <key> | set <key> <value>]');
}

async function cmdLogin(project, opts) {
  const creds = project.loadCredentials();
  const provider = project.settings.provider;
  if (provider === PROVIDER_LOCAL) {
    out('The local provider needs no credentials.');
    return 0;
  }
  if (provider === PROVIDER_DRIVE) {
    const effective = project.credentials();
    let clientId = effective.driveClientId;
    let clientSecret = effective.driveClientSecret;
    if (!clientId) clientId = creds.driveClientId = await ask('Google OAuth client ID: ');
    if (!clientSecret) clientSecret = creds.driveClientSecret = await ask('Google OAuth client secret: ', { hidden: true });
    const tokens = await signIn(clientId, clientSecret, { log: err, open: !opts['no-browser'] });
    creds.driveRefreshToken = tokens.refreshToken || '';
    creds.driveAccountEmail = tokens.accountEmail || '';
    project.saveCredentials(creds);
    out(tokens.accountEmail ? `Signed in to Google Drive as ${tokens.accountEmail}.` : 'Signed in to Google Drive.');
    if (!project.settings.driveFolderId) {
      const answer = await ask('No folder ID is set. Create a new "UniLFS" folder in My Drive? [y/N] ');
      if (/^y/i.test(answer)) {
        const id = await GoogleDriveProvider.createFolder(clientId, clientSecret, creds.driveRefreshToken, 'UniLFS');
        project.saveSettings({ ...project.settings, driveFolderId: id });
        out(`Created folder; driveFolderId = ${id}. Share it with your teammates in Google Drive.`);
      }
    }
  } else {
    creds.s3AccessKeyId = await ask('Access Key ID: ');
    creds.s3SecretAccessKey = await ask('Secret Access Key: ', { hidden: true });
    if (!creds.s3AccessKeyId || !creds.s3SecretAccessKey) throw new Error('Both values are required.');
    project.saveCredentials(creds);
    out(`Saved to ${project.credentialsPath} (ignored by git).`);
  }
  updateGitIgnore(project, Manifest.load(project.manifestPath).files.map((f) => f.path));
  return 0;
}

async function cmdLogout(project) {
  const creds = project.loadCredentials();
  for (const k of ['s3AccessKeyId', 's3SecretAccessKey', 'driveRefreshToken', 'driveAccountEmail']) creds[k] = '';
  project.saveCredentials(creds);
  out('Credentials cleared for this machine.');
  return 0;
}

async function cmdStatus(project, opts, signal) {
  const report = await core.status(project, { verifyRemote: !!opts.verify, onProgress: makeProgress(opts.quiet), signal });
  finishProgress();
  if (report.files.length === 0) {
    out('mylfs: no tracked files.');
    return 0;
  }
  const counts = {};
  const width = Math.max(...report.files.map((f) => stateLabel(f).length));
  out(`mylfs status (${report.files.length} tracked):`);
  for (const f of report.files) {
    const label = stateLabel(f);
    counts[label] = (counts[label] || 0) + 1;
    out(`  ${label.padEnd(width)}  ${f.path}  (${formatBytes(f.currentSize || f.size)})`);
  }
  out('');
  out(Object.entries(counts).map(([k, v]) => `${v} ${k}`).join(', '));
  if (report.verified) out(`Storage confirmed ${report.confirmed} blob(s).`);
  for (const p of report.missingRemote) err(`  missing in storage: ${p}`);
  for (const f of report.failures) err(`  could not check: ${f}`);
  for (const p of report.notIgnored) err(`  warning: staged but not hidden from git: ${p}`);
  const hints = [];
  if (counts.staged || counts.modified || counts['not pushed']) hints.push('"mylfs push" uploads staged/modified files');
  if (counts.missing || counts.outdated) hints.push('"mylfs pull" downloads missing/outdated files');
  if (counts['not pushed'] && !report.verified) hints.push('"mylfs status --verify" asks storage what it already has');
  if (counts.conflicted) hints.push('conflicts: "mylfs pull --restore-modified" (theirs) or "mylfs keep-mine <path>" + push (yours)');
  for (const h of hints) out(`hint: ${h}`);
  return 0;
}

function pathsInGitIndex(project, paths) {
  if (paths.length === 0) return [];
  const r = spawnSync('git', ['-c', 'core.quotepath=false', 'ls-files', '--', ...paths], { cwd: project.root, encoding: 'utf8' });
  if (r.status !== 0) return [];
  const inIndex = new Set(r.stdout.split(/\r?\n/).filter(Boolean));
  return paths.filter((p) => inIndex.has(p));
}

async function cmdTrack(project, args, opts, signal) {
  const progress = makeProgress(opts.quiet);
  const result = args.length === 0 || opts.matching
    ? await core.trackMatching(project, { paths: args.length ? expandPaths(project, args) : undefined, onProgress: progress, signal })
    : await core.track(project, expandPaths(project, args), { onProgress: progress, signal });
  finishProgress();
  if (args.length === 0 && TrackPatterns.load(project.trackPath).isEmpty) {
    err('unilfs.track has no patterns. Add lines such as "*.psd", or pass paths: mylfs track <files...>');
  }
  reportResult('Track', result);
  const hint = core.gitRemoveHint(pathsInGitIndex(project, result.newlyTracked));
  if (hint) out(hint);
  if (result.trackedNew) out('Run "mylfs push" to upload them and record them in the manifest.');
  return result.errors.length ? 1 : 0;
}

async function runHook(project, name, args, signal) {
  const missing = missingRequirements(project);
  const quiet = undefined;
  if (name === 'post-checkout' || name === 'post-merge') {
    // post-checkout's third argument is 1 for a branch checkout, 0 for a file checkout.
    if (name === 'post-checkout' && args[2] === '0') return 0;
    if (Manifest.load(project.manifestPath).files.length === 0) return 0;
    if (missing.length) {
      err(`mylfs: not pulling - storage is not configured yet (${missing.join(', ')}).`);
      return 0;
    }
    const r = await core.pull(project, { onProgress: makeProgress(quiet), signal });
    finishProgress();
    if (r.downloaded || r.errors.length || r.conflicted.length || r.keptModified.length) reportResult('Pull', r);
    return 0;
  }
  if (name === 'pre-push') {
    const manifests = manifestsBeingPushed(project, await readStdin());
    if (manifests.every((m) => m.files.length === 0)) return 0;
    if (missing.length) {
      err(`mylfs: skipping the blob check - storage is not configured (${missing.join(', ')}).`);
      return 0;
    }
    const r = await core.verify(project, { signal, manifests });
    if (r.errors.length) {
      reportResult('Verify', r);
      err('mylfs: the manifest names blobs storage does not have. Run "mylfs push" and commit the manifest, then push again.');
      return 1;
    }
    return 0;
  }
  if (name === 'pre-commit') {
    if (missing.length) {
      err(`mylfs: not pushing - storage is not configured (${missing.join(', ')}).`);
      return 0;
    }
    const r = await core.push(project, { requireBaseline: true, onProgress: makeProgress(quiet), signal });
    finishProgress();
    if (r.uploaded || r.promoted || r.errors.length || r.conflicted.length || r.outdated.length) reportResult('Push', r);
    stageCommittedFiles(project, [project.manifestPath, project.gitignorePath]);
    if (r.errors.length) {
      err('mylfs: some files could not be uploaded; the commit was stopped so the manifest does not miss them.');
      return 1;
    }
    return 0;
  }
  err(`mylfs: unknown hook "${name}".`);
  return 0;
}

// ------------------------------------------------------- machine interface
//
// `--json` is the interface other tools (the UniLFS Unity package) drive
// mylfs through: one JSON object on stdout per run - the operation's result,
// or { "error": { "kind", "message" } } - and nothing else there.
// `--progress-json` adds one JSON event per line on stderr, and
// `--cancel-on-stdin` cancels gracefully (finishing what is done, saving the
// manifest) when a line "cancel" arrives on stdin.

function readPathsFile(file) {
  return fs.readFileSync(file, 'utf8').split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
}

/** Relative paths are project-relative here, unlike the human CLI's (relative to the shell's directory). */
function machinePaths(project, args, opts) {
  const list = opts['paths-file'] ? readPathsFile(opts['paths-file']) : args;
  return list.map((a) => path.resolve(project.root, a));
}

function errorKind(e) {
  if (isAbort(e)) return 'cancelled';
  if (e && e.code === 'UNILFS_BUSY') return 'busy';
  if (e && (e.name === 'ConfigError' || e.code === 'UNILFS_NO_PROJECT')) return 'config';
  return 'error';
}

/** Status entries with no nulls, so strict deserializers (Unity's JsonUtility) read them. */
function machineStatus(report) {
  return {
    ...report,
    files: report.files.map((f) => ({
      path: f.path,
      hash: f.hash || '',
      size: f.size || 0,
      guid: f.guid || '',
      state: f.state,
      currentHash: f.currentHash || '',
      currentSize: f.currentSize || 0,
      remoteKnown: !!f.remoteKnown,
      baselineKnown: !!f.baselineKnown,
      needsDownload: !!f.needsDownload,
    })),
  };
}

async function runMachine(command, args, opts, signal) {
  const onProgress = opts['progress-json']
    ? (ev) => process.stderr.write(JSON.stringify(ev) + '\n')
    : undefined;
  if (command === 'version') return { version: VERSION };
  const project = resolveProject(opts);
  switch (command) {
    case 'status':
      return machineStatus(await core.status(project, { verifyRemote: !!opts.verify, onProgress, signal }));
    case 'track':
      if (opts.matching) {
        const paths = opts['paths-file'] || args.length ? machinePaths(project, args, opts) : undefined;
        return core.trackMatching(project, { paths, onProgress, signal });
      }
      return core.track(project, machinePaths(project, args, opts), { onProgress, signal });
    case 'untrack':
      return core.untrack(project, machinePaths(project, args, opts));
    case 'keep-mine':
      return core.keepLocal(project, machinePaths(project, args, opts));
    case 'push':
      return core.push(project, { requireBaseline: !!opts['require-baseline'], onProgress, signal });
    case 'pull':
      return core.pull(project, { restoreModified: !!opts['restore-modified'], onProgress, signal });
    case 'verify':
      return core.verify(project, { onProgress, signal });
    case 'test': {
      const missing = missingRequirements(project);
      if (missing.length) {
        const e = new Error(`This provider still needs ${missing.join(', ')}.`);
        e.name = 'ConfigError';
        throw e;
      }
      return { message: await core.testConnection(project, { signal }) };
    }
    case 'patterns': {
      const p = TrackPatterns.load(project.trackPath);
      return { exists: fs.existsSync(project.trackPath), count: p.rules.length, errors: p.errors };
    }
    case 'refresh-ignore':
      updateGitIgnore(project, Manifest.load(project.manifestPath).files.map((f) => f.path));
      return { ok: true };
    case 'login': {
      if (project.settings.provider !== PROVIDER_DRIVE) {
        const e = new Error('Only Google Drive signs in through a browser; S3 keys are entered directly.');
        e.name = 'ConfigError';
        throw e;
      }
      const c = project.credentials();
      const tokens = await signIn(c.driveClientId, c.driveClientSecret, {
        log: () => {},
        onUrl: (url) => process.stderr.write(JSON.stringify({ type: 'auth-url', url }) + '\n'),
        open: !opts['no-browser'],
        signal,
      });
      const creds = project.loadCredentials();
      creds.driveRefreshToken = tokens.refreshToken || '';
      creds.driveAccountEmail = tokens.accountEmail || '';
      project.saveCredentials(creds);
      updateGitIgnore(project, Manifest.load(project.manifestPath).files.map((f) => f.path));
      return { accountEmail: tokens.accountEmail || '' };
    }
    case 'drive-create-folder': {
      const c = project.credentials();
      const name = args[0] || 'UniLFS';
      return { id: await GoogleDriveProvider.createFolder(c.driveClientId, c.driveClientSecret, c.driveRefreshToken, name), name };
    }
    default: {
      const e = new Error(`Unknown command "${command}" for --json.`);
      e.name = 'ConfigError';
      throw e;
    }
  }
}

async function mainJson(command, args, opts) {
  const controller = new AbortController();
  if (opts['cancel-on-stdin']) {
    let buffered = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => {
      buffered += chunk;
      if (/(^|\n)cancel\r?\n/.test(buffered)) controller.abort();
    });
    process.stdin.on('error', () => {});
  }
  process.on('SIGINT', () => controller.abort());
  let payload;
  try {
    payload = await runMachine(command, args, opts, controller.signal);
  } catch (e) {
    payload = { error: { kind: errorKind(e), message: e && e.message ? e.message : String(e) } };
  }
  const code = payload && payload.error ? 1 : 0;
  // Exit explicitly: a stdin listener would otherwise keep the process alive.
  process.stdout.write(JSON.stringify(payload) + '\n', () => process.exit(code));
  return new Promise(() => {});
}

async function main(argv) {
  const { opts, positional } = parseArgs(argv);
  const [command, ...args] = positional;
  if (opts.json && command) return mainJson(command, args, opts);
  if (opts.version) {
    out(VERSION);
    return 0;
  }
  if (!command || opts.help || command === 'help') {
    out(HELP);
    return 0;
  }

  const controller = new AbortController();
  let interrupted = 0;
  process.on('SIGINT', () => {
    interrupted++;
    if (interrupted > 1) process.exit(130);
    err('\nmylfs: cancelling (finishing the files already done; press Ctrl+C again to force)...');
    controller.abort();
  });
  const signal = controller.signal;

  if (command === 'init') return cmdInit(opts);

  const project = resolveProject(opts);
  switch (command) {
    case 'config':
      return cmdConfig(project, args);
    case 'login':
      return cmdLogin(project, opts);
    case 'logout':
      return cmdLogout(project);
    case 'test': {
      const missing = missingRequirements(project);
      if (missing.length) {
        err(`Not configured yet: ${missing.join(', ')}`);
        return 1;
      }
      out(await core.testConnection(project, { signal }));
      return 0;
    }
    case 'status':
      return cmdStatus(project, opts, signal);
    case 'track':
      return cmdTrack(project, args, opts, signal);
    case 'untrack': {
      if (!args.length) throw new Error('usage: mylfs untrack <paths...>');
      const r = await core.untrack(project, expandPaths(project, args));
      reportResult('Untrack', r);
      return r.errors.length ? 1 : 0;
    }
    case 'keep-mine': {
      if (!args.length) throw new Error('usage: mylfs keep-mine <paths...>');
      const r = await core.keepLocal(project, args.map((a) => path.resolve(process.cwd(), a)));
      for (const p of r.conflicted) out(`keep mine: ${p} (uploaded by the next "mylfs push")`);
      for (const e of r.errors) err(`  error: ${e}`);
      return r.errors.length ? 1 : 0;
    }
    case 'push': {
      const r = await core.push(project, { requireBaseline: !!opts['require-baseline'], onProgress: makeProgress(opts.quiet), signal });
      finishProgress();
      reportResult('Push', r);
      if (r.uploaded || r.promoted) out('Commit unilfs.manifest.json and .gitignore to share the change.');
      return r.errors.length ? 1 : 0;
    }
    case 'pull': {
      const restore = !!opts['restore-modified'] || process.env.UNILFS_PULL_RESTORE_MODIFIED === '1';
      const r = await core.pull(project, { restoreModified: restore, onProgress: makeProgress(opts.quiet), signal });
      finishProgress();
      reportResult(restore ? 'Restore' : 'Pull', r);
      return r.errors.length ? 1 : 0;
    }
    case 'verify': {
      const r = await core.verify(project, { onProgress: makeProgress(opts.quiet), signal });
      finishProgress();
      reportResult('Verify', r);
      if (r.errors.length) {
        err('mylfs Verify failed: the manifest names content storage does not have - most likely a merge or hand-edit'
          + ' that kept an entry nobody pushed, or a blob deleted from the bucket.');
      }
      return r.errors.length ? 1 : 0;
    }
    case 'hooks': {
      const action = args[0] || 'status';
      if (action === 'install') {
        for (const f of installHooks(project, { preCommit: !!opts['pre-commit'] })) out(`installed ${f}`);
        return 0;
      }
      if (action === 'uninstall') {
        const removed = uninstallHooks(project);
        for (const f of removed) out(`removed mylfs from ${f}`);
        if (!removed.length) out('No mylfs hooks were installed.');
        return 0;
      }
      const names = installedHooks(project);
      out(names.length ? `mylfs hooks installed: ${names.join(', ')}` : 'No mylfs hooks installed. Run "mylfs hooks install".');
      return 0;
    }
    case 'hook':
      return runHook(project, args[0], args.slice(1), signal);
    default:
      err(`Unknown command "${command}". Run "mylfs help".`);
      return 2;
  }
}

const isMain = (() => {
  try {
    return norm(fs.realpathSync(process.argv[1])) === norm(fs.realpathSync(fileURLToPath(import.meta.url)));
  } catch {
    return true;
  }
})();

if (isMain) {
  // Piped into `head` and friends: a closed stdout is not an error.
  process.stdout.on('error', (e) => {
    if (e.code === 'EPIPE') process.exit(process.exitCode || 0);
  });
  main(process.argv.slice(2)).then(
    (code) => { process.exitCode = code; },
    (e) => {
      finishProgress();
      if (isAbort(e)) {
        err('mylfs: cancelled.');
        process.exitCode = 130;
      } else {
        err(`mylfs: ${e && e.message ? e.message : e}`);
        if (process.env.UNILFS_DEBUG) err(e && e.stack);
        process.exitCode = 1;
      }
    },
  );
}

export { main };
