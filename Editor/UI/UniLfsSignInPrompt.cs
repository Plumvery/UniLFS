using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// Asks the user to finish provider setup once per editor session.
    ///
    /// The sign-in button lives in Project Settings, which is easy to miss: a
    /// teammate cloning a configured project would otherwise discover the
    /// problem only when their first Pull failed. The prompt is deliberately
    /// quiet — it stays silent unless the project actually tracks files, asks
    /// at most once per editor session, and can be muted per project.
    /// </summary>
    static class UniLfsSignInPrompt
    {
        /// <summary>Survives domain reloads but resets on editor restart, so this asks once per session.</summary>
        const string AskedThisSessionKey = "UniLFS.SignInPrompt.Asked";

        [InitializeOnLoadMethod]
        static void Init()
        {
            // A modal dialog in batch mode would hang CI forever.
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += Check;
        }

        static void Check()
        {
            if (SessionState.GetBool(AskedThisSessionKey, false)) return;
            SessionState.SetBool(AskedThisSessionKey, true);
            if (EditorPrefs.GetBool(MuteKey(), false)) return;

            // Nothing is tracked yet: the user has not adopted UniLFS in this
            // project, so there is nothing to sign in for.
            if (!File.Exists(UniLfsPaths.ManifestPath)) return;
            UniLfsManifest manifest;
            try
            {
                manifest = UniLfsManifest.Load(UniLfsPaths.ManifestPath);
            }
            catch (Exception)
            {
                return;
            }
            if (manifest.files.Count == 0) return;

            var settings = UniLfsSettings.Load();
            var user = UniLfsUserSettings.Load();
            var missing = UniLfsProviderStatus.MissingRequirements(settings, user);
            if (missing.Count == 0) return;

            bool signInOnly = UniLfsProviderStatus.NeedsGoogleSignInOnly(settings, user);
            string message = signInOnly
                ? "This project stores " + manifest.files.Count + " tracked file(s) in Google Drive, but this machine is not signed in yet.\n\n"
                  + "Push and Pull will fail until you sign in."
                : "This project tracks " + manifest.files.Count + " file(s) with UniLFS, but the storage provider is not ready.\n\n"
                  + "Still missing: " + UniLfsProviderStatus.Describe(missing) + ".";

            int choice = EditorUtility.DisplayDialogComplex(
                "UniLFS setup",
                message,
                signInOnly ? "Sign in with Google" : "Open Settings",
                "Later",
                "Don't ask again");

            switch (choice)
            {
                case 0:
                    if (signInOnly) SignInToGoogleDrive(settings, user);
                    else SettingsService.OpenProjectSettings("Project/UniLFS");
                    break;
                case 2:
                    EditorPrefs.SetBool(MuteKey(), true);
                    Debug.Log("UniLFS: setup reminders muted for this project. Re-enable them from the banner in Window > UniLFS.");
                    break;
            }
        }

        /// <summary>Lets the UniLFS window undo a "Don't ask again".</summary>
        internal static bool Muted
        {
            get { return EditorPrefs.GetBool(MuteKey(), false); }
            set { EditorPrefs.SetBool(MuteKey(), value); }
        }

        /// <summary>
        /// EditorPrefs is machine-wide, so the key carries a project fingerprint
        /// — muting one project must not silence another.
        /// </summary>
        static string MuteKey()
        {
            return "UniLFS.SignInPrompt.Muted." + UniLfsHasher.Sha256OfString(Application.dataPath).Substring(0, 16);
        }

        static async void SignInToGoogleDrive(UniLfsSettings settings, UniLfsUserSettings user)
        {
            try
            {
                var tokens = await GoogleOAuth.SignInAsync(
                    UniLfsCredentials.DriveClientId(settings, user),
                    UniLfsCredentials.DriveClientSecret(settings, user),
                    CancellationToken.None);
                user.driveRefreshToken = tokens.RefreshToken;
                user.Save();
                Debug.Log("UniLFS: signed in to Google Drive.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("UniLFS: Google Drive sign-in failed (" + e.Message + "). Retry from Project Settings > UniLFS.");
                SettingsService.OpenProjectSettings("Project/UniLFS");
            }
            finally
            {
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }
    }
}
