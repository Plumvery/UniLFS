import fs from 'node:fs';
import { request, readText, downloadVerified, StorageError, ConfigError } from './http.js';
import { refresh, fetchAccountEmail } from './google-oauth.js';
import { ENV } from '../project.js';

const API = 'https://www.googleapis.com/drive/v3';
const UPLOAD = 'https://www.googleapis.com/upload/drive/v3';

/**
 * Blobs are files named by their SHA-256 inside one Drive folder - the same
 * layout the Unity package uses, so both can share a folder.
 */
export class GoogleDriveProvider {
  constructor({ clientId, clientSecret, refreshToken, folderId }) {
    if (!clientId || !clientSecret) {
      throw new ConfigError('Google OAuth client ID / client secret are not set (driveClientId / driveClientSecret).');
    }
    if (!refreshToken) {
      throw new ConfigError(`Not signed in to Google Drive. Run "mylfs login". For CI, set ${ENV.driveRefreshToken}.`);
    }
    if (!folderId || !/^[A-Za-z0-9_-]+$/.test(folderId.trim())) {
      throw new ConfigError('Google Drive folder ID is missing or invalid (driveFolderId). Paste the ID from the folder\'s URL.');
    }
    this.clientId = clientId.trim();
    this.clientSecret = clientSecret.trim();
    this.refreshToken = refreshToken.trim();
    this.folderId = folderId.trim();
    this.tokens = null;
    this.tokenPromise = null;
    this.fileIds = new Map();
    this.emailPromise = null;
    this.displayName = 'Google Drive';
  }

  async accessToken(signal) {
    if (this.tokens && Date.now() < this.tokens.expiresAt) return this.tokens.accessToken;
    if (!this.tokenPromise) {
      this.tokenPromise = refresh(this.clientId, this.clientSecret, this.refreshToken, signal)
        .then((t) => { this.tokens = t; return t; })
        .finally(() => { this.tokenPromise = null; });
    }
    return (await this.tokenPromise).accessToken;
  }

  async auth(signal) {
    return { authorization: `Bearer ${await this.accessToken(signal)}` };
  }

  /** Resolved once, for messages only; never fails the operation it explains. */
  accountEmail(signal) {
    if (!this.emailPromise) {
      this.emailPromise = (async () => {
        try {
          const ctl = AbortSignal.timeout(10000);
          return await fetchAccountEmail(await this.accessToken(signal), ctl);
        } catch {
          return null;
        }
      })();
    }
    return this.emailPromise;
  }

  async findFileId(hash, signal) {
    if (this.fileIds.has(hash)) return this.fileIds.get(hash);
    const q = `name='${hash}' and '${this.folderId}' in parents and trashed=false`;
    const url = `${API}/files?q=${encodeURIComponent(q)}&fields=${encodeURIComponent('files(id,name,size)')}`
      + '&pageSize=1&supportsAllDrives=true&includeItemsFromAllDrives=true';
    const r = await request('GET', url, { headers: await this.auth(signal), signal });
    const text = await readText(r);
    if (r.status < 200 || r.status >= 300) throw driveError('files.list', r.status, text);
    const list = JSON.parse(text);
    const id = list && Array.isArray(list.files) && list.files.length > 0 ? list.files[0].id : null;
    if (id) this.fileIds.set(hash, id);
    return id;
  }

  async blobExists(hash, signal) {
    return (await this.findFileId(hash, signal)) != null;
  }

  async uploadBlob(hash, srcAbsPath, onBytes, signal) {
    const metadata = JSON.stringify({ name: hash, parents: [this.folderId] });
    const start = await request('POST', `${UPLOAD}/files?uploadType=resumable&supportsAllDrives=true`, {
      headers: { ...(await this.auth(signal)), 'content-type': 'application/json; charset=utf-8' },
      body: metadata,
      signal,
    });
    const startText = await readText(start);
    if (start.status < 200 || start.status >= 300) throw driveError('start upload', start.status, startText);
    const session = start.headers.location;
    if (!session) throw new StorageError('Google Drive did not return an upload session URL.');

    const size = fs.statSync(srcAbsPath).size;
    const r = await request('PUT', session, {
      headers: { 'content-length': String(size), 'content-type': 'application/octet-stream' },
      body: { file: srcAbsPath, onBytes },
      signal,
    });
    const text = await readText(r);
    if (r.status < 200 || r.status >= 300) throw driveError('upload', r.status, text);
    try {
      const file = JSON.parse(text);
      if (file && file.id) this.fileIds.set(hash, file.id);
    } catch { /* fine: found again by name */ }
  }

