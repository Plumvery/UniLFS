using System;
using System.IO;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// The little bit of .meta handling UniLFS needs: reading the GUID that
    /// scenes and prefabs reference, and recreating a .meta that Unity already
    /// discarded.
    ///
    /// UniLFS never tracks .meta files (they belong in git) and never edits one
    /// that exists. It only writes a missing one, and only with a GUID recorded
    /// in the manifest, so the asset keeps the identity the rest of the project
    /// already points at.
    /// </summary>
    public static class UniLfsMetaFile
    {
        public const string Extension = ".meta";

        public static string PathFor(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ? null : assetPath + Extension;
        }

        /// <summary>A Unity GUID is exactly 32 lowercase-or-uppercase hex digits.</summary>
        public static bool IsValidGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid) || guid.Length != 32) return false;
            foreach (var c in guid)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the GUID out of a .meta file, or null when the file is absent,
        /// unreadable or does not carry one. Only the head of the file is read -
        /// the GUID is on the second line of every .meta Unity writes.
        /// </summary>
        public static string ReadGuid(string metaPath)
        {
            if (string.IsNullOrEmpty(metaPath) || !File.Exists(metaPath)) return null;
            try
            {
                using (var reader = new StreamReader(metaPath, Encoding.UTF8))
                {
                    for (int line = 0; line < 16; line++)
                    {
                        var text = reader.ReadLine();
                        if (text == null) break;
                        var trimmed = text.Trim();
                        if (!trimmed.StartsWith("guid:", StringComparison.Ordinal)) continue;
                        var guid = trimmed.Substring("guid:".Length).Trim();
                        return IsValidGuid(guid) ? guid : null;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        /// <summary>
        /// Writes a minimal .meta carrying <paramref name="guid"/>, but only
        /// when no .meta exists at the path. Returns false when one is already
        /// there (whatever it says) or the GUID is not usable.
        ///
        /// The importer section is deliberately left out: Unity fills it in on
        /// the next import. That loses non-default import settings, so a .meta
        /// restored from git is always better than one rebuilt here. This runs
        /// anyway because the alternative is worse: a wrong GUID breaks every
        /// reference to the asset, while default import settings merely look
        /// wrong and can be set again.
        /// </summary>
        public static bool WriteMinimal(string metaPath, string guid)
        {
            if (string.IsNullOrEmpty(metaPath) || !IsValidGuid(guid)) return false;
            if (File.Exists(metaPath)) return false;
            try
            {
                var dir = Path.GetDirectoryName(metaPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var text = "fileFormatVersion: 2\nguid: " + guid + "\n";
                File.WriteAllText(metaPath, text, new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
