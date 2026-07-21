using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace UniLFS.Editor
{
    public static class UniLfsHttp
    {
        static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(() =>
        {
            var client = new HttpClient();
            // Transfers of multi-gigabyte files must not hit the 100s default;
            // cancellation tokens are the only timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("UniLFS/0.1");
            return client;
        });

        public static HttpClient Client
        {
            get { return _client.Value; }
        }

        /// <summary>
        /// Streams a response body to a file while hashing it, then verifies the
        /// hash so a corrupted download never replaces a project asset.
        /// </summary>
        public static async Task DownloadToFileVerifiedAsync(HttpResponseMessage response, string expectedSha256, string destAbsPath, IProgress<long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destAbsPath));
            string tmp = destAbsPath + ".unilfs-partial";
            try
            {
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, UniLfsHasher.BufferSize))
                {
                    var buffer = new byte[UniLfsHasher.BufferSize];
                    long total = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        total += read;
                        if (progress != null) progress.Report(total);
                    }
                    string actual = UniLfsHasher.ToHex(sha.GetHashAndReset());
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new UniLfsStorageException(
                            "Downloaded content hash mismatch (expected " + expectedSha256 + ", got " + actual + "). The remote object may be corrupted.");
                }
                if (File.Exists(destAbsPath)) File.Delete(destAbsPath);
                File.Move(tmp, destAbsPath);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
        }
    }

    /// <summary>Read-only stream wrapper that reports cumulative bytes read.</summary>
    public class UniLfsReadProgressStream : Stream
    {
        readonly Stream _inner;
        readonly IProgress<long> _progress;
        long _total;

        public UniLfsReadProgressStream(Stream inner, IProgress<long> progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override bool CanRead { get { return _inner.CanRead; } }
        public override bool CanSeek { get { return _inner.CanSeek; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _inner.Length; } }

        public override long Position
        {
            get { return _inner.Position; }
            set { _inner.Position = value; }
        }

        public override void Flush()
        {
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
            Count(read);
            return read;
        }

        void Count(int read)
        {
            if (read > 0)
            {
                _total += read;
                if (_progress != null) _progress.Report(_total);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
