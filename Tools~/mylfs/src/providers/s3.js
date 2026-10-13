import fs from 'node:fs';
import { request, readText, drain, downloadVerified, StorageError, ConfigError } from './http.js';
import { sign, encodePath, amzDate, EMPTY_PAYLOAD_SHA256 } from './sigv4.js';
import { ENV } from '../project.js';

/**
 * Any S3-compatible store with path-style URLs: Cloudflare R2, Amazon S3,
 * MinIO, Wasabi, ... Objects live at [prefix]/objects/[aa]/[sha256], the
 * same keys the Unity package uses.
 */
export class S3Provider {
  constructor({ endpoint, bucket, region, prefix, accessKeyId, secretAccessKey }) {
    if (!endpoint || !endpoint.trim()) throw new ConfigError('S3 endpoint is not set (s3Endpoint). Run "mylfs config set s3Endpoint <url>".');
    let parsed;
    try {
      parsed = new URL(endpoint.trim());
    } catch {
      parsed = null;
    }
    if (!parsed || (parsed.protocol !== 'https:' && parsed.protocol !== 'http:')) {
      throw new ConfigError('S3 endpoint is not a valid http(s) URL: ' + endpoint);
    }
    if (!bucket || !bucket.trim()) throw new ConfigError('S3 bucket is not set (s3Bucket). Run "mylfs config set s3Bucket <name>".');
    if (!accessKeyId || !secretAccessKey) {
      throw new ConfigError(`S3 credentials are not set. Run "mylfs login" (stored per-user, never committed), or set ${ENV.s3AccessKeyId} / ${ENV.s3SecretAccessKey}.`);
    }
    this.host = parsed.host;
    this.base = `${parsed.protocol}//${parsed.host}`;
    this.bucket = bucket.trim();
    this.region = region && region.trim() ? region.trim() : 'auto';
    this.prefix = (prefix || '').trim().replace(/^\/+|\/+$/g, '');
    this.accessKeyId = accessKeyId.trim();
    this.secretAccessKey = secretAccessKey.trim();
    this.displayName = `S3 (${this.host}/${this.bucket})`;
  }

  keyPath(hash) {
    const segments = [this.bucket];
    if (this.prefix) segments.push(...this.prefix.split('/'));
    segments.push('objects', hash.slice(0, 2), hash);
    return encodePath(segments);
  }

  signedHeaders(method, uri, payloadSha256) {
    const date = new Date();
    const signed = sign({
      method,
      canonicalUri: uri,
      headers: { host: this.host, 'x-amz-content-sha256': payloadSha256, 'x-amz-date': amzDate(date) },
      payloadSha256,
      accessKeyId: this.accessKeyId,
      secretAccessKey: this.secretAccessKey,
      region: this.region,
      date,
    });
    return {
      'x-amz-date': signed.amzDate,
      'x-amz-content-sha256': payloadSha256,
      authorization: signed.authorization,
    };
  }

  async blobExists(hash, signal) {
    const uri = this.keyPath(hash);
    const r = await request('HEAD', this.base + uri, { headers: this.signedHeaders('HEAD', uri, EMPTY_PAYLOAD_SHA256), signal });
    drain(r);
    if (r.status === 200) return true;
    if (r.status === 404) return false;
    throw await this.error('HEAD', hash, r);
  }

  async uploadBlob(hash, srcAbsPath, onBytes, signal) {
    const uri = this.keyPath(hash);
    const size = fs.statSync(srcAbsPath).size;
    // The blob's name is its SHA-256, which is exactly the payload hash
    // SigV4 wants - so the upload is signed without reading the file twice.
    const headers = { ...this.signedHeaders('PUT', uri, hash), 'content-length': String(size) };
    const r = await request('PUT', this.base + uri, { headers, body: { file: srcAbsPath, onBytes }, signal });
    if (r.status < 200 || r.status >= 300) throw await this.error('PUT', hash, r);
    drain(r);
  }

  async downloadBlob(hash, destAbsPath, onBytes, signal) {
    const uri = this.keyPath(hash);
    const r = await request('GET', this.base + uri, { headers: this.signedHeaders('GET', uri, EMPTY_PAYLOAD_SHA256), signal });
    if (r.status < 200 || r.status >= 300) throw await this.error('GET', hash, r);
    await downloadVerified(r.res, hash, destAbsPath, onBytes, signal);
  }

  async testConnection(signal) {
    // HEAD a well-formed key that should not exist: a clean 404 proves
    // endpoint, bucket, credentials and clock are all fine.
    await this.blobExists('0'.repeat(64), signal);
    return `Connected to ${this.displayName}. Bucket is reachable and credentials are valid.`;
  }

  async error(op, hash, r) {
    let body = '';
    try {
      if (op !== 'HEAD') body = await readText(r);
      if (body.length > 2000) body = body.slice(0, 2000) + '...';
    } catch { /* ignore */ }
    let hint = '';
    if (r.status === 401 || r.status === 403) {
      hint = ' Check the access key / secret, that the token has object read & write permission for this bucket, and that your system clock is correct.';
    } else if (r.status === 404) {
      hint = ' Check the bucket name.';
    }
    return new StorageError(`S3 ${op} ${hash.slice(0, 8)}... failed: HTTP ${r.status} ${r.statusText}.${hint}${body ? '\n' + body : ''}`);
  }
}
