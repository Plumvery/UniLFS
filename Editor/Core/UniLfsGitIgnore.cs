using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// Maintains a marker-delimited block inside the project root .gitignore
    /// that hides tracked large files (and the per-user credential file) from
    /// git. Everything outside the markers is preserved untouched.
    /// </summary>
    public static class UniLfsGitIgnore
    {
        public const string BeginMarker = "# >>> UniLFS managed block - do not edit by hand >>>";
        public const string EndMarker = "# <<< UniLFS managed block <<<";

        /// <summary>
        /// Escapes gitignore pattern characters and anchors the path to the
        /// .gitignore location. '#' and '!' are only special at line start, but
        /// backslash-escaping them anywhere is valid and keeps this simple.
        /// </summary>
        public static string EscapeGitIgnorePath(string projectRelativePath)
        {
            var sb = new StringBuilder(projectRelativePath.Length + 8);
            foreach (var c in projectRelativePath)
            {
                if (c == '\\' || c == '*' || c == '?' || c == '[' || c == ']' || c == '#' || c == '!')
                    sb.Append('\\');
                sb.Append(c);
            }
            return "/" + sb;
        }

        public static void Update(string gitIgnorePath, IEnumerable<string> trackedProjectRelativePaths)
        {
            var block = new List<string> { BeginMarker, UniLfsPaths.UserSettingsGitIgnoreLine };
            block.AddRange(trackedProjectRelativePaths
                .Select(EscapeGitIgnorePath)
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal));
            block.Add(EndMarker);
            WriteBlock(gitIgnorePath, block);
        }

        static void WriteBlock(string gitIgnorePath, List<string> blockLines)
        {
            string eol = "\n";
            var existing = new List<string>();
            if (File.Exists(gitIgnorePath))
            {
                var text = File.ReadAllText(gitIgnorePath, Encoding.UTF8);
                if (text.Contains("\r\n")) eol = "\r\n";
                existing.AddRange(text.Replace("\r\n", "\n").Split('\n'));
                if (existing.Count > 0 && existing[existing.Count - 1] == "")
                    existing.RemoveAt(existing.Count - 1);
            }

            int begin = existing.FindIndex(l => l.Trim() == BeginMarker);
            int end = begin >= 0 ? existing.FindIndex(begin, l => l.Trim() == EndMarker) : -1;

            var result = new List<string>();
            if (begin >= 0 && end >= begin)
            {
                result.AddRange(existing.Take(begin));
                result.AddRange(blockLines);
                result.AddRange(existing.Skip(end + 1));
            }
            else
            {
                result.AddRange(existing);
                if (result.Count > 0 && result[result.Count - 1] != "") result.Add("");
                result.AddRange(blockLines);
            }

            File.WriteAllText(gitIgnorePath, string.Join(eol, result) + eol, new UTF8Encoding(false));
        }

        public static List<string> ReadManagedLines(string gitIgnorePath)
        {
            var result = new List<string>();
            if (!File.Exists(gitIgnorePath)) return result;
            var lines = File.ReadAllText(gitIgnorePath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            bool inside = false;
            foreach (var l in lines)
            {
                if (l.Trim() == BeginMarker) { inside = true; continue; }
                if (l.Trim() == EndMarker) inside = false;
                else if (inside) result.Add(l);
            }
            return result;
        }
    }
}
