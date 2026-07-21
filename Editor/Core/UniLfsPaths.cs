using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Central place for every filesystem location UniLFS touches.
    /// ProjectRoot is cached on the main thread at load time so all other
    /// members are safe to call from worker threads.
    /// </summary>
    public static class UniLfsPaths
    {
        static string _projectRoot;

        [InitializeOnLoadMethod]
        static void Init()
        {
            if (string.IsNullOrEmpty(_projectRoot))
                _projectRoot = Normalize(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
        }

        public static string ProjectRoot
        {
            get
            {
                Init();
                return _projectRoot;
            }
        }

        public const string ManifestFileName = "unilfs.manifest.json";

        public static string ManifestPath => Combine(ProjectRoot, ManifestFileName);
        public static string GitIgnorePath => Combine(ProjectRoot, ".gitignore");
        public static string ProjectSettingsFilePath => Combine(ProjectRoot, "ProjectSettings/UniLFSSettings.json");
        public static string UserSettingsFilePath => Combine(ProjectRoot, "UserSettings/UniLFS.json");
        public const string UserSettingsGitIgnoreLine = "/UserSettings/UniLFS.json";
        public static string LibraryDir => Combine(ProjectRoot, "Library/UniLFS");
        public static string StateCachePath => Combine(LibraryDir, "statecache.json");
        public static string TempDownloadDir => Combine(LibraryDir, "tmp");

        public static string Normalize(string path)
        {
            return path == null ? null : path.Replace('\\', '/');
        }

        public static string Combine(string a, string b)
        {
            return Normalize(Path.Combine(a, b));
        }

        public static string ToAbsolute(string projectRelative)
        {
            return Combine(ProjectRoot, projectRelative);
        }

        /// <summary>
        /// Converts an absolute (or already relative) path to a normalized
        /// project-relative path, or null when the path is outside the project.
        /// </summary>
        public static string ToProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full;
            try
            {
                full = Normalize(Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(ProjectRoot, path)));
            }
            catch (Exception)
            {
                return null;
            }
            string root = ProjectRoot;
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return "";
            if (!full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return full.Substring(root.Length + 1);
        }

        static readonly string[] ForbiddenTopLevel = { "Library", "Temp", "Logs", "obj", "UserSettings", ".git" };

        public static bool IsTrackablePath(string projectRelative, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(projectRelative))
            {
                reason = "the path is outside the project";
                return false;
            }
            if (projectRelative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                reason = ".meta files must stay in git";
                return false;
            }
            if (string.Equals(projectRelative, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                reason = "the UniLFS manifest itself cannot be tracked";
                return false;
            }
            foreach (var top in ForbiddenTopLevel)
            {
                if (projectRelative.Equals(top, StringComparison.OrdinalIgnoreCase) ||
                    projectRelative.StartsWith(top + "/", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "files under " + top + "/ cannot be tracked";
                    return false;
                }
            }
            return true;
        }
    }
}
