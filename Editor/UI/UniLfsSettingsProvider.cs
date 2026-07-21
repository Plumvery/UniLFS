using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UniLFS.Editor
{
    class UniLfsSettingsProvider : SettingsProvider
    {
        /// <summary>Wide enough for "Secret Access Key" and "Client Secret (project)" not to be clipped.</summary>
        const float LabelWidth = 190f;

        static GUIStyle _pageStyle;

        UniLfsSettings _settings;
        UniLfsUserSettings _user;
        string _statusMessage = "";
        MessageType _statusType = MessageType.Info;
        bool _working;
        string _newFolderName = "UniLFS";

        // Text fields write straight into the in-memory settings objects, but
        // persisting is deferred until the field loses focus. Saving per
        // keystroke rewrote the settings JSON — and, for user settings, the
        // whole managed .gitignore block — once per typed character.
        string _pendingControl;
        Action _pendingCommit;

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
            _statusType = MessageType.Info;
        }

        public override void OnDeactivate()
        {
            FlushPendingCommit();
        }

        static GUIStyle PageStyle
        {
            get
            {
                // Custom settings pages otherwise render flush against the pane
                // edge, unlike every built-in one.
                if (_pageStyle == null) _pageStyle = new GUIStyle { padding = new RectOffset(10, 10, 4, 10) };
                return _pageStyle;
            }
        }

        public override void OnGUI(string searchContext)
        {
            if (_settings == null || _user == null) OnActivate(null, null);

            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = LabelWidth;
            EditorGUILayout.BeginVertical(PageStyle);
            try
            {
                // GUILayout.Label, not LabelField: LabelField reserves a single
                // line, so a wrapped sentence loses everything after line one.
                GUILayout.Label(
                    "UniLFS stores tracked large files in your own external storage instead of Git LFS.",
                    EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(8);

                DrawStorageSelection();
                EditorGUILayout.Space(4);
                if (_settings.ProviderKind == UniLfsProviderKind.S3Compatible) DrawS3(); else DrawGoogleDrive();

                EditorGUILayout.Space(12);
                DrawConnectionTest();
            }
            finally
            {
                EditorGUILayout.EndVertical();
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

            FlushPendingCommitIfFocusLeft();
        }

        void DrawStorageSelection()
        {
            EditorGUILayout.LabelField("Storage", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            int providerIndex = _settings.ProviderKind == UniLfsProviderKind.GoogleDrive ? 1 : 0;
            providerIndex = EditorGUILayout.Popup(
                new GUIContent("Provider", "Where tracked files are uploaded to. Changing this does not move blobs already uploaded to the other provider."),
                providerIndex,
                new[] { "S3 compatible (R2 / S3 / MinIO)", "Google Drive" });
            _settings.provider = providerIndex == 1 ? UniLfsSettings.ProviderGoogleDrive : UniLfsSettings.ProviderS3;

            _settings.parallelTransfers = EditorGUILayout.IntSlider(
                new GUIContent("Parallel Transfers", "How many files upload or download at the same time"),
                _settings.parallelTransfers, 1, 16);

            int autoPullIndex = EditorGUILayout.Popup(
                new GUIContent("Auto Pull", "What to do when the editor regains focus, the manifest changed (e.g. after a git pull) and tracked files are missing"),
                (int)_settings.AutoPullMode,
                new[] { "Off (warn in Console)", "Ask (dialog)", "Automatic" });
            _settings.autoPull = autoPullIndex == 0 ? UniLfsSettings.AutoPullOff
                : autoPullIndex == 2 ? UniLfsSettings.AutoPullAuto
                : UniLfsSettings.AutoPullAsk;

            int autoPushIndex = EditorGUILayout.Popup(
                new GUIContent("Auto Push", "What to do when tracked files have local changes that are not uploaded yet. Automatic uploads right after the asset is saved/imported"),
                (int)_settings.AutoPushMode,
                new[] { "Off", "Ask (dialog)", "Automatic" });
            _settings.autoPush = autoPushIndex == 0 ? UniLfsSettings.AutoPushOff
                : autoPushIndex == 2 ? UniLfsSettings.AutoPushAuto
                : UniLfsSettings.AutoPushAsk;

            if (EditorGUI.EndChangeCheck()) _settings.Save();
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

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Credentials (per-user, not committed)", EditorStyles.boldLabel);
            _user.s3AccessKeyId = UserField("unilfs.s3.accessKeyId", new GUIContent("Access Key ID"), _user.s3AccessKeyId, false);
            _user.s3SecretAccessKey = UserField("unilfs.s3.secretAccessKey", new GUIContent("Secret Access Key"), _user.s3SecretAccessKey, true);

            WarnIfIncomplete();
        }

        void DrawGoogleDrive()
        {
            EditorGUILayout.LabelField("Google Drive", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Requires a Google Cloud OAuth client (type: Desktop app) with the Drive API enabled - see Documentation~/setup-google-drive.md.\n"
                + "Client ID/secret below are project settings shared with the team. If your git repo is public, leave them empty here and use the per-user fields instead.",
                MessageType.Info);

            _settings.driveClientId = ProjectField("unilfs.drive.clientId", new GUIContent("Client ID (project)"), _settings.driveClientId, false);
            _settings.driveClientSecret = ProjectField("unilfs.drive.clientSecret", new GUIContent("Client Secret (project)"), _settings.driveClientSecret, true);
            EditorGUI.BeginChangeCheck();
            _settings.driveFolderId = EditorGUILayout.DelayedTextField(
                new GUIContent("Folder ID", "The ID from the Drive folder URL: drive.google.com/drive/folders/<ID>"), _settings.driveFolderId);
            if (EditorGUI.EndChangeCheck()) _settings.Save();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Per-user overrides (not committed)", EditorStyles.boldLabel);
            _user.driveClientId = UserField("unilfs.drive.userClientId", new GUIContent("Client ID (per-user)"), _user.driveClientId, false);
            _user.driveClientSecret = UserField("unilfs.drive.userClientSecret", new GUIContent("Client Secret (per-user)"), _user.driveClientSecret, true);

            EditorGUILayout.Space(8);
            bool signedIn = !string.IsNullOrEmpty(_user.driveRefreshToken);
            EditorGUILayout.LabelField("Account", signedIn ? "Signed in" : "Not signed in");
            using (FieldColumn.Open())
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
                        SetStatus("Signed out. (The token was only removed locally.)", MessageType.Info);
                    }
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            _newFolderName = EditorGUILayout.TextField(
                new GUIContent("New Folder Name", "Creates a folder in My Drive and selects it as the storage folder"), _newFolderName);
            using (new EditorGUI.DisabledScope(_working || !signedIn))
            {
                if (GUILayout.Button("Create", GUILayout.Width(80)))
                    CreateFolder();
            }
            EditorGUILayout.EndHorizontal();

            WarnIfIncomplete();
        }

        void DrawConnectionTest()
        {
            using (new EditorGUI.DisabledScope(_working))
            {
                if (GUILayout.Button(_working ? "Testing..." : "Test Connection", GUILayout.Width(140)))
                    TestConnection();
            }
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        /// <summary>Lists what still has to be filled in before Push/Pull can work.</summary>
        void WarnIfIncomplete()
        {
            var missing = UniLfsProviderStatus.MissingRequirements(_settings, _user);
            if (missing.Count == 0) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("This provider still needs " + UniLfsProviderStatus.Describe(missing) + ".", MessageType.Warning);
        }

        string ProjectField(string controlName, GUIContent label, string value, bool password)
        {
            return DeferredField(controlName, label, value, password, () => _settings.Save());
        }

        string UserField(string controlName, GUIContent label, string value, bool password)
        {
            return DeferredField(controlName, label, value, password, () => _user.Save());
        }

        string DeferredField(string controlName, GUIContent label, string value, bool password, Action commit)
        {
            GUI.SetNextControlName(controlName);
            EditorGUI.BeginChangeCheck();
            // PasswordField has no delayed variant, so focus tracking (rather
            // than DelayedTextField) is what defers the write for both kinds.
            string edited = password
                ? EditorGUILayout.PasswordField(label, value)
                : EditorGUILayout.TextField(label, value);
            if (EditorGUI.EndChangeCheck()) SchedulePendingCommit(controlName, commit);
            return edited;
        }

        void SchedulePendingCommit(string controlName, Action commit)
        {
            if (_pendingCommit != null && _pendingControl != controlName) FlushPendingCommit();
            _pendingControl = controlName;
            _pendingCommit = commit;
        }

        void FlushPendingCommitIfFocusLeft()
        {
            if (_pendingCommit != null && GUI.GetNameOfFocusedControl() != _pendingControl) FlushPendingCommit();
        }

        void FlushPendingCommit()
        {
            if (_pendingCommit == null) return;
            var commit = _pendingCommit;
            _pendingCommit = null;
            _pendingControl = null;
            commit();
        }

        /// <summary>A horizontal row indented to line up with the field column.</summary>
        struct FieldColumn : IDisposable
        {
            public void Dispose()
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            public static FieldColumn Open()
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUIUtility.labelWidth);
                return new FieldColumn();
            }
        }

        void SetStatus(string message, MessageType type)
        {
            _statusMessage = message;
            _statusType = type;
        }

        async void SignIn()
        {
            FlushPendingCommit();
            _working = true;
            SetStatus("A browser window opened - finish the Google consent screen there...", MessageType.Info);
            RepaintSettings();
            try
            {
                string clientId = UniLfsCredentials.DriveClientId(_settings, _user);
                string clientSecret = UniLfsCredentials.DriveClientSecret(_settings, _user);
                var tokens = await GoogleOAuth.SignInAsync(clientId, clientSecret, CancellationToken.None);
                _user.driveRefreshToken = tokens.RefreshToken;
                _user.Save();
                SetStatus("Signed in to Google Drive.", MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus("Sign-in failed: " + e.Message, MessageType.Error);
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
            FlushPendingCommit();
            _working = true;
            SetStatus("Creating folder...", MessageType.Info);
            RepaintSettings();
            try
            {
                string clientId = UniLfsCredentials.DriveClientId(_settings, _user);
                string clientSecret = UniLfsCredentials.DriveClientSecret(_settings, _user);
                string refreshToken = UniLfsCredentials.DriveRefreshToken(_user);
                string id = await GoogleDriveProvider.CreateFolderAsync(clientId, clientSecret, refreshToken, _newFolderName, CancellationToken.None);
                _settings.driveFolderId = id;
                _settings.Save();
                SetStatus("Created folder '" + _newFolderName + "' (ID: " + id + ") and saved it to the settings. Share this folder with your teammates.", MessageType.Info);
            }
            catch (Exception e)
            {
                SetStatus("Folder creation failed: " + e.Message, MessageType.Error);
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
            FlushPendingCommit();
            _working = true;
            SetStatus("Testing connection...", MessageType.Info);
            RepaintSettings();
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                using (var provider = UniLfsCore.CreateProvider(_settings, _user))
                {
                    SetStatus(await provider.TestConnectionAsync(cts.Token), MessageType.Info);
                }
            }
            catch (Exception e)
            {
                SetStatus("Connection failed: " + e.Message, MessageType.Error);
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
