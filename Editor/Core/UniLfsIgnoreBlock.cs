using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// Reads and rewrites a marker-delimited block inside an ignore file,
    /// leaving everything outside the markers untouched.
    ///
    /// Two files need this and they are not interchangeable: the committed
    /// <c>.gitignore</c> hides the manifest's paths from everyone who clones the
    /// repository (see <see cref="UniLfsGitIgnore"/>), and the checkout-local
    /// <c>.git/info/exclude</c> hides paths this machine has staged and not
    /// pushed yet (see <see cref="UniLfsGitExclude"/>). Same file format, same
    /// rewriting problem, different audience — so the mechanics live here once
    /// and the two callers keep their own markers.
    /// </summary>
    static class UniLfsIgnoreBlock
    {
        /// <summary>
        /// Replaces the block between the markers, or appends one when the file
        /// has none. The existing line ending style is kept, because a file
        /// that flips between LF and CRLF shows up as a whole-file diff.
        /// </summary>
        public static void Write(string filePath, string beginMarker, string endMarker, List<string> blockLines)
        {
            string eol = "\n";
            var existing = new List<string>();
            if (File.Exists(filePath))
            {
                var text = File.ReadAllText(filePath, Encoding.UTF8);
                if (text.Contains("\r\n")) eol = "\r\n";
                existing.AddRange(text.Replace("\r\n", "\n").Split('\n'));
                if (existing.Count > 0 && existing[existing.Count - 1] == "")
                    existing.RemoveAt(existing.Count - 1);
            }

            int begin = existing.FindIndex(l => l.Trim() == beginMarker);
            int end = begin >= 0 ? existing.FindIndex(begin, l => l.Trim() == endMarker) : -1;

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

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, string.Join(eol, result) + eol, new UTF8Encoding(false));
        }

        public static List<string> ReadManagedLines(string filePath, string beginMarker, string endMarker)
        {
            var result = new List<string>();
            if (!File.Exists(filePath)) return result;
            var lines = File.ReadAllText(filePath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            bool inside = false;
            foreach (var l in lines)
            {
                if (l.Trim() == beginMarker) { inside = true; continue; }
                if (l.Trim() == endMarker) inside = false;
                else if (inside) result.Add(l);
            }
            return result;
        }

        /// <summary>
        /// Escapes gitignore pattern characters and anchors the path to the
        /// ignore file's own directory. '#' and '!' are only special at line
        /// start, but backslash-escaping them anywhere is valid and keeps this
        /// simple.
        /// </summary>
        public static string Escape(string relativePath)
        {
            var sb = new StringBuilder(relativePath.Length + 8);
            foreach (var c in relativePath)
            {
                if (c == '\\' || c == '*' || c == '?' || c == '[' || c == ']' || c == '#' || c == '!')
                    sb.Append('\\');
                sb.Append(c);
            }
            return "/" + sb;
        }
    }
}
