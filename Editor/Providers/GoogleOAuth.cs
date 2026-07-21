using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UniLFS.Editor
{
    [Serializable]
    class GoogleTokenResponse
    {
        public string access_token;
        public string refresh_token;
        public int expires_in;
        public string token_type;
        public string scope;
        public string error;
        public string error_description;
    }

    public class GoogleTokenSet
    {
        public string AccessToken;
        public string RefreshToken;
        public DateTimeOffset ExpiresAtUtc;
    }

    /// <summary>
    /// OAuth 2.0 "installed app" loopback flow with PKCE for Google Drive.
    /// Opens the system browser and receives the redirect on 127.0.0.1.
    /// </summary>
    public static class GoogleOAuth
    {
        public const string Scope = "https://www.googleapis.com/auth/drive";
        const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        const string TokenEndpoint = "https://oauth2.googleapis.com/token";

        /// <summary>
        /// Must be called from the main thread (it opens the system browser).
        /// Completes when the user finishes the consent screen, times out after
        /// 5 minutes, or <paramref name="ct"/> is cancelled.
        /// </summary>
        public static async Task<GoogleTokenSet> SignInAsync(string clientId, string clientSecret, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new UniLfsConfigException("Google OAuth client ID / client secret are not set. See Documentation~/setup-google-drive.md.");

            string verifier = RandomUrlSafe(64);
            string challenge = Base64Url(Sha256(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomUrlSafe(32);

            HttpListener listener = null;
            string redirectUri = null;
            var rng = new System.Random();
            for (int attempt = 0; attempt < 5 && listener == null; attempt++)
            {
                int port = rng.Next(49500, 65000);
                var candidate = new HttpListener();
                candidate.Prefixes.Add("http://127.0.0.1:" + port + "/unilfs/");
                try
                {
                    candidate.Start();
                    listener = candidate;
                    redirectUri = "http://127.0.0.1:" + port + "/unilfs/";
                }
                catch (Exception)
                {
                    // port in use; try another
                }
            }
            if (listener == null)
                throw new UniLfsStorageException("Could not open a local port for the OAuth redirect. Check firewall settings.");

            try
            {
                string authUrl = AuthEndpoint
                    + "?response_type=code"
                    + "&client_id=" + Uri.EscapeDataString(clientId.Trim())
                    + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                    + "&scope=" + Uri.EscapeDataString(Scope)
                    + "&state=" + Uri.EscapeDataString(state)
                    + "&code_challenge=" + challenge
                    + "&code_challenge_method=S256"
                    + "&access_type=offline"
                    + "&prompt=consent";
                Application.OpenURL(authUrl);

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    HttpListenerContext context;
                    using (timeout.Token.Register(() => { try { listener.Stop(); } catch (Exception) { } }))
                    {
                        try
                        {
                            context = await listener.GetContextAsync().ConfigureAwait(false);
                        }
                        catch (Exception) when (timeout.IsCancellationRequested)
                        {
                            throw new OperationCanceledException("Google sign-in was cancelled or timed out after 5 minutes.");
                        }
                    }

                    var query = context.Request.QueryString;
                    string code = query["code"];
                    string returnedState = query["state"];
                    string error = query["error"];
                    bool ok = error == null && !string.IsNullOrEmpty(code) && returnedState == state;
                    await WriteHtmlAsync(context.Response, ok).ConfigureAwait(false);
                    if (!ok)
                        throw new UniLfsStorageException("Google sign-in failed: " + (error ?? "unexpected response") + ".");

                    string body = "code=" + Uri.EscapeDataString(code)
                        + "&client_id=" + Uri.EscapeDataString(clientId.Trim())
                        + "&client_secret=" + Uri.EscapeDataString(clientSecret.Trim())
                        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                        + "&code_verifier=" + verifier
                        + "&grant_type=authorization_code";
                    return await ExchangeAsync(body, null, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                try { listener.Close(); } catch (Exception) { }
            }
        }

        public static Task<GoogleTokenSet> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
        {
            string body = "client_id=" + Uri.EscapeDataString(clientId)
                + "&client_secret=" + Uri.EscapeDataString(clientSecret)
                + "&refresh_token=" + Uri.EscapeDataString(refreshToken)
                + "&grant_type=refresh_token";
            return ExchangeAsync(body, refreshToken, ct);
        }

        static async Task<GoogleTokenSet> ExchangeAsync(string formBody, string fallbackRefreshToken, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint))
            {
                request.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
                using (var response = await UniLfsHttp.Client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    GoogleTokenResponse parsed = null;
                    try { parsed = JsonUtility.FromJson<GoogleTokenResponse>(json); } catch (Exception) { }
                    if (!response.IsSuccessStatusCode || parsed == null || string.IsNullOrEmpty(parsed.access_token))
                    {
                        string detail = parsed != null ? (parsed.error + " " + parsed.error_description).Trim() : json;
                        string hint = parsed != null && parsed.error == "invalid_grant"
                            ? " The refresh token may be expired or revoked - sign in again in Project Settings > UniLFS."
                            + " (Note: while a Google Cloud OAuth consent screen is in 'Testing' status, refresh tokens expire after 7 days.)"
                            : "";
                        throw new UniLfsStorageException("Google token request failed (HTTP " + (int)response.StatusCode + "): " + detail + "." + hint);
                    }
                    return new GoogleTokenSet
                    {
                        AccessToken = parsed.access_token,
                        RefreshToken = string.IsNullOrEmpty(parsed.refresh_token) ? fallbackRefreshToken : parsed.refresh_token,
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(120, parsed.expires_in) - 60),
                    };
                }
            }
        }

        static async Task WriteHtmlAsync(HttpListenerResponse response, bool ok)
        {
            string html = ok
                ? "<html><body style='font-family:sans-serif;text-align:center;margin-top:4em'><h2>UniLFS: sign-in complete</h2><p>You can close this tab and return to Unity.</p></body></html>"
                : "<html><body style='font-family:sans-serif;text-align:center;margin-top:4em'><h2>UniLFS: sign-in failed</h2><p>Return to Unity and check the Console.</p></body></html>";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                response.OutputStream.Close();
            }
            catch (Exception)
            {
                // The browser closing early is not an error worth failing sign-in over.
            }
        }

        static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static string RandomUrlSafe(int byteCount)
        {
            var buffer = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buffer);
            return Base64Url(buffer);
        }
    }
}
