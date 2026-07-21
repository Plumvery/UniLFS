using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniLFS.Editor
{
    [Serializable]
    class DriveFile
    {
        public string id;
        public string name;
        public string mimeType;
        public string size;
    }

    [Serializable]
    class DriveFileList
    {
        public DriveFile[] files;
    }

    [Serializable]
    class DriveErrorBody
    {
        public int code;
        public string message;
    }

    [Serializable]
    class DriveErrorEnvelope
    {
        public DriveErrorBody error;
    }

    /// <summary>
    /// Stores blobs as files named by their SHA-256 inside one Drive folder
    /// (My Drive or a shared drive). Uses resumable uploads and alt=media
    /// downloads with hash verification.
    /// </summary>
    public class GoogleDriveProvider : IUniLfsStorageProvider
    {
        const string ApiBase = "https://www.googleapis.com/drive/v3";
        const string UploadBase = "https://www.googleapis.com/upload/drive/v3";
        static readonly Regex FolderIdPattern = new Regex("^[A-Za-z0-9_-]+$");

        readonly string _clientId;
        readonly string _clientSecret;
        readonly string _refreshToken;
        readonly string _folderId;
        GoogleTokenSet _tokens;
        readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, string> _fileIdCache = new Dictionary<string, string>();

        public string DisplayName
        {
            get { return "Google Drive"; }
        }

        public GoogleDriveProvider(string clientId, string clientSecret, string refreshToken, string folderId)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new UniLfsConfigException("Google OAuth client ID / client secret are not set. Open Project Settings > UniLFS (see Documentation~/setup-google-drive.md).");
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new UniLfsConfigException("Not signed in to Google Drive. Open Project Settings > UniLFS and press 'Sign in with Google'. For CI, set " + UniLfsCredentials.EnvDriveRefreshToken + ".");
            if (string.IsNullOrWhiteSpace(folderId) || !FolderIdPattern.IsMatch(folderId.Trim()))
                throw new UniLfsConfigException("Google Drive folder ID is missing or invalid. Open Project Settings > UniLFS and paste the folder ID from the folder's URL.");
            _clientId = clientId.Trim();
            _clientSecret = clientSecret.Trim();
            _refreshToken = refreshToken.Trim();
            _folderId = folderId.Trim();
        }

        async Task<string> AccessTokenAsync(CancellationToken ct)
        {
            await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_tokens == null || DateTimeOffset.UtcNow >= _tokens.ExpiresAtUtc)
                    _tokens = await GoogleOAuth.RefreshAsync(_clientId, _clientSecret, _refreshToken, ct).ConfigureAwait(false);
                return _tokens.AccessToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        async Task<HttpRequestMessage> AuthorizedRequestAsync(HttpMethod method, string url, CancellationToken ct)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct).ConfigureAwait(false));
            return request;
        }

        async Task<string> FindFileIdAsync(string hash, CancellationToken ct)
        {
            lock (_fileIdCache)
            {
                string cached;
                if (_fileIdCache.TryGetValue(hash, out cached)) return cached;
            }
            string q = Uri.EscapeDataString("name='" + hash + "' and '" + _folderId + "' in parents and trashed=false");
            string url = ApiBase + "/files?q=" + q
                + "&fields=" + Uri.EscapeDataString("files(id,name,size)")
                + "&pageSize=1&supportsAllDrives=true&includeItemsFromAllDrives=true";
            using (var request = await AuthorizedRequestAsync(HttpMethod.Get, url, ct).ConfigureAwait(false))
            using (var response = await UniLfsHttp.Client.SendAsync(request, ct).ConfigureAwait(false))
            {
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw DriveError("files.list", (int)response.StatusCode, json);
                var list = JsonUtility.FromJson<DriveFileList>(json);
                string id = (list != null && list.files != null && list.files.Length > 0) ? list.files[0].id : null;
                if (id != null)
                    lock (_fileIdCache) _fileIdCache[hash] = id;
                return id;
            }
        }

        public async Task<bool> BlobExistsAsync(string hash, CancellationToken ct)
        {
            return await FindFileIdAsync(hash, ct).ConfigureAwait(false) != null;
        }

        public async Task UploadBlobAsync(string hash, string sourceAbsPath, IProgress<long> bytesTransferred, CancellationToken ct)
        {
            string metadata = "{\"name\": " + UniLfsJsonUtil.Quote(hash) + ", \"parents\": [" + UniLfsJsonUtil.Quote(_folderId) + "]}";
            string sessionUrl;
            using (var request = await AuthorizedRequestAsync(HttpMethod.Post, UploadBase + "/files?uploadType=resumable&supportsAllDrives=true", ct).ConfigureAwait(false))
            {
                request.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
                using (var response = await UniLfsHttp.Client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw DriveError("start upload", (int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    sessionUrl = response.Headers.Location != null ? response.Headers.Location.ToString() : null;
                    if (string.IsNullOrEmpty(sessionUrl))
                        throw new UniLfsStorageException("Google Drive did not return an upload session URL.");
                }
            }

            using (var fs = new FileStream(sourceAbsPath, FileMode.Open, FileAccess.Read, FileShare.Read, UniLfsHasher.BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var progressStream = new UniLfsReadProgressStream(fs, bytesTransferred))
            using (var request = new HttpRequestMessage(HttpMethod.Put, sessionUrl))
            {
                var content = new StreamContent(progressStream, UniLfsHasher.BufferSize);
                content.Headers.ContentLength = fs.Length;
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content = content;
                using (var response = await UniLfsHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw DriveError("upload", (int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }

        public async Task DownloadBlobAsync(string hash, string destAbsPath, IProgress<long> bytesTransferred, CancellationToken ct)
        {
            string id = await FindFileIdAsync(hash, ct).ConfigureAwait(false);
            if (id == null)
                throw new UniLfsStorageException("Blob " + hash.Substring(0, 8) + "... was not found in the Google Drive folder. The pusher may not have pushed yet, or the folder ID is wrong.");
            using (var request = await AuthorizedRequestAsync(HttpMethod.Get, ApiBase + "/files/" + id + "?alt=media&supportsAllDrives=true", ct).ConfigureAwait(false))
            using (var response = await UniLfsHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw DriveError("download", (int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                await UniLfsHttp.DownloadToFileVerifiedAsync(response, hash, destAbsPath, bytesTransferred, ct).ConfigureAwait(false);
            }
        }

        public async Task<string> TestConnectionAsync(CancellationToken ct)
        {
            string url = ApiBase + "/files/" + _folderId + "?fields=" + Uri.EscapeDataString("id,name,mimeType") + "&supportsAllDrives=true";
            using (var request = await AuthorizedRequestAsync(HttpMethod.Get, url, ct).ConfigureAwait(false))
            using (var response = await UniLfsHttp.Client.SendAsync(request, ct).ConfigureAwait(false))
            {
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw DriveError("get folder", (int)response.StatusCode, json);
                var file = JsonUtility.FromJson<DriveFile>(json);
                if (file == null || file.mimeType != "application/vnd.google-apps.folder")
                    throw new UniLfsStorageException("The configured Google Drive ID exists but is not a folder.");
                return "Connected to Google Drive folder '" + file.name + "'.";
            }
        }

        /// <summary>Creates a folder in the signed-in user's My Drive and returns its ID.</summary>
        public static async Task<string> CreateFolderAsync(string clientId, string clientSecret, string refreshToken, string name, CancellationToken ct)
        {
            var tokens = await GoogleOAuth.RefreshAsync(clientId, clientSecret, refreshToken, ct).ConfigureAwait(false);
            using (var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/files?supportsAllDrives=true"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                string body = "{\"name\": " + UniLfsJsonUtil.Quote(name) + ", \"mimeType\": \"application/vnd.google-apps.folder\"}";
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await UniLfsHttp.Client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw DriveError("create folder", (int)response.StatusCode, json);
                    var file = JsonUtility.FromJson<DriveFile>(json);
                    if (file == null || string.IsNullOrEmpty(file.id))
                        throw new UniLfsStorageException("Google Drive folder creation returned no ID.");
                    return file.id;
                }
            }
        }

        static UniLfsStorageException DriveError(string op, int status, string body)
        {
            string message = null;
            try
            {
                var envelope = JsonUtility.FromJson<DriveErrorEnvelope>(body);
                if (envelope != null && envelope.error != null && !string.IsNullOrEmpty(envelope.error.message))
                    message = envelope.error.message;
            }
            catch (Exception) { }
            if (message == null)
                message = body != null && body.Length > 500 ? body.Substring(0, 500) + "..." : body;

            string hint = "";
            if (status == 401) hint = " Sign in again in Project Settings > UniLFS.";
            else if (status == 403) hint = " Check that the Drive API is enabled for your Google Cloud project and that you have access to the folder (quota/rate limits also report 403).";
            else if (status == 404) hint = " Check the folder ID and that the signed-in account can access it.";

            return new UniLfsStorageException("Google Drive " + op + " failed (HTTP " + status + "): " + message + "." + hint);
        }

        public void Dispose()
        {
            _tokenLock.Dispose();
        }
    }
}
