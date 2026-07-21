using System.Collections.Generic;

namespace UniLFS.Editor
{
    public enum UniLfsFileState
    {
        /// <summary>Local file exists and matches the manifest hash.</summary>
        UpToDate,
        /// <summary>Local file exists but differs from the manifest hash.</summary>
        Modified,
        /// <summary>Tracked in the manifest but missing on disk (needs Pull).</summary>
        MissingLocal,
    }

    public class UniLfsStatusEntry
    {
        public UniLfsManifestFile File;
        public UniLfsFileState State;
        public string CurrentHash;
        public long CurrentSize;
        /// <summary>
        /// Whether this machine has proof that the manifest's blob exists in
        /// remote storage. State alone cannot answer this: a freshly tracked
        /// file matches the manifest (UpToDate) while never having been
        /// uploaded. See <see cref="UniLfsRemoteBlobCache"/>.
        /// </summary>
        public bool RemoteKnown;
    }

    /// <summary>
    /// The outcome of a status check. <see cref="Files"/> is the list a UI
    /// draws; the other fields describe the optional remote check that ran
    /// first, and stay empty when it did not (<see cref="Verified"/> is false).
    /// </summary>
    public class UniLfsStatusReport
    {
        public List<UniLfsStatusEntry> Files = new List<UniLfsStatusEntry>();
        /// <summary>True when remote storage was actually asked about the manifest's blobs.</summary>
        public bool Verified;
        /// <summary>Distinct blobs storage confirmed it has.</summary>
        public int Confirmed;
        /// <summary>Tracked files whose blob storage answered for, and does not have.</summary>
        public List<string> MissingRemote = new List<string>();
        /// <summary>
        /// Why some blobs could not be checked (network or credential
        /// failures). Distinct from <see cref="MissingRemote"/>: no answer is
        /// not the same as "absent", so those keep whatever confirmation they
        /// already had.
        /// </summary>
        public List<string> Failures = new List<string>();
    }

    /// <summary>
    /// A snapshot of an operation's progress, produced by
    /// <see cref="UniLfsProgressReporter"/>. All fields describe the same
    /// instant, so a UI never mixes numbers from two different workers.
    /// </summary>
    public struct UniLfsProgress
    {
        public string Phase;
        /// <summary>Path of the longest-running in-flight item (stable while it runs).</summary>
        public string Item;
        /// <summary>Path of the item that just finished, if this report was triggered by one.</summary>
        public string Completed;
        /// <summary>Items finished in the current phase.</summary>
        public int Done;
        /// <summary>Items in the current phase.</summary>
        public int Total;
        /// <summary>0..1 progress within the current item (byte progress for transfers).</summary>
        public float ItemProgress;
        /// <summary>
        /// 0..1 progress of the whole operation. Already weighted across phases
        /// and, where sizes are known, by bytes rather than file count, and
        /// clamped so parallel workers can never make it run backwards. Use
        /// this instead of deriving a value from Done/Total.
        /// </summary>
        public float Fraction;
        /// <summary>Ready-to-display one-line description of what is happening.</summary>
        public string Label;
        public long DoneBytes;
        public long TotalBytes;
        /// <summary>Transfers currently in flight.</summary>
        public int Active;
    }

    public class UniLfsOpResult
    {
        public int Uploaded;
        public int Downloaded;
        public int Skipped;
        public int TrackedNew;
        public int TrackedUpdated;
        public int Untracked;
        public List<string> MissingLocal = new List<string>();
        public List<string> KeptModified = new List<string>();
        public List<string> NewlyTracked = new List<string>();
        public List<string> Errors = new List<string>();

        public bool HasErrors
        {
            get { return Errors.Count > 0; }
        }
    }
}
