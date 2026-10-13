import crypto from 'node:crypto';

/** Minimal AWS Signature Version 4, enough for object HEAD/GET/PUT. */
export const EMPTY_PAYLOAD_SHA256 = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

/** AWS-style RFC 3986 encoding: uppercase hex, '~' untouched. */
export function uriEncode(value, encodeSlash) {
  let s = '';
  for (const b of Buffer.from(value, 'utf8')) {
    const c = String.fromCharCode(b);
    if (/[A-Za-z0-9\-._~]/.test(c) || (c === '/' && !encodeSlash)) s += c;
    else s += '%' + b.toString(16).toUpperCase().padStart(2, '0');
  }
  return s;
}

export function encodePath(segments) {
  return '/' + segments.map((s) => uriEncode(s, true)).join('/');
}

function hmac(key, data) {
  return crypto.createHmac('sha256', key).update(data, 'utf8').digest();
}

function normalizeHeaderValue(v) {
  return String(v ?? '').trim().replace(/ +/g, ' ');
}

export function amzDate(date) {
  return date.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '');
}

/**
 * @param {object} p
 * @param {string} p.method
 * @param {string} p.canonicalUri       already encoded
 * @param {string} [p.canonicalQuery]
 * @param {Record<string,string>} p.headers  must include host, x-amz-date
 * @param {string} p.payloadSha256
 * @param {string} p.accessKeyId
 * @param {string} p.secretAccessKey
 * @param {string} p.region
 * @param {Date} p.date
 */
export function sign({ method, canonicalUri, canonicalQuery = '', headers, payloadSha256, accessKeyId, secretAccessKey, region, date, service = 's3' }) {
  const stamp = amzDate(date);
  const day = stamp.slice(0, 8);
  const canonical = Object.entries(headers)
    .map(([k, v]) => [k.toLowerCase(), normalizeHeaderValue(v)])
    .sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
  const canonicalHeaders = canonical.map(([k, v]) => `${k}:${v}\n`).join('');
  const signedHeaders = canonical.map(([k]) => k).join(';');
  const canonicalRequest = [method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadSha256].join('\n');
  const scope = `${day}/${region}/${service}/aws4_request`;
  const stringToSign = [
    'AWS4-HMAC-SHA256',
    stamp,
    scope,
    crypto.createHash('sha256').update(canonicalRequest, 'utf8').digest('hex'),
  ].join('\n');
  const kDate = hmac('AWS4' + secretAccessKey, day);
  const kRegion = hmac(kDate, region);
  const kService = hmac(kRegion, service);
  const kSigning = hmac(kService, 'aws4_request');
  const signature = crypto.createHmac('sha256', kSigning).update(stringToSign, 'utf8').digest('hex');
  return {
    amzDate: stamp,
    canonicalRequest,
    stringToSign,
    signature,
    authorization: `AWS4-HMAC-SHA256 Credential=${accessKeyId}/${scope}, SignedHeaders=${signedHeaders}, Signature=${signature}`,
  };
}
