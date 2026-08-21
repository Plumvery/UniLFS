using System;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class GoogleOAuthTests
    {
        /// <summary>A fixed loopback URI; the real one picks a random free port.</summary>
        const string RedirectUri = "http://127.0.0.1:50123/unilfs/";

        /// <summary>Padded on purpose: a client ID pasted from the console often carries whitespace.</summary>
        const string PaddedClientId = "  abc.apps.googleusercontent.com  ";

        static string BuildUrl()
        {
            return GoogleOAuth.BuildAuthUrl(PaddedClientId, RedirectUri, "state-value", "challenge-value");
        }

        [Test]
        public void BuildAuthUrl_CarriesTheLoopbackFlowParameters()
        {
            string url = BuildUrl();
            StringAssert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?response_type=code", url);
            StringAssert.Contains("&redirect_uri=" + Uri.EscapeDataString(RedirectUri), url);
            StringAssert.Contains("&scope=" + Uri.EscapeDataString(GoogleOAuth.Scope), url);
            StringAssert.Contains("&state=state-value", url);
            StringAssert.Contains("&code_challenge=challenge-value", url);
            StringAssert.Contains("&code_challenge_method=S256", url);
            StringAssert.Contains("&access_type=offline", url);
        }

        [Test]
        public void BuildAuthUrl_TrimsTheClientId()
        {
            StringAssert.Contains("&client_id=abc.apps.googleusercontent.com&", BuildUrl());
        }

        /// <summary>
        /// The account chooser is the whole point of the parameter: with a plain
        /// "consent" prompt, a browser holding one Google session signs the user
        /// in without asking, on an account that may not see the Drive folder -
        /// after which every Pull fails with "not found".
        /// </summary>
        [Test]
        public void BuildAuthUrl_AsksForTheAccountChooser()
        {
            StringAssert.Contains("prompt=consent%20select_account", BuildUrl());
        }

        [Test]
        public void ParseAccountEmail_ReadsTheAddress()
        {
            Assert.AreEqual("a@b.example", GoogleOAuth.ParseAccountEmail("{\"user\":{\"emailAddress\":\"a@b.example\"}}"));
        }

        /// <summary>
        /// The lookup only feeds diagnostics, so anything unreadable has to come
        /// back as "unknown" rather than as an exception thrown while UniLFS was
        /// busy explaining a different failure.
        /// </summary>
        [Test]
        public void ParseAccountEmail_ReturnsNullForAnythingItCannotRead()
        {
            Assert.IsNull(GoogleOAuth.ParseAccountEmail(null));
            Assert.IsNull(GoogleOAuth.ParseAccountEmail(""));
            Assert.IsNull(GoogleOAuth.ParseAccountEmail("{}"));
            Assert.IsNull(GoogleOAuth.ParseAccountEmail("{\"user\":{}}"));
            Assert.IsNull(GoogleOAuth.ParseAccountEmail("not json"));
        }

        [Test]
        public void DescribeSignIn_NamesTheAccountWhenItIsKnown()
        {
            StringAssert.Contains("a@b.example", GoogleOAuth.DescribeSignIn("a@b.example"));
        }

        [Test]
        public void DescribeSignIn_SaysSoWhenTheAddressIsUnknown()
        {
            StringAssert.Contains("Could not read", GoogleOAuth.DescribeSignIn(""));
            StringAssert.Contains("Could not read", GoogleOAuth.DescribeSignIn(null));
        }
    }
}
