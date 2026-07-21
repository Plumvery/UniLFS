using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniLFS.Editor
{
    public static class UniLfsHasher
    {
        public const int BufferSize = 1024 * 1024;

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Sha256OfString(string text)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        public static async Task<string> Sha256OfFileAsync(string absPath, IProgress<float> progress = null, CancellationToken ct = default(CancellationToken))
        {
            return await Task.Run(() =>
            {
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                {
                    long total = fs.Length;
                    long done = 0;
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, read);
                        done += read;
                        if (progress != null && total > 0) progress.Report((float)((double)done / total));
                    }
                    return ToHex(sha.GetHashAndReset());
                }
            }, ct).ConfigureAwait(false);
        }
    }
}
