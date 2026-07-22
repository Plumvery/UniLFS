using System.Collections.Generic;

namespace UniLFS.Editor
{
    public enum UniLfsFileState
    {
        /// <summary>Local file exists and matches the manifest hash.</summary>
        UpToDate,
        /// <summary>
        /// Local file changed since this machine last synced, while the manifest
        /// stayed where it was. Needs Push.
        /// </summary>
        Modified,
        /// <summary>
        /// The manifest moved on (someone else pushed a new version) while the
        /// local file stayed where this machine last synced it. Needs Pull —
        /// and must never be pushed, or the manifest would roll back to this
        /// machine's older copy.
        /// </summary>
        Outdated,
        /// <summary>
        /// Local file and manifest both moved since the last sync, in different
        /// directions. Neither side can win automatically.
        /// </summary>
        Conflicted,
        /// <summary>Tracked in the manifest but missing on disk (needs Pull).</summary>
        MissingLocal,
    }

    /// <summary>
    /// Decides what a tracked file's hashes mean by comparing them against the
    /// baseline — the manifest hash this machine last agreed with, recorded by
    /// <see cref="UniLfsStateCache"/>.
    ///
    /// Local hash and manifest hash alone cannot carry this: "I edited it" and
    /// "someone else pushed a newer one" both read as local != manifest, and
    /// they need opposite fixes. The baseline is the third point that says which
    /// side actually moved, the same way a merge base does.
    /// </summary>
    public static class UniLfsThreeWay
    {
        public static UniLfsFileState Classify(string localHash, string manifestHash, string baselineHash)
        {
            if (localHash == manifestHash) return UniLfsFileState.UpToDate;
            // No baseline (pre-0.3.3 cache, deleted Library/, never synced here):
            // the divergence cannot be attributed, so report the reading that
            // makes every caller leave local content alone.
            if (string.IsNullOrEmpty(baselineHash)) return UniLfsFileState.Modified;
            if (localHash == baselineHash) return UniLfsFileState.Outdated;
            if (manifestHash == baselineHash) return UniLfsFileState.Modified;
            return UniLfsFileState.Conflicted;
        }
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
        /// <summary>
        /// Whether this machine has a record of which manifest hash the file was
        /// last in sync with. False for files that were already diverged when
        /// baselines arrived, or when <c>Library/</c> was last wiped: those read
        /// as <see cref="UniLfsFileState.Modified"/> because that is the safe
        /// guess, not because anything established that the local side is the
        /// one that moved. Callers that act without the user watching should
        /// leave them alone.
        /// </summary>
        public bool BaselineKnown;
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
        /// <summary>
        /// Files Push refused to upload because the local copy is older than the
        /// manifest. Pushing them would roll the manifest back to this machine's
        /// stale version and undo whoever pushed last.
        /// </summary>
        public List<string> Outdated = new List<string>();
        /// <summary>
        /// Files where local content and the manifest both moved since this
        /// machine last synced. Push and Pull leave these alone; Track resolves
        /// them in favour of the local copy and lists them here so that choice
        /// gets reported rather than made silently. No consumer sees both kinds
        /// of result, so the two readings never meet.
        /// </summary>
        public List<string> Conflicted = new List<string>();
        /// <summary>
        /// Files Push skipped because it could not tell whether the local copy
        /// or the manifest is the newer one — this machine has no baseline for
        /// them (they were already diverged when baselines arrived, or
        /// <c>Library/</c> was wiped). Only populated for callers that asked for
        /// that guarantee; an explicit Push takes them.
        /// </summary>
        public List<string> Unattributed = new List<string>();
        public List<string> Errors = new List<string>();

        public bool HasErrors
        {
            get { return Errors.Count > 0; }
        }
    }
}
