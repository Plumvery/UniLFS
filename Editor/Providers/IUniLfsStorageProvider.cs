using System;
using System.Threading;
using System.Threading.Tasks;

namespace UniLFS.Editor
{
    /// <summary>
    /// A remote blob store addressed by SHA-256 content hash. Implementations
    /// must be callable from worker threads and must not touch Unity APIs
    /// (other than thread-safe logging).
    /// </summary>
    public interface IUniLfsStorageProvider : IDisposable
    {
        string DisplayName { get; }

        Task<bool> BlobExistsAsync(string hash, CancellationToken ct);

        Task UploadBlobAsync(string hash, string sourceAbsPath, IProgress<long> bytesTransferred, CancellationToken ct);

        /// <summary>
        /// Downloads a blob to <paramref name="destAbsPath"/> and verifies its
        /// SHA-256. The destination should live outside Assets/ (the core
        /// copies verified blobs into place afterwards); a temporary
        /// ".unilfs-partial" sibling file is used during the transfer.
        /// </summary>
        Task DownloadBlobAsync(string hash, string destAbsPath, IProgress<long> bytesTransferred, CancellationToken ct);

        /// <summary>Returns a human-readable success message, or throws.</summary>
        Task<string> TestConnectionAsync(CancellationToken ct);
    }

    /// <summary>Configuration is missing or invalid; the message tells the user where to fix it.</summary>
    public class UniLfsConfigException : Exception
    {
        public UniLfsConfigException(string message) : base(message) { }
    }

    /// <summary>A remote operation failed (network, auth, corrupted data, ...).</summary>
    public class UniLfsStorageException : Exception
    {
        public UniLfsStorageException(string message) : base(message) { }
        public UniLfsStorageException(string message, Exception inner) : base(message, inner) { }
    }
}
