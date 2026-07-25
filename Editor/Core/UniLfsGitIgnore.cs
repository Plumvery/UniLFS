using System;
using System.Collections.Generic;
using System.Linq;

namespace UniLFS.Editor
{
    /// <summary>
    /// Maintains a marker-delimited block inside the project root .gitignore
    /// that hides tracked large files (and the two per-user UniLFS files) from
    /// git. Everything outside the markers is preserved untouched.
    ///
    /// The block is derived from the manifest alone. It is a committed file, so
    /// deriving any of it from a local one — staging, say — would make two
    /// machines produce different content for it and turn the most boring file
    /// in the repository into a source of merge conflicts. Paths that are
    /// staged and not pushed are hidden by <see cref="UniLfsGitExclude"/>
    /// instead, which git never tracks.
    /// </summary>
    public static class UniLfsGitIgnore
    {
        public const string BeginMarker = "# >>> UniLFS managed block - do not edit by hand >>>";
        public const string EndMarker = "# <<< UniLFS managed block <<<";

        /// <summary>
        /// Escapes gitignore pattern characters and anchors the path to the
        /// .gitignore location.
        /// </summary>
        public static string EscapeGitIgnorePath(string projectRelativePath)
        {
            return UniLfsIgnoreBlock.Escape(projectRelativePath);
        }

        public static void Update(string gitIgnorePath, IEnumerable<string> trackedProjectRelativePaths)
        {
            var block = new List<string>
            {
                BeginMarker,
                UniLfsPaths.UserSettingsGitIgnoreLine,
                UniLfsPaths.StagedGitIgnoreLine,
            };
            block.AddRange(trackedProjectRelativePaths
                .Select(EscapeGitIgnorePath)
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal));
            block.Add(EndMarker);
            UniLfsIgnoreBlock.Write(gitIgnorePath, BeginMarker, EndMarker, block);
        }

        public static List<string> ReadManagedLines(string gitIgnorePath)
        {
            return UniLfsIgnoreBlock.ReadManagedLines(gitIgnorePath, BeginMarker, EndMarker);
        }
    }
}
