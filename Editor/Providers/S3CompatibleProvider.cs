using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UniLFS.Editor
{
    /// <summary>
    /// Talks to any S3-compatible object store with path-style URLs:
    /// Cloudflare R2 (https://[account].r2.cloudflarestorage.com), Amazon S3
    /// regional endpoints, MinIO, Wasabi, ... Objects are stored under
    /// [prefix]/objects/[aa]/[sha256].
    /// </summary>
    public class S3CompatibleProvider : IUniLfsStorageProvider
    {
        readonly string _endpointBase;   // scheme://authority, no trailing slash
        readonly string _authority;
        readonly string _bucket;
        readonly string _region;
        readonly string _prefix;
        readonly string _accessKeyId;
        readonly string _secretAccessKey;

        public string DisplayName
        {
            get { return "S3 (" + _authority + "/" + _bucket + ")"; }
        }

        public S3CompatibleProvider(string endpoint, string bucket, string region, string prefix, string accessKeyId, string secretAccessKey)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new UniLfsConfigException("S3 endpoint is not set. Open Project Settings > UniLFS.");
            Uri parsed;
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out parsed) || (parsed.Scheme != "https" && parsed.Scheme != "http"))
                throw new UniLfsConfigException("S3 endpoint is not a valid http(s) URL: " + endpoint);
            if (string.IsNullOrWhiteSpace(bucket))
                throw new UniLfsConfigException("S3 bucket is not set. Open Project Settings > UniLFS.");
            if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
                throw new UniLfsConfigException(
                    "S3 credentials are not set. Add them in Project Settings > UniLFS (stored per-user, never committed), or set "
                    + UniLfsCredentials.EnvS3AccessKeyId + " / " + UniLfsCredentials.EnvS3SecretAccessKey + " for CI.");

            _authority = parsed.Authority;
            _endpointBase = parsed.Scheme + "://" + parsed.Authority;
            _bucket = bucket.Trim();
            _region = string.IsNullOrWhiteSpace(region) ? "auto" : region.Trim();
            _prefix = (prefix ?? "").Trim().Trim('/');
            _accessKeyId = accessKeyId.Trim();
            _secretAccessKey = secretAccessKey.Trim();
        }

        List<string> KeySegments(string hash)
        {
            var segments = new List<string> { _bucket };
            if (_prefix.Length > 0) segments.AddRange(_prefix.Split('/'));
            segments.Add("objects");
            segments.Add(hash.Substring(0, 2));
            segments.Add(hash);
            return segments;
        }

        HttpRequestMessage BuildRequest(HttpMethod method, List<string> pathSegments, string payloadSha256)
        {
            string encodedPath = S3SigV4.EncodePath(pathSegments);
            var now = DateTimeOffset.UtcNow;
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("host", _authority),
                new KeyValuePair<string, string>("x-amz-content-sha256", payloadSha256),
                new KeyValuePair<string, string>("x-amz-date", now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'")),
            };
            var signed = S3SigV4.Sign(method.Method, encodedPath, "", headers, payloadSha256,
                _accessKeyId, _secretAccessKey, _region, now);

            var request = new HttpRequestMessage(method, _endpointBase + encodedPath);
            request.Headers.Host = _authority;
            request.Headers.TryAddWithoutValidation("x-amz-date", signed.AmzDate);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadSha256);
            request.Headers.TryAddWithoutValidation("Authorization", signed.AuthorizationHeader);
            return request;
        }

        public async Task<bool> BlobExistsAsync(string hash, CancellationToken ct)
        {
            using (var request = BuildRequest(HttpMethod.Head, KeySegments(hash), S3SigV4.EmptyPayloadSha256))
            using (var response = await UniLfsHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.OK) return true;
                if (response.StatusCode == HttpStatusCode.NotFound) return false;
                throw await ErrorAsync("HEAD", hash, response).ConfigureAwait(false);
            }
        }

        public async Task UploadBlobAsync(string hash, string sourceAbsPath, IProgress<long> bytesTransferred, CancellationToken ct)
        {
            using (var fs = new FileStream(sourceAbsPath, FileMode.Open, FileAccess.Read, FileShare.Read, UniLfsHasher.BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var progressStream = new UniLfsReadProgressStream(fs, bytesTransferred))
            using (var request = BuildRequest(HttpMethod.Put, KeySegments(hash), hash))
            {
                var content = new StreamContent(progressStream, UniLfsHasher.BufferSize);
                content.Headers.ContentLength = fs.Length;
                request.Content = content;
                using (var response = await UniLfsHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw await ErrorAsync("PUT", hash, response).ConfigureAwait(false);
                }
            }
        }

        public async Task DownloadBlobAsync(string hash, string destAbsPath, IProgress<long> bytesTransferred, CancellationToken ct)
        {
            using (var request = BuildRequest(HttpMethod.Get, KeySegments(hash), S3SigV4.EmptyPayloadSha256))
            using (var response = await UniLfsHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw await ErrorAsync("GET", hash, response).ConfigureAwait(false);
                await UniLfsHttp.DownloadToFileVerifiedAsync(response, hash, destAbsPath, bytesTransferred, ct).ConfigureAwait(false);
            }
        }

        public async Task<string> TestConnectionAsync(CancellationToken ct)
        {
            // HEAD for a well-formed key that should not exist: a clean 404
            // proves endpoint, bucket, credentials and clock are all fine.
            string probe = new string('0', 64);
            bool exists = await BlobExistsAsync(probe, ct).ConfigureAwait(false);
            return "Connected to " + DisplayName + (exists ? "." : ". Bucket is reachable and credentials are valid.");
        }

        async Task<UniLfsStorageException> ErrorAsync(string op, string hash, HttpResponseMessage response)
        {
            string body = "";
            try
            {
                if (response.Content != null)
                {
                    body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 2000) body = body.Substring(0, 2000) + "...";
                }
            }
            catch (Exception) { }

            string hint = "";
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 401)
                hint = " Check the access key / secret, that the token has object read & write permission for this bucket, and that your system clock is correct.";
            else if (response.StatusCode == HttpStatusCode.NotFound)
                hint = " Check the bucket name.";

            return new UniLfsStorageException(
                "S3 " + op + " " + hash.Substring(0, 8) + "... failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "." + hint
                + (string.IsNullOrEmpty(body) ? "" : "\n" + body));
        }

        public void Dispose()
        {
        }
    }
}
