using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UniLFS.Editor
{
    /// <summary>
    /// Minimal AWS Signature Version 4 signer, sufficient for object
    /// GET/PUT/HEAD against S3-compatible services (Cloudflare R2, Amazon S3,
    /// MinIO, Wasabi, ...). No external dependencies.
    /// </summary>
    public static class S3SigV4
    {
        public const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        public sealed class SigningResult
        {
            public string AmzDate;
            public string CanonicalRequest;
            public string StringToSign;
            public string Signature;
            public string AuthorizationHeader;
        }

        /// <summary>AWS-style RFC 3986 percent-encoding (uppercase hex, '~' untouched).</summary>
        public static string UriEncode(string value, bool encodeSlash)
        {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(value))
            {
                char c = (char)b;
                bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '.' || c == '_' || c == '~';
                if (unreserved || (c == '/' && !encodeSlash))
                    sb.Append(c);
                else
                    sb.Append('%').Append(((int)b).ToString("X2"));
            }
            return sb.ToString();
        }

        public static string EncodePath(IEnumerable<string> segments)
        {
            return "/" + string.Join("/", segments.Select(s => UriEncode(s, true)));
        }

        public static SigningResult Sign(
            string httpMethod,
            string canonicalUri,
            string canonicalQueryString,
            IEnumerable<KeyValuePair<string, string>> headers,
            string payloadSha256,
            string accessKeyId,
            string secretAccessKey,
            string region,
            DateTimeOffset utcNow,
            string service = "s3")
        {
            string amzDate = utcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
            string dateStamp = utcNow.UtcDateTime.ToString("yyyyMMdd");

            var canonical = headers
                .Select(h => new KeyValuePair<string, string>(h.Key.ToLowerInvariant(), NormalizeHeaderValue(h.Value)))
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .ToList();
            string canonicalHeaders = string.Concat(canonical.Select(h => h.Key + ":" + h.Value + "\n"));
            string signedHeaders = string.Join(";", canonical.Select(h => h.Key));

            string canonicalRequest = string.Join("\n", new[]
            {
                httpMethod,
                canonicalUri,
                canonicalQueryString ?? "",
                canonicalHeaders,
                signedHeaders,
                payloadSha256,
            });

            string scope = dateStamp + "/" + region + "/" + service + "/aws4_request";
            string stringToSign = string.Join("\n", new[]
            {
                "AWS4-HMAC-SHA256",
                amzDate,
                scope,
                UniLfsHasher.Sha256OfString(canonicalRequest),
            });

            byte[] signingKey = SigningKey(secretAccessKey, dateStamp, region, service);
            string signature = UniLfsHasher.ToHex(Hmac(signingKey, stringToSign));

            return new SigningResult
            {
                AmzDate = amzDate,
                CanonicalRequest = canonicalRequest,
                StringToSign = stringToSign,
                Signature = signature,
                AuthorizationHeader = "AWS4-HMAC-SHA256 Credential=" + accessKeyId + "/" + scope
                    + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature,
            };
        }

        static string NormalizeHeaderValue(string value)
        {
            var sb = new StringBuilder();
            bool lastWasSpace = false;
            foreach (var c in (value ?? "").Trim())
            {
                if (c == ' ')
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            return sb.ToString();
        }

        static byte[] Hmac(byte[] key, string data)
        {
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        static byte[] SigningKey(string secretAccessKey, string dateStamp, string region, string service)
        {
            byte[] kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
            byte[] kRegion = Hmac(kDate, region);
            byte[] kService = Hmac(kRegion, service);
            return Hmac(kService, "aws4_request");
        }
    }
}
