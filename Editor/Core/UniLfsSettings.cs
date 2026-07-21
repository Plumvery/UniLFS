using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace UniLFS.Editor
{
    public enum UniLfsProviderKind
    {
        S3Compatible,
        GoogleDrive,
    }

    public enum UniLfsAutoPullMode
    {
        /// <summary>Only log a Console warning when tracked files are missing.</summary>
        Off,
        /// <summary>Show a dialog offering to pull (default).</summary>
        Ask,
        /// <summary>Pull missing files immediately without asking.</summary>
        Auto,
    }

    /// <summary>
    /// Project-wide configuration, stored at ProjectSettings/UniLFSSettings.json
    /// and meant to be committed to git. Never put secrets here — credentials
    /// live in <see cref="UniLfsUserSettings"/> or environment variables.
    /// (The Google OAuth client secret of a "Desktop app" client is not treated
    /// as confidential by Google, so it may go here for private repos; see docs.)
    /// </summary>
    [Serializable]
    public class UniLfsSettings
    {
        public const string ProviderS3 = "s3";
        public const string ProviderGoogleDrive = "googledrive";

        public const string AutoPullOff = "off";
        public const string AutoPullAsk = "ask";
        public const string AutoPullAuto = "auto";

        public int version = 1;
        public string provider = ProviderS3;
        public string autoPull = AutoPullAsk;

        public string s3Endpoint = "";
        public string s3Bucket = "";
        public string s3Region = "auto";
        public string s3Prefix = "unilfs";

        public string driveFolderId = "";
        public string driveClientId = "";
        public string driveClientSecret = "";

        public int parallelTransfers = 4;

        public UniLfsProviderKind ProviderKind
        {
            get { return provider == ProviderGoogleDrive ? UniLfsProviderKind.GoogleDrive : UniLfsProviderKind.S3Compatible; }
        }

        public UniLfsAutoPullMode AutoPullMode
        {
            get
            {
                if (autoPull == AutoPullOff) return UniLfsAutoPullMode.Off;
                if (autoPull == AutoPullAuto) return UniLfsAutoPullMode.Auto;
                return UniLfsAutoPullMode.Ask;
            }
        }

        public static UniLfsSettings Load()
        {
            return LoadFrom(UniLfsPaths.ProjectSettingsFilePath);
        }

        public void Save()
        {
            SaveTo(UniLfsPaths.ProjectSettingsFilePath);
        }

        internal static UniLfsSettings LoadFrom(string path)
        {
            var settings = new UniLfsSettings();
            try
            {
                if (File.Exists(path))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(path, Encoding.UTF8), settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not read " + path + " (" + e.Message + "); using defaults.");
            }
            return settings;
        }

        internal void SaveTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, true) + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// Per-user, per-machine credentials, stored at UserSettings/UniLFS.json.
    /// The file is kept out of git by the UniLFS managed .gitignore block.
    /// </summary>
    [Serializable]
    public class UniLfsUserSettings
    {
        public int version = 1;

        public string s3AccessKeyId = "";
        public string s3SecretAccessKey = "";

        public string driveClientId = "";
        public string driveClientSecret = "";
        public string driveRefreshToken = "";

        public static UniLfsUserSettings Load()
        {
            var settings = new UniLfsUserSettings();
            try
            {
                var path = UniLfsPaths.UserSettingsFilePath;
                if (File.Exists(path))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(path, Encoding.UTF8), settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: could not read user settings (" + e.Message + "); using defaults.");
            }
            return settings;
        }

        public void Save()
        {
            var path = UniLfsPaths.UserSettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, true) + "\n", new UTF8Encoding(false));
            // Make sure the credential file is ignored even before the first Track.
            var manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            var paths = new System.Collections.Generic.List<string>();
            foreach (var f in manifest.files) paths.Add(f.path);
            UniLfsGitIgnore.Update(UniLfsPaths.GitIgnorePath, paths);
        }
    }

    /// <summary>
    /// Resolves effective credentials with priority: environment variable
    /// (for CI) > per-user settings > project settings (where applicable).
    /// </summary>
    public static class UniLfsCredentials
    {
        public const string EnvS3AccessKeyId = "UNILFS_S3_ACCESS_KEY_ID";
        public const string EnvS3SecretAccessKey = "UNILFS_S3_SECRET_ACCESS_KEY";
        public const string EnvDriveClientId = "UNILFS_DRIVE_CLIENT_ID";
        public const string EnvDriveClientSecret = "UNILFS_DRIVE_CLIENT_SECRET";
        public const string EnvDriveRefreshToken = "UNILFS_DRIVE_REFRESH_TOKEN";

        public static string S3AccessKeyId(UniLfsUserSettings user)
        {
            return FirstNonEmpty(Env(EnvS3AccessKeyId), user.s3AccessKeyId);
        }

        public static string S3SecretAccessKey(UniLfsUserSettings user)
        {
            return FirstNonEmpty(Env(EnvS3SecretAccessKey), user.s3SecretAccessKey);
        }

        public static string DriveClientId(UniLfsSettings settings, UniLfsUserSettings user)
        {
            return FirstNonEmpty(Env(EnvDriveClientId), user.driveClientId, settings.driveClientId);
        }

        public static string DriveClientSecret(UniLfsSettings settings, UniLfsUserSettings user)
        {
            return FirstNonEmpty(Env(EnvDriveClientSecret), user.driveClientSecret, settings.driveClientSecret);
        }

        public static string DriveRefreshToken(UniLfsUserSettings user)
        {
            return FirstNonEmpty(Env(EnvDriveRefreshToken), user.driveRefreshToken);
        }

        static string Env(string name)
        {
            return Environment.GetEnvironmentVariable(name);
        }

        static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v))
                    return v;
            return "";
        }
    }
}
