import http from 'node:http';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { request, readText, StorageError, ConfigError } from './http.js';

export const SCOPE = 'https://www.googleapis.com/auth/drive';
const AUTH_ENDPOINT = 'https://accounts.google.com/o/oauth2/v2/auth';
const TOKEN_ENDPOINT = 'https://oauth2.googleapis.com/token';
const ABOUT_ENDPOINT = 'https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)';

const base64Url = (buf) => buf.toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');

export function buildAuthUrl(clientId, redirectUri, state, codeChallenge) {
  // "select_account" so a browser holding one Google session still asks
  // which account to use, instead of silently picking one that may not see
  // the Drive folder.
  const q = new URLSearchParams({
    response_type: 'code',
    client_id: clientId.trim(),
    redirect_uri: redirectUri,
    scope: SCOPE,
    state,
    code_challenge: codeChallenge,
    code_challenge_method: 'S256',
    access_type: 'offline',
    prompt: 'consent select_account',
  });
  return `${AUTH_ENDPOINT}?${q.toString()}`;
}

export function openBrowser(url) {
  try {
    let child;
    if (process.platform === 'win32') child = spawn('rundll32', ['url.dll,FileProtocolHandler', url], { detached: true, stdio: 'ignore' });
    else if (process.platform === 'darwin') child = spawn('open', [url], { detached: true, stdio: 'ignore' });
    else child = spawn('xdg-open', [url], { detached: true, stdio: 'ignore' });
    child.on('error', () => { /* the URL is printed too */ });
    child.unref();
  } catch { /* the URL is printed too */ }
}

/**
 * OAuth 2.0 installed-app loopback flow with PKCE. Prints the consent URL
 * (and tries to open it), waits up to 5 minutes for the redirect to
 * 127.0.0.1, then exchanges the code.
 */
export async function signIn(clientId, clientSecret, { log = console.error, onUrl, open = true, signal } = {}) {
  if (!clientId || !clientSecret) {
    throw new ConfigError('Google OAuth client ID / client secret are not set (driveClientId / driveClientSecret).');
  }
  const verifier = base64Url(crypto.randomBytes(64));
  const challenge = base64Url(crypto.createHash('sha256').update(verifier, 'ascii').digest());
  const state = base64Url(crypto.randomBytes(32));

  const server = http.createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const port = server.address().port;
  const redirectUri = `http://127.0.0.1:${port}/unilfs/`;

  try {
    const query = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new StorageError('Google sign-in timed out after 5 minutes.')), 5 * 60 * 1000);
      if (signal) {
        signal.addEventListener('abort', () => {
          clearTimeout(timer);
          const e = new Error('Google sign-in was cancelled.');
          e.name = 'AbortError';
          reject(e);
        }, { once: true });
      }
      server.on('request', (req, res) => {
        const u = new URL(req.url, redirectUri);
        if (!u.pathname.startsWith('/unilfs')) {
          res.writeHead(404).end();
          return;
        }
        const ok = !u.searchParams.get('error') && !!u.searchParams.get('code') && u.searchParams.get('state') === state;
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
        res.end(ok
          ? "<html><body style='font-family:sans-serif;text-align:center;margin-top:4em'><h2>UniLFS: sign-in complete</h2><p>You can close this tab and return to the terminal.</p></body></html>"
          : "<html><body style='font-family:sans-serif;text-align:center;margin-top:4em'><h2>UniLFS: sign-in failed</h2><p>Return to the terminal for details.</p></body></html>");
        clearTimeout(timer);
        resolve(u.searchParams);
      });
      const authUrl = buildAuthUrl(clientId, redirectUri, state, challenge);
      log('Open this URL in a browser to sign in to Google Drive:\n\n  ' + authUrl + '\n');
      if (onUrl) onUrl(authUrl);
      if (open) openBrowser(authUrl);
    });

    const code = query.get('code');
    const error = query.get('error');
    if (error || !code || query.get('state') !== state) {
      throw new StorageError(`Google sign-in failed: ${error || 'unexpected response'}.`);
    }
    const tokens = await exchange(new URLSearchParams({
      code,
      client_id: clientId.trim(),
      client_secret: clientSecret.trim(),
      redirect_uri: redirectUri,
      code_verifier: verifier,
      grant_type: 'authorization_code',
    }).toString(), null);
    tokens.accountEmail = await fetchAccountEmail(tokens.accessToken);
    return tokens;
  } finally {
    server.close();
  }
}

export function refresh(clientId, clientSecret, refreshToken, signal) {
  return exchange(new URLSearchParams({
    client_id: clientId,
    client_secret: clientSecret,
    refresh_token: refreshToken,
    grant_type: 'refresh_token',
  }).toString(), refreshToken, signal);
}

async function exchange(form, fallbackRefreshToken, signal) {
  const r = await request('POST', TOKEN_ENDPOINT, {
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: form,
    signal,
  });
  const text = await readText(r);
  let parsed = null;
  try { parsed = JSON.parse(text); } catch { /* not JSON */ }
  if (r.status < 200 || r.status >= 300 || !parsed || !parsed.access_token) {
    const detail = parsed ? `${parsed.error || ''} ${parsed.error_description || ''}`.trim() : text;
    const hint = parsed && parsed.error === 'invalid_grant'
      ? ' The refresh token may be expired or revoked - run "mylfs login" again.'
        + " (While an OAuth consent screen is in 'Testing' status, refresh tokens expire after 7 days.)"
      : '';
    throw new StorageError(`Google token request failed (HTTP ${r.status}): ${detail}.${hint}`);
  }
  return {
    accessToken: parsed.access_token,
    refreshToken: parsed.refresh_token || fallbackRefreshToken,
    expiresAt: Date.now() + (Math.max(120, parsed.expires_in || 0) - 60) * 1000,
  };
}

/** Best effort: the address is only ever shown to a human. Never throws. */
export async function fetchAccountEmail(accessToken, signal) {
  if (!accessToken) return null;
  try {
    const r = await request('GET', ABOUT_ENDPOINT, { headers: { authorization: `Bearer ${accessToken}` }, signal });
    const text = await readText(r);
    if (r.status < 200 || r.status >= 300) return null;
    const about = JSON.parse(text);
    return (about && about.user && about.user.emailAddress) || null;
  } catch {
    return null;
  }
}
