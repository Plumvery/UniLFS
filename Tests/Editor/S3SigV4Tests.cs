using System;
using System.Collections.Generic;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class S3SigV4Tests
    {
        // The worked "GET Object" example from the AWS documentation:
        // https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html
        [Test]
        public void Sign_MatchesAwsDocumentationExample()
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Host", "examplebucket.s3.amazonaws.com"),
                new KeyValuePair<string, string>("Range", "bytes=0-9"),
                new KeyValuePair<string, string>("x-amz-content-sha256", S3SigV4.EmptyPayloadSha256),
                new KeyValuePair<string, string>("x-amz-date", "20130524T000000Z"),
            };

            var result = S3SigV4.Sign(
                "GET",
                "/test.txt",
                "",
                headers,
                S3SigV4.EmptyPayloadSha256,
                "AKIAIOSFODNN7EXAMPLE",
                "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                "us-east-1",
                new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));

            string expectedCanonicalRequest = string.Join("\n", new[]
            {
                "GET",
                "/test.txt",
                "",
                "host:examplebucket.s3.amazonaws.com",
                "range:bytes=0-9",
                "x-amz-content-sha256:" + S3SigV4.EmptyPayloadSha256,
                "x-amz-date:20130524T000000Z",
                "",
                "host;range;x-amz-content-sha256;x-amz-date",
                S3SigV4.EmptyPayloadSha256,
            });
            Assert.AreEqual(expectedCanonicalRequest, result.CanonicalRequest);
            Assert.AreEqual("20130524T000000Z", result.AmzDate);
            Assert.AreEqual("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", result.Signature);
            StringAssert.Contains("Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request", result.AuthorizationHeader);
            StringAssert.Contains("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", result.AuthorizationHeader);
        }

        [Test]
        public void UriEncode_EncodesReservedCharacters()
        {
            Assert.AreEqual("abcXYZ019-._~", S3SigV4.UriEncode("abcXYZ019-._~", true));
            Assert.AreEqual("a%20b", S3SigV4.UriEncode("a b", true));
            Assert.AreEqual("a%2Fb", S3SigV4.UriEncode("a/b", true));
            Assert.AreEqual("a/b", S3SigV4.UriEncode("a/b", false));
            Assert.AreEqual("%E3%81%82", S3SigV4.UriEncode("あ", true));
            Assert.AreEqual("%2B%3D%26", S3SigV4.UriEncode("+=&", true));
        }

        [Test]
        public void EncodePath_JoinsAndEncodesSegments()
        {
            Assert.AreEqual("/bucket/pre%20fix/objects/ab/abc",
                S3SigV4.EncodePath(new[] { "bucket", "pre fix", "objects", "ab", "abc" }));
        }

        [Test]
        public void Sign_LowercasesAndSortsHeaders()
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("X-Amz-Date", "20130524T000000Z"),
                new KeyValuePair<string, string>("HOST", "example.com"),
                new KeyValuePair<string, string>("x-amz-content-sha256", "abc"),
            };
            var result = S3SigV4.Sign("PUT", "/x", "", headers, "abc", "AK", "SK", "auto",
                new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));
            StringAssert.Contains("host:example.com\nx-amz-content-sha256:abc\nx-amz-date:20130524T000000Z\n", result.CanonicalRequest);
            StringAssert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", result.AuthorizationHeader);
        }

        [Test]
        public void Sign_NormalizesHeaderWhitespace()
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("host", "  example.com  "),
                new KeyValuePair<string, string>("x-amz-meta-note", "a   b"),
            };
            var result = S3SigV4.Sign("GET", "/", "", headers, S3SigV4.EmptyPayloadSha256, "AK", "SK", "auto",
                new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));
            StringAssert.Contains("host:example.com\n", result.CanonicalRequest);
            StringAssert.Contains("x-amz-meta-note:a b\n", result.CanonicalRequest);
        }
    }
}
