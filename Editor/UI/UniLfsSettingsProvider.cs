using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UniLFS.Editor
{
    class UniLfsSettingsProvider : SettingsProvider
    {
        UniLfsSettings _settings;
        UniLfsUserSettings _user;
        string _statusMessage = "";
        bool _working;
        string _newFolderName = "UniLFS";

        UniLfsSettingsProvider(string path, SettingsScope scope, System.Collections.Generic.IEnumerable<string> keywords)
            : base(path, scope, keywords)
        {
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new UniLfsSettingsProvider("Project/UniLFS", SettingsScope.Project,
                new[] { "UniLFS", "LFS", "R2", "S3", "MinIO", "Google", "Drive", "storage", "large", "assets" });
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            _settings = UniLfsSettings.Load();
            _user = UniLfsUserSettings.Load();
            _statusMessage = "";
        }

        public override void OnGUI(string searchContext)
        {
            if (_settings == null || _user == null) OnActivate(null, null);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("UniLFS stores tracked large files in your own external storage instead of Git LFS.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            int providerIndex = _settings.ProviderKind == UniLfsProviderKind.GoogleDrive ? 1 : 0;
            providerIndex = EditorGUILayout.Popup("Provider", providerIndex,
                new[] { "S3 compatible (Cloudflare R2 / S3 / MinIO)", "Google Drive" });
            _settings.provider = providerIndex == 1 ? UniLfsSettings.ProviderGoogleDrive : UniLfsSettings.ProviderS3;
            _settings.parallelTransfers = EditorGUILayout.IntSlider("Parallel Transfers", _settings.parallelTransfers, 1, 16);
            int autoPullIndex = (int)_settings.AutoPullMode;
            autoPullIndex = EditorGUILayout.Popup(
                new GUIContent("Auto Pull", "What to do when the editor regains focus, the manifest changed (e.g. after a git pull) and tracked files are missing"),
                autoPullIndex,
                new[] { "Off (warn in Console)", "Ask (dialog)", "Automatic" });
            _settings.autoPull = autoPullIndex == 0 ? UniLfsSettings.AutoPullOff
                : autoPullIndex == 2 ? UniLfsSettings.AutoPullAuto
                : UniLfsSettings.AutoPullAsk;
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            EditorGUILayout.Space();
            if (providerIndex == 0) DrawS3(); else DrawGoogleDrive();

            EditorGUILayout.Space(16);
            using (new EditorGUI.DisabledScope(_working))
            {
                if (GUILayout.Button("Test Connection", GUILayout.Width(140)))
                    TestConnection();
            }
            if (!string.IsNullOrEmpty(_statusMessage))
                EditorGUILayout.HelpBox(_statusMessage, MessageType.None);
        }

        void DrawS3()
        {
            EditorGUILayout.LabelField("S3 compatible storage", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Cloudflare R2 endpoint: https://<account-id>.r2.cloudflarestorage.com (region \"auto\").\n"
                + "Endpoint / bucket / prefix are project settings (committed to git). Credentials are stored per-user in UserSettings/UniLFS.json, which UniLFS keeps gitignored.\n"
                + "CI: set " + UniLfsCredentials.EnvS3AccessKeyId + " and " + UniLfsCredentials.EnvS3SecretAccessKey + ".",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _settings.s3Endpoint = EditorGUILayout.DelayedTextField("Endpoint", _settings.s3Endpoint);
            _settings.s3Bucket = EditorGUILayout.DelayedTextField("Bucket", _settings.s3Bucket);
            _settings.s3Region = EditorGUILayout.DelayedTextField(new GUIContent("Region", "\"auto\" for R2, e.g. \"us-east-1\" for AWS"), _settings.s3Region);
            _settings.s3Prefix = EditorGUILayout.DelayedTextField(new GUIContent("Key Prefix", "Objects are stored under <prefix>/objects/..."), _settings.s3Prefix);
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Credentials (per-user, not committed)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _user.s3AccessKeyId = EditorGUILayout.TextField("Access Key ID", _user.s3AccessKeyId);
            _user.s3SecretAccessKey = EditorGUILayout.PasswordField("Secret Access Key", _user.s3SecretAccessKey);
            if (EditorGUI.EndChangeCheck()) _user.Save();
        }

        void DrawGoogleDrive()
        {
            EditorGUILayout.LabelField("Google Drive", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Requires a Google Cloud OAuth client (type: Desktop app) with the Drive API enabled - see Documentation~/setup-google-drive.md.\n"
                + "Client ID/secret below are project settings shared with the team. If your git repo is public, leave them empty here and use the per-user fields instead.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _settings.driveClientId = EditorGUILayout.DelayedTextField("Client ID (project)", _settings.driveClientId);
            _settings.driveClientSecret = EditorGUILayout.PasswordField("Client Secret (project)", _settings.driveClientSecret);
            _settings.driveFolderId = EditorGUILayout.DelayedTextField(new GUIContent("Folder ID", "The ID from the Drive folder URL: drive.google.com/drive/folders/<ID>"), _settings.driveFolderId);
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Per-user overrides (not committed)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _user.driveClientId = EditorGUILayout.DelayedTextField("Client ID (per-user)", _user.driveClientId);
            _user.driveClientSecret = EditorGUILayout.PasswordField("Client Secret (per-user)", _user.driveClientSecret);
            if (EditorGUI.EndChangeCheck()) _user.Save();

            EditorGUILayout.Space();
            bool signedIn = !string.IsNullOrEmpty(_user.driveRefreshToken);
            EditorGUILayout.LabelField("Account: " + (signedIn ? "signed in" : "not signed in"), EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_working))
            {
                if (GUILayout.Button(signedIn ? "Sign in again" : "Sign in with Google", GUILayout.Width(150)))
                    SignIn();
                using (new EditorGUI.DisabledScope(!signedIn))
                {
                    if (GUILayout.Button("Sign out", GUILayout.Width(90)))
                    {
                        _user.driveRefreshToken = "";
                        _user.Save();
                        _statusMessage = "Signed out. (The token was only removed locally.)";
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Create a storage folder in My Drive", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            _newFolderName = EditorGUILayout.TextField(_newFolderName);
            using (new EditorGUI.DisabledScope(_working || !signedIn))
            {
                if (GUILayout.Button("Create Folder", GUILayout.Width(110)))
                    CreateFolder();
            }
            EditorGUILayout.EndHorizontal();
        }

        async void SignIn()
        {
            _working = true;
            _statusMessage = "A browser window opened - finish the Google consent screen there...";
            RepaintSettings();
            try
            {
                string clientId = UniLfsCredentials.DriveClientId(_settings, _user);
                string clientSecret = UniLfsCredentials.DriveClientSecret(_settings, _user);
                var tokens = await GoogleOAuth.SignInAsync(clientId, clientSecret, CancellationToken.None);
                _user.driveRefreshToken = tokens.RefreshToken;
                _user.Save();
                _statusMessage = "Signed in to Google Drive.";
            }
            catch (Exception e)
            {
                _statusMessage = "Sign-in failed: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                _working = false;
                RepaintSettings();
            }
        }

        async void CreateFolder()
        {
            _working = true;
            _statusMessage = "Creating folder...";
            RepaintSettings();
            try
            {
                string clientId = UniLfsCredentials.DriveClientId(_settings, _user);
                string clientSecret = UniLfsCredentials.DriveClientSecret(_settings, _user);
                string refreshToken = UniLfsCredentials.DriveRefreshToken(_user);
                string id = await GoogleDriveProvider.CreateFolderAsync(clientId, clientSecret, refreshToken, _newFolderName, CancellationToken.None);
                _settings.driveFolderId = id;
                _settings.Save();
                _statusMessage = "Created folder '" + _newFolderName + "' (ID: " + id + ") and saved it to the settings. Share this folder with your teammates.";
            }
            catch (Exception e)
            {
                _statusMessage = "Folder creation failed: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                _working = false;
                RepaintSettings();
            }
        }

        async void TestConnection()
        {
            _working = true;
            _statusMessage = "Testing connection...";
            RepaintSettings();
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                using (var provider = UniLfsCore.CreateProvider(_settings, _user))
                {
                    _statusMessage = await provider.TestConnectionAsync(cts.Token);
                }
            }
            catch (Exception e)
            {
                _statusMessage = "Connection failed: " + e.Message;
            }
            finally
            {
                _working = false;
                RepaintSettings();
            }
        }

        static void RepaintSettings()
        {
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
    }
}