  async downloadBlob(hash, destAbsPath, onBytes, signal) {
    const id = await this.findFileId(hash, signal);
    if (!id) throw new StorageError(notFoundMessage(hash, await this.accountEmail(signal)));
    const r = await request('GET', `${API}/files/${id}?alt=media&supportsAllDrives=true`, { headers: await this.auth(signal), signal });
    if (r.status < 200 || r.status >= 300) throw driveError('download', r.status, await readText(r));
    await downloadVerified(r.res, hash, destAbsPath, onBytes, signal);
  }

  /**
   * Checks the folder exists and that this account can list its contents -
   * two separate permissions on Drive.
   */
  async testConnection(signal) {
    const meta = await request('GET', `${API}/files/${this.folderId}?fields=${encodeURIComponent('id,name,mimeType')}&supportsAllDrives=true`, {
      headers: await this.auth(signal),
      signal,
    });
    const metaText = await readText(meta);
    if (meta.status < 200 || meta.status >= 300) throw driveError('get folder', meta.status, metaText);
    const folder = JSON.parse(metaText);
    if (!folder || folder.mimeType !== 'application/vnd.google-apps.folder') {
      throw new StorageError('The configured Google Drive ID exists but is not a folder.');
    }
    const q = `'${this.folderId}' in parents and trashed=false`;
    const r = await request('GET', `${API}/files?q=${encodeURIComponent(q)}&fields=${encodeURIComponent('files(id)')}`
      + '&pageSize=1&supportsAllDrives=true&includeItemsFromAllDrives=true', { headers: await this.auth(signal), signal });
    const text = await readText(r);
    if (r.status < 200 || r.status >= 300) throw driveError('files.list', r.status, text);
    const list = JSON.parse(text);
    const sawAny = !!(list && Array.isArray(list.files) && list.files.length > 0);
    const email = await this.accountEmail(signal);
    const who = email ? ` (signed in as ${email})` : '';
    if (sawAny) return `Connected to Google Drive folder '${folder.name}'${who}.`;
    return `Connected to Google Drive folder '${folder.name}'${who}, but this account sees no files in it. `
      + 'That is expected for a brand-new folder; if teammates have already pushed, ask the owner to share the folder with '
      + (email || 'this account') + '.';
  }

  /** Creates a folder in the signed-in user's My Drive and returns its ID. */
  static async createFolder(clientId, clientSecret, refreshToken, name) {
    const tokens = await refresh(clientId, clientSecret, refreshToken);
    const r = await request('POST', `${API}/files?supportsAllDrives=true`, {
      headers: { authorization: `Bearer ${tokens.accessToken}`, 'content-type': 'application/json; charset=utf-8' },
      body: JSON.stringify({ name, mimeType: 'application/vnd.google-apps.folder' }),
    });
    const text = await readText(r);
    if (r.status < 200 || r.status >= 300) throw driveError('create folder', r.status, text);
    const file = JSON.parse(text);
    if (!file || !file.id) throw new StorageError('Google Drive folder creation returned no ID.');
    return file.id;
  }
}

function notFoundMessage(hash, email) {
  return `Blob ${hash.slice(0, 8)}... was not found in the Google Drive folder${email ? ` (signed in as ${email})` : ''}. `
    + 'Either it was never pushed, the folder ID is wrong, or this Google account cannot see the folder\'s contents - '
    + `ask the folder owner to share the folder with ${email || 'this account'}, or run "mylfs login" with a different account.`;
}

function driveError(op, status, body) {
  let message = null;
  try {
    const env = JSON.parse(body);
    if (env && env.error && env.error.message) message = env.error.message;
  } catch { /* not JSON */ }
  if (message == null) message = body && body.length > 500 ? body.slice(0, 500) + '...' : body;
  let hint = '';
  if (status === 401) hint = ' Run "mylfs login" again.';
  else if (status === 403) hint = ' Check that the Drive API is enabled for your Google Cloud project and that you have access to the folder (quota/rate limits also report 403).';
  else if (status === 404) hint = ' Check the folder ID and that the signed-in account can access it.';
  return new StorageError(`Google Drive ${op} failed (HTTP ${status}): ${message}.${hint}`);
}

