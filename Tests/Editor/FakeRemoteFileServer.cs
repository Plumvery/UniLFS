using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// A throwaway S3-compatible object server that keeps its objects in a
    /// directory on disk, so a test can play the part of the remote storage two
    /// clones share.
    ///
    /// It exists because the interesting rules in Push and Pull are about what
    /// one machine does to another one's work, and those only appear once the
    /// two really talk through storage. Substituting a fake
    /// <see cref="IUniLfsStorageProvider"/> would skip
    /// <see cref="S3CompatibleProvider"/>, the signing, the hash verification on
    /// download and the HTTP status handling — the layer where a broken
    /// round trip is most likely to actually go wrong.
    ///
    /// It speaks only what UniLFS uses: path-style HEAD, GET and PUT of a single
    /// object per request, SigV4-signed. Requests are answered with
    /// <c>Connection: close</c>, one per connection, which is why no keep-alive
    /// state machine is needed here.
    ///
    /// It is deliberately strict:
    /// - Signatures are verified with <see cref="S3SigV4"/> against the secret
    ///   the fixture configured; a mismatch is a 403, exactly as R2 and S3
    ///   answer. Without that, every other test would pass just as well against
    ///   a server that ignored authentication entirely.
    /// - A PUT body whose SHA-256 does not match the <c>x-amz-content-sha256</c>
    ///   the client signed is a 400, so a corrupted upload cannot be stored
    ///   under the hash it claims.
    /// - Keys that try to escape the object tree are a 400.
    ///
    /// Anything the server itself got wrong lands in <see cref="Faults"/>, so a
    /// test fails on a broken fixture instead of silently reading a 500.
    /// </summary>
    public sealed class FakeRemoteFileServer : IDisposable
    {
        readonly string _root;
        readonly string _bucket;
        readonly string _accessKeyId;
        readonly string _secretAccessKey;
        readonly TcpListener _listener;
        readonly Thread _acceptThread;
        readonly List<string> _faults = new List<string>();
        readonly object _faultLock = new object();

        volatile bool _stopping;
        int _heads;
        int _gets;
        int _puts;
        int _rejected;

        /// <param name="root">
        /// Directory the objects are written under, as
        /// <c>[root]/[bucket]/[prefix]/objects/[aa]/[sha256]</c> — the same key
        /// layout <see cref="S3CompatibleProvider"/> builds, so a test can point
        /// at a blob on disk and say what it is.
        /// </param>
        public FakeRemoteFileServer(string root, string bucket, string accessKeyId, string secretAccessKey)
        {
            _root = Path.GetFullPath(root);
            _bucket = bucket;
            _accessKeyId = accessKeyId;
            _secretAccessKey = secretAccessKey;
            Directory.CreateDirectory(BucketRoot);

            // Port 0 lets the OS pick a free one, so parallel test runs and
            // leftover sockets from an earlier run cannot collide.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "FakeRemoteFileServer" };
            _acceptThread.Start();
        }

        public int Port { get; private set; }

        /// <summary>Value for <see cref="UniLfsSettings.s3Endpoint"/>.</summary>
        public string Endpoint
        {
            get { return "http://127.0.0.1:" + Port; }
        }

        public string Bucket
        {
            get { return _bucket; }
        }

        /// <summary>Existence checks answered (Push and Verify make these).</summary>
        public int Heads
        {
            get { return Volatile.Read(ref _heads); }
        }

        /// <summary>Downloads served.</summary>
        public int Gets
        {
            get { return Volatile.Read(ref _gets); }
        }

        /// <summary>
        /// Uploads accepted. This is the number that says whether Push actually
        /// re-sent content it did not have to.
        /// </summary>
        public int Puts
        {
            get { return Volatile.Read(ref _puts); }
        }

        /// <summary>Requests refused because the signature did not verify.</summary>
        public int Rejected
        {
            get { return Volatile.Read(ref _rejected); }
        }

        /// <summary>Problems in the fixture itself, not answers it gave on purpose.</summary>
        public List<string> Faults
        {
            get { lock (_faultLock) return new List<string>(_faults); }
        }

        public void ResetCounters()
        {
            Volatile.Write(ref _heads, 0);
            Volatile.Write(ref _gets, 0);
            Volatile.Write(ref _puts, 0);
            Volatile.Write(ref _rejected, 0);
        }

        /// <summary>Where a blob would live on disk, whether or not it is there.</summary>
        public string PathOf(string prefix, string hash)
        {
            var segments = new List<string> { BucketRoot };
            if (!string.IsNullOrEmpty(prefix)) segments.AddRange(prefix.Trim('/').Split('/'));
            segments.Add("objects");
            segments.Add(hash.Substring(0, 2));
            segments.Add(hash);
            return Path.Combine(segments.ToArray());
        }

        public bool Has(string prefix, string hash)
        {
            return File.Exists(PathOf(prefix, hash));
        }

        /// <summary>
        /// Drops a blob, standing in for the bucket being emptied behind the
        /// project's back — the case a manifest cannot notice on its own.
        /// </summary>
        public void Delete(string prefix, string hash)
        {
            var path = PathOf(prefix, hash);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Every object currently stored, by hash.</summary>
        public List<string> StoredHashes
        {
            get
            {
                string objects = BucketRoot;
                if (!Directory.Exists(objects)) return new List<string>();
                return Directory.GetFiles(objects, "*", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
            }
        }

        string BucketRoot
        {
            get { return Path.Combine(_root, _bucket); }
        }

        string StagingRoot
        {
            // Outside the bucket directory so a half-received upload never shows
            // up in StoredHashes.
            get { return Path.Combine(_root, ".incoming"); }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener.Stop(); }
            catch (Exception) { }
            if (_acceptThread != null) _acceptThread.Join(TimeSpan.FromSeconds(5));
        }

        void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return; // listener stopped
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                // Push and Pull run several transfers at once, so connections
                // have to be served concurrently or the semaphore in the core
                // would be measuring this fixture instead of itself.
                var served = client;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Serve(served);
                    }
                    catch (Exception e)
                    {
                        Fault(e.GetType().Name + ": " + e.Message);
                    }
                    finally
                    {
                        try { served.Close(); }
                        catch (Exception) { }
                    }
                });
            }
        }

        void Serve(TcpClient client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            string requestLine = ReadLine(stream);
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                Send(stream, 400, "Bad Request", ErrorBody("MalformedRequest", requestLine));
                Finish(client, stream);
                return;
            }
            string method = parts[0].ToUpperInvariant();
            string target = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string header;
            while (!string.IsNullOrEmpty(header = ReadLine(stream)))
            {
                int colon = header.IndexOf(':');
                if (colon > 0) headers[header.Substring(0, colon).Trim()] = header.Substring(colon + 1).Trim();
            }

            string path = target, query = "";
            int mark = target.IndexOf('?');
            if (mark >= 0)
            {
                path = target.Substring(0, mark);
                query = target.Substring(mark + 1);
            }

            // The body is always consumed before anything is written back, even
            // for a request that is about to be refused: replying and closing
            // with bytes still in flight resets the connection, which the client
            // reports as a transport error rather than the status we chose.
            string staged = null;
            string bodyHash = null;
            long declared = ContentLength(headers);
            try
            {
                if (declared > 0)
                {
                    Directory.CreateDirectory(StagingRoot);
                    staged = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
                    bodyHash = Receive(stream, declared, staged);
                }

                string denial = Verify(method, path, query, headers);
                if (denial != null)
                {
                    Interlocked.Increment(ref _rejected);
                    Send(stream, 403, "Forbidden", ErrorBody("SignatureDoesNotMatch", denial));
                    return;
                }

                string objectPath;
                string keyProblem = ResolveKey(path, out objectPath);
                if (keyProblem != null)
                {
                    Send(stream, 400, "Bad Request", ErrorBody("InvalidRequest", keyProblem));
                    return;
                }

                switch (method)
                {
                    case "HEAD":
                        Interlocked.Increment(ref _heads);
                        if (File.Exists(objectPath))
                            SendHeadersOnly(stream, 200, "OK", new FileInfo(objectPath).Length);
                        else
                            SendHeadersOnly(stream, 404, "Not Found", 0);
                        return;

                    case "GET":
                        Interlocked.Increment(ref _gets);
                        if (!File.Exists(objectPath))
                        {
                            Send(stream, 404, "Not Found", ErrorBody("NoSuchKey", path));
                            return;
                        }
                        Send(stream, 200, "OK", File.ReadAllBytes(objectPath), "application/octet-stream");
                        return;

                    case "PUT":
                        Interlocked.Increment(ref _puts);
                        string signedPayload;
                        headers.TryGetValue("x-amz-content-sha256", out signedPayload);
                        // An empty body is legal HTTP but never a blob UniLFS
                        // means to store, so it is refused rather than written
                        // under a hash it does not have.
                        if (staged == null) bodyHash = UniLfsHasher.Sha256OfString("");
                        if (!string.Equals(bodyHash, signedPayload, StringComparison.OrdinalIgnoreCase))
                        {
                            Send(stream, 400, "Bad Request", ErrorBody("XAmzContentSHA256Mismatch",
                                "body hashes to " + bodyHash + ", header claimed " + signedPayload));
                            return;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(objectPath));
                        if (staged != null) File.Copy(staged, objectPath, true);
                        else File.WriteAllBytes(objectPath, new byte[0]);
                        Send(stream, 200, "OK", null, null,
                            new[] { new KeyValuePair<string, string>("ETag", "\"" + bodyHash + "\"") });
                        return;

                    default:
                        Send(stream, 405, "Method Not Allowed", ErrorBody("MethodNotAllowed", method));
                        return;
                }
            }
            finally
            {
                try { if (staged != null && File.Exists(staged)) File.Delete(staged); }
                catch (Exception) { }
                Finish(client, stream);
            }
        }

        /// <summary>
        /// Recomputes the signature the client should have produced and compares
        /// it. Returns null when the request is authentic, otherwise why it is
        /// not — which the test can read back out of the reported error.
        /// </summary>
        string Verify(string method, string encodedPath, string query, Dictionary<string, string> headers)
        {
            const string algorithm = "AWS4-HMAC-SHA256";
            string authorization;
            if (!headers.TryGetValue("Authorization", out authorization) || string.IsNullOrEmpty(authorization))
                return "no Authorization header";
            if (!authorization.StartsWith(algorithm + " ", StringComparison.Ordinal))
                return "unexpected authorization scheme";

            string credential = null, signedHeaders = null, signature = null;
            foreach (var raw in authorization.Substring(algorithm.Length + 1).Split(','))
            {
                var field = raw.Trim();
                int eq = field.IndexOf('=');
                if (eq <= 0) continue;
                string name = field.Substring(0, eq);
                string value = field.Substring(eq + 1);
                if (name == "Credential") credential = value;
                else if (name == "SignedHeaders") signedHeaders = value;
                else if (name == "Signature") signature = value;
            }
            if (credential == null || signedHeaders == null || signature == null)
                return "incomplete Authorization header";

            // keyId/date/region/service/aws4_request
            var scope = credential.Split('/');
            if (scope.Length != 5) return "malformed credential scope: " + credential;
            if (scope[0] != _accessKeyId) return "unknown access key id " + scope[0];
            if (scope[3] != "s3" || scope[4] != "aws4_request") return "unexpected credential scope: " + credential;

            string amzDate;
            if (!headers.TryGetValue("x-amz-date", out amzDate)) return "no x-amz-date header";
            DateTimeOffset signedAt;
            if (!DateTimeOffset.TryParseExact(amzDate, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out signedAt))
                return "unparseable x-amz-date " + amzDate;
            if (scope[1] != signedAt.UtcDateTime.ToString("yyyyMMdd"))
                return "credential scope date " + scope[1] + " does not match x-amz-date " + amzDate;

            string payloadSha;
            if (!headers.TryGetValue("x-amz-content-sha256", out payloadSha))
                return "no x-amz-content-sha256 header";

            var signed = new List<KeyValuePair<string, string>>();
            foreach (var name in signedHeaders.Split(';'))
            {
                string value;
                if (!headers.TryGetValue(name, out value)) return "signed header " + name + " was not sent";
                signed.Add(new KeyValuePair<string, string>(name, value));
            }

            // The path is used exactly as it arrived: the canonical request is
            // built from the encoded form the client signed, not from a decoded
            // round trip of it.
            var expected = S3SigV4.Sign(method, encodedPath, query, signed, payloadSha,
                _accessKeyId, _secretAccessKey, scope[2], signedAt);
            return string.Equals(expected.Signature, signature, StringComparison.OrdinalIgnoreCase)
                ? null
                : "signature mismatch";
        }

        /// <summary>
        /// Maps a request path to a file, or explains why it is not addressing
        /// an object in this bucket.
        /// </summary>
        string ResolveKey(string encodedPath, out string objectPath)
        {
            objectPath = null;
            var segments = new List<string>();
            foreach (var raw in encodedPath.Split('/'))
            {
                if (raw.Length == 0) continue;
                string segment = Uri.UnescapeDataString(raw);
                if (segment == "." || segment == ".." || segment.IndexOf('\\') >= 0 || segment.IndexOf(':') >= 0
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return "illegal key segment: " + segment;
                segments.Add(segment);
            }
            if (segments.Count < 2) return "not an object key: " + encodedPath;
            if (segments[0] != _bucket) return "no such bucket: " + segments[0];

            var all = new List<string> { _root };
            all.AddRange(segments);
            string full = Path.GetFullPath(Path.Combine(all.ToArray()));
            string bucketRoot = BucketRoot;
            if (!full.StartsWith(bucketRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "key escapes the bucket: " + encodedPath;
            objectPath = full;
            return null;
        }

        static long ContentLength(Dictionary<string, string> headers)
        {
            string value;
            long length;
            if (headers.TryGetValue("Content-Length", out value) && long.TryParse(value, out length) && length > 0)
                return length;
            return 0;
        }

        /// <summary>Streams the request body to a file and returns its SHA-256.</summary>
        static string Receive(Stream source, long length, string destPath)
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) throw new IOException("request body ended " + remaining + " byte(s) early");
                    sha.AppendData(buffer, 0, read);
                    dst.Write(buffer, 0, read);
                    remaining -= read;
                }
                return UniLfsHasher.ToHex(sha.GetHashAndReset());
            }
        }

        static string ReadLine(Stream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
            return sb.Length > 0 ? sb.ToString().TrimEnd('\r') : null;
        }

        /// <summary>
        /// The XML error document the real services answer with. UniLFS quotes
        /// the body into its own exception message, so this is where a test reads
        /// back why a request was refused.
        /// </summary>
        static byte[] ErrorBody(string code, string message)
        {
            return Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>" + code + "</Code><Message>"
                + (message ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                + "</Message></Error>");
        }

        static void Send(Stream stream, int status, string reason, byte[] body, string contentType = "application/xml", IEnumerable<KeyValuePair<string, string>> extra = null)
        {
            Write(stream, status, reason, body == null ? 0 : body.Length, contentType, extra);
            if (body != null && body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>
        /// A HEAD reply: the object's length is declared but no body follows, the
        /// same shape the real services answer an existence check with.
        /// </summary>
        static void SendHeadersOnly(Stream stream, int status, string reason, long declaredLength)
        {
            Write(stream, status, reason, declaredLength, "application/octet-stream", null);
            stream.Flush();
        }

        static void Write(Stream stream, int status, string reason, long contentLength, string contentType, IEnumerable<KeyValuePair<string, string>> extra)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            head.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            if (contentLength > 0 && !string.IsNullOrEmpty(contentType))
                head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (extra != null)
                foreach (var kv in extra) head.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Half-closes before dropping the socket, so the client is guaranteed to
        /// read the whole reply instead of racing a reset.
        /// </summary>
        static void Finish(TcpClient client, Stream stream)
        {
            try { stream.Flush(); }
            catch (Exception) { }
            try { client.Client.Shutdown(SocketShutdown.Send); }
            catch (Exception) { }
        }

        void Fault(string message)
        {
            lock (_faultLock) _faults.Add(message);
        }
    }
}
