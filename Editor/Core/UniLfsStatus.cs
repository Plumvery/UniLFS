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
    }

    public struct UniLfsProgress
    {
        public string Phase;
        public string Item;
        public int Done;
        public int Total;
        /// <summary>0..1 progress within the current item (byte progress for transfers).</summary>
        public float ItemProgress;
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
