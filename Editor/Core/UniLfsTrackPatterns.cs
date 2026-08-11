using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// The patterns in <c>unilfs.track</c>: which files this project considers
    /// large enough to live in external storage, written once and committed, so
    /// the answer stops being something each person has to remember per file.
    ///
    /// It decides nothing on its own. Everything it matches is handed to the
    /// ordinary Track path, which stages it — the file is a statement of
    /// intent, exactly like the staging file, and only Push turns any of it
    /// into a manifest entry.
    ///
    /// The syntax is a subset of gitignore's, because that is the one glob
    /// dialect everyone using git already reads:
    ///
    ///   *.psd             no slash: matched against the file name, at any depth
    ///   Assets/Movies/    trailing slash: everything under that folder
    ///   Assets/**/*.wav   ** crosses folders, * stays inside one name, ? is one character
    ///   !Assets/UI/*.psd  ! excludes, and the last matching line wins
    ///
    /// Matching ignores case, because the filesystems Unity runs on by default
    /// (NTFS, APFS) do, and a rule that misses <c>.PSD</c> would look broken
    /// rather than precise.
    /// </summary>
    public sealed class UniLfsTrackPatterns
    {
        /// <summary>
        /// Written when the file is created, so the first thing anyone opening
        /// it sees is the syntax. No active pattern: creating the file must
        /// not start tracking anything by itself.
        /// </summary>
        public const string Template =
            "# UniLFS: files matching a line below are stored in external storage\n" +
            "# instead of git. Commit this file - it is how the team agrees on what\n" +
            "# counts as a large asset. Editing it tracks nothing on its own; use\n" +
            "# Window > UniLFS > Track Matching (or let Auto Track catch new imports).\n" +
            "#\n" +
            "#   *.psd             no slash: matched against the file name, at any depth\n" +
            "#   Assets/Movies/    trailing slash: everything under that folder\n" +
            "#   Assets/**/*.wav   ** crosses folders, * stays inside one name\n" +
            "#   !Assets/UI/*.psd  ! excludes, and the last matching line wins\n" +
            "#\n" +
            "# Case is ignored. .meta files are never tracked.\n";

        sealed class Rule
        {
            public Regex Regex;
            public bool Negate;
            /// <summary>Match the file name rather than the whole path.</summary>
            public bool NameOnly;
        }

        readonly List<Rule> _rules = new List<Rule>();

        /// <summary>
        /// Lines that could not be read as a pattern, as "line N: text". They
        /// are reported rather than guessed at: a typo that silently matched
        /// nothing would look exactly like a working rule.
        /// </summary>
        public List<string> Errors = new List<string>();

        public int Count { get { return _rules.Count; } }
        public bool IsEmpty { get { return _rules.Count == 0; } }

        /// <summary>
        /// Reads the file, or returns an empty set when it does not exist —
        /// having no patterns is the normal state of a project that never opted
        /// in, not an error.
        /// </summary>
        public static UniLfsTrackPatterns Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new UniLfsTrackPatterns();
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not read " + path + " (" + e.Message + "); no patterns are in effect.");
                return new UniLfsTrackPatterns();
            }
        }

        public static UniLfsTrackPatterns Parse(string text)
        {
            var patterns = new UniLfsTrackPatterns();
            if (string.IsNullOrEmpty(text)) return patterns;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                patterns.AddLine(lines[i], i + 1);
            return patterns;
        }

        void AddLine(string rawLine, int lineNumber)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') return;

            bool negate = line[0] == '!';
            if (negate) line = line.Substring(1).Trim();

            string glob = UniLfsPaths.Normalize(line);
            if (glob.Length == 0)
            {
                Errors.Add("line " + lineNumber + ": " + rawLine.Trim() + " (empty pattern)");
                return;
            }
            // A pattern is a claim about paths inside this project, and ".." is
            // the one thing that can walk out of it. Rejecting it here keeps
            // that impossible to express rather than merely unlikely to match.
            foreach (var segment in glob.Split('/'))
            {
                if (segment == "..")
                {
                    Errors.Add("line " + lineNumber + ": " + rawLine.Trim() + " ('..' cannot appear in a pattern)");
                    return;
                }
            }
            if (glob.StartsWith("/", StringComparison.Ordinal)) glob = glob.Substring(1);
            // A folder line means everything under it, which is what the rest
            // of the pattern language spells "**".
            if (glob.EndsWith("/", StringComparison.Ordinal)) glob += "**";
            if (glob.Length == 0)
            {
                Errors.Add("line " + lineNumber + ": " + rawLine.Trim() + " (empty pattern)");
                return;
            }

            bool nameOnly = glob.IndexOf('/') < 0;
            _rules.Add(new Rule
            {
                Regex = new Regex(GlobToRegex(glob), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                Negate = negate,
                NameOnly = nameOnly,
            });
        }

        /// <summary>
        /// Whether this project wants the path tracked. The last matching line
        /// decides, so an exclusion can be written after the rule it carves an
        /// exception out of.
        /// </summary>
        public bool Matches(string projectRelative)
        {
            if (_rules.Count == 0 || string.IsNullOrEmpty(projectRelative)) return false;
            string path = UniLfsPaths.Normalize(projectRelative);
            // The same gate Track itself applies, checked before any pattern
            // gets a say: no line anyone can write should be able to reach a
            // .meta file, the manifest, or Library/.
            string reason;
            if (!UniLfsPaths.IsTrackablePath(path, out reason)) return false;

            string name = Path.GetFileName(path);
            bool matched = false;
            foreach (var rule in _rules)
            {
                if (!rule.Regex.IsMatch(rule.NameOnly ? name : path)) continue;
                matched = !rule.Negate;
            }
            return matched;
        }

        /// <summary>Writes the commented template when the file is not there yet.</summary>
        public static bool CreateIfMissing(string path)
        {
            if (File.Exists(path)) return false;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Template, new UTF8Encoding(false));
            return true;
        }

        static string GlobToRegex(string glob)
        {
            var sb = new StringBuilder(glob.Length * 3);
            sb.Append('^');
            int i = 0;
            while (i < glob.Length)
            {
                char c = glob[i];
                if (c == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i += 2;
                        if (i < glob.Length && glob[i] == '/')
                        {
                            // "**/" spans whole directory names, including none
                            // at all, so "**/x.png" also matches "x.png".
                            sb.Append("(?:[^/]+/)*");
                            i++;
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i++;
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                    i++;
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                }
            }
            sb.Append('$');
            return sb.ToString();
        }
    }
}
