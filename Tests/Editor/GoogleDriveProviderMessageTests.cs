using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// These two strings are the whole of what a user sees when a Pull cannot
    /// find a blob or when Test Connection comes back empty, and both are
    /// assembled from fragments that appear only when the account's address is
    /// known - the shape that produces a stray double space, a doubled full
    /// stop, or a sentence naming nobody. Pinned here rather than left to a
    /// live Drive account nobody can reproduce.
    /// </summary>
    public class GoogleDriveProviderMessageTests
    {
        const string Hash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        const string Email = "a@b.example";

        /// <summary>
        /// The hash prefix is deliberately followed by "...", so that one
        /// ellipsis is dropped before looking for the doubled punctuation and
        /// spacing that a badly joined fragment would leave behind.
        /// </summary>
        static void AssertReadsCleanly(string message)
        {
            string prose = message.Replace("...", "");
            StringAssert.DoesNotContain("  ", prose);
            StringAssert.DoesNotContain("..", prose);
            StringAssert.DoesNotContain(" .", prose);
            StringAssert.DoesNotContain(" ,", prose);
        }

        [Test]
        public void NotFoundMessage_NamesTheAccountItSearchedAs()
        {
            string message = GoogleDriveProvider.NotFoundMessage(Hash, Email);
            StringAssert.Contains("abcdef12", message);
            StringAssert.Contains("(signed in as " + Email + ")", message);
            StringAssert.Contains("share the folder with " + Email, message);
            AssertReadsCleanly(message);
        }

        /// <summary>
        /// Without an address the message still has to be actionable, which is
        /// why "this account" carries directions to where the address is shown.
        /// </summary>
        [Test]
        public void NotFoundMessage_StaysActionableWithoutAnAddress()
        {
            string message = GoogleDriveProvider.NotFoundMessage(Hash, null);
            StringAssert.Contains("abcdef12", message);
            StringAssert.DoesNotContain("signed in as", message);
            StringAssert.Contains("this account", message);
            StringAssert.Contains("Project Settings > UniLFS", message);
            AssertReadsCleanly(message);
        }

        /// <summary>
        /// The everyday success stays exactly as terse as 0.5.0's was: the new
        /// diagnostics are for the cases that go wrong.
        /// </summary>
        [Test]
        public void ConnectedMessage_IsUnchangedForAFolderWithFilesAndNoAddress()
        {
            Assert.AreEqual("Connected to Google Drive folder 'X'.", GoogleDriveProvider.ConnectedMessage("X", true, null));
        }

        [Test]
        public void ConnectedMessage_NamesTheAccountWhenItIsKnown()
        {
            string message = GoogleDriveProvider.ConnectedMessage("X", true, Email);
            StringAssert.Contains("Connected to Google Drive folder 'X'", message);
            StringAssert.Contains("(signed in as a@b.example)", message);
            AssertReadsCleanly(message);
        }

        [Test]
        public void ConnectedMessage_ExplainsAnEmptyListing()
        {
            foreach (string email in new string[] { Email, null })
            {
                string message = GoogleDriveProvider.ConnectedMessage("X", false, email);
                StringAssert.Contains("sees no files", message);
                StringAssert.Contains("brand-new folder", message);
                StringAssert.Contains(email == null ? "this account" : email, message);
                AssertReadsCleanly(message);
            }
        }
    }
}
