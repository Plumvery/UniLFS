using System;
using System.IO;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// A stand-in written at a tracked file's path while the real content is
    /// not on disk.
    ///
    /// Unity discards a .meta file whose asset it cannot find, and mints a new
    /// GUID when the asset reappears - which silently breaks every scene,
    /// prefab and Addressables entry that referenced the old one. Tracked files
    /// are gitignored, so a fresh clone always hits that window: the .meta comes
    /// from git, the asset does not.
    ///
    /// A placeholder does not win that first race - nothing managed runs early
    /// enough (see <see cref="UniLfsMetaGuard"/>). What it does is keep the
    /// window shut afterwards: with an asset at the path, the .meta the guard
    /// just restored survives every later refresh instead of being discarded and
    /// rebuilt on each one, and Pull can overwrite it in place under the GUID
    /// the project already references.
    ///
    /// A placeholder is deliberately identifiable from its own contents rather
    /// than from a side list, so a half-finished Pull, a deleted Library folder
    /// or a stale index can never make UniLFS mistake one for real content. That
    /// matters most for Push: uploading a placeholder would rewrite the manifest
    /// to point at it and orphan the real blob.
    /// </summary>
    public static class UniLfsPlaceholder
    {
        /// <summary>First bytes of every placeholder. ASCII, so the probe needs no decoding.</summary>
        public const string Marker = "UNILFS-PLACEHOLDER";

        /// <summary>
        /// Files larger than this are never probed. Placeholders are a few
        /// hundred bytes; tracked files are the ones too big for git. The bound
        /// keeps the check O(1) for real assets.
        /// </summary>
        public const int MaxLength = 4096;

        static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);

        /// <summary>
        /// Writes a placeholder, creating parent directories as needed. Returns
        /// false when something already occupies the path and is not a
        /// placeholder - real content is never overwritten.
        /// </summary>
        public static bool Write(string absPath, string hash, long realSize)
        {
            if (string.IsNullOrEmpty(absPath)) return false;
            if (File.Exists(absPath) && !IsPlaceholder(absPath)) return false;

            var dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append(Marker).Append('\n');
            sb.Append("sha256: ").Append(hash ?? "").Append('\n');
            sb.Append("size: ").Append(realSize).Append('\n');
            sb.Append('\n');
            sb.Append("UniLFS wrote this stand-in because the real file is not on disk yet.\n");
            sb.Append("It exists so Unity keeps the .meta next to it - an orphaned .meta loses\n");
            sb.Append("its GUID, and every reference to the asset breaks with it.\n");
            sb.Append("Run Window > UniLFS > Pull to replace it with the real content.\n");

            if (File.Exists(absPath)) File.SetAttributes(absPath, FileAttributes.Normal);
            File.WriteAllText(absPath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        /// <summary>
        /// True when the path holds a UniLFS placeholder rather than real
        /// content. Safe to call from worker threads and on paths that do not
        /// exist.
        ///
        /// Deliberately the only signature: an overload taking a FileInfo would
        /// save one stat per entry and make every <c>IsPlaceholder(null)</c>
        /// ambiguous to compile, which is a bad trade for a check that runs over
        /// tens of files.
        /// </summary>
        public static bool IsPlaceholder(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return false;
            try
            {
                var info = new FileInfo(absPath);
                if (!info.Exists) return false;
                if (info.Length < MarkerBytes.Length || info.Length > MaxLength) return false;

                using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[MarkerBytes.Length];
                    int read = 0;
                    while (read < head.Length)
                    {
                        int n = stream.Read(head, read, head.Length - read);
                        if (n <= 0) return false;
                        read += n;
                    }
                    for (int i = 0; i < head.Length; i++)
                        if (head[i] != MarkerBytes[i]) return false;
                    return true;
                }
            }
            catch (Exception)
            {
                // An unreadable file is not something we may treat as absent:
                // reporting it as a placeholder would let Pull overwrite it.
                return false;
            }
        }

        /// <summary>
        /// Removes a placeholder if one sits at the path. Real content is left
        /// alone. Pull overwrites in place instead of calling this; it exists
        /// for cleanup paths such as untracking.
        /// </summary>
        public static bool Clear(string absPath)
        {
            if (!IsPlaceholder(absPath)) return false;
            try
            {
                File.SetAttributes(absPath, FileAttributes.Normal);
                File.Delete(absPath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
