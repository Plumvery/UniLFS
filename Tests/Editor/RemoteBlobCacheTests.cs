using System;
using System.IO;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class RemoteBlobCacheTests
    {
        const string HashA = "aaaa000000000000000000000000000000000000000000000000000000000000";
        const string HashB = "bbbb000000000000000000000000000000000000000000000000000000000000";

        string _dir;
        string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "unilfs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "remote.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        [Test]
        public void Unknown_UntilConfirmed()
        {
            var cache = new UniLfsRemoteBlobCache(_path);
            Assert.IsFalse(cache.Contains(HashA), "a freshly tracked blob must not count as uploaded");
        }

        [Test]
        public void Add_SurvivesReload()
        {
            var cache = new UniLfsRemoteBlobCache(_path);
            cache.Add(HashA);
            cache.Save();

            var reloaded = new UniLfsRemoteBlobCache(_path);
            Assert.IsTrue(reloaded.Contains(HashA));
            Assert.IsFalse(reloaded.Contains(HashB));
        }

        /// <summary>
        /// Confirmations have to be retractable, or a blob deleted from the
        /// bucket would keep reading as "up to date" on every machine that once
        /// saw it there — exactly the case Refresh's remote check exists to
        /// catch.
        /// </summary>
        [Test]
        public void Remove_RetractsAConfirmation()
        {
            var cache = new UniLfsRemoteBlobCache(_path);
            cache.AddRange(new[] { HashA, HashB });
            cache.Save();

            var reopened = new UniLfsRemoteBlobCache(_path);
            reopened.Remove(HashA);
            reopened.Save();

            var reloaded = new UniLfsRemoteBlobCache(_path);
            Assert.IsFalse(reloaded.Contains(HashA), "a retracted confirmation must not come back after a reload");
            Assert.IsTrue(reloaded.Contains(HashB), "retracting one blob must not touch the others");
        }

        [Test]
        public void RetainOnly_DropsBlobsTheManifestNoLongerReferences()
        {
            var cache = new UniLfsRemoteBlobCache(_path);
            cache.AddRange(new[] { HashA, HashB });
            cache.RetainOnly(new[] { HashA });
            cache.Save();

            var reloaded = new UniLfsRemoteBlobCache(_path);
            Assert.IsTrue(reloaded.Contains(HashA));
            Assert.IsFalse(reloaded.Contains(HashB));
        }

        [Test]
        public void CorruptFile_IsTreatedAsEmpty()
        {
            File.WriteAllText(_path, "{ this is not json");
            var cache = new UniLfsRemoteBlobCache(_path);
            Assert.IsFalse(cache.Contains(HashA));
        }

        /// <summary>
        /// Confirmations are proof about one bucket only — repointing the
        /// project elsewhere must not inherit them, or files would show as
        /// uploaded to a bucket that has never seen them.
        /// </summary>
        [Test]
        public void Fingerprint_IsScopedToTheStorageLocation()
        {
            var a = new UniLfsSettings { provider = UniLfsSettings.ProviderS3, s3Endpoint = "https://e", s3Bucket = "one", s3Prefix = "unilfs" };
            var same = new UniLfsSettings { provider = UniLfsSettings.ProviderS3, s3Endpoint = "https://e", s3Bucket = "one", s3Prefix = "unilfs" };
            var otherBucket = new UniLfsSettings { provider = UniLfsSettings.ProviderS3, s3Endpoint = "https://e", s3Bucket = "two", s3Prefix = "unilfs" };
            var otherPrefix = new UniLfsSettings { provider = UniLfsSettings.ProviderS3, s3Endpoint = "https://e", s3Bucket = "one", s3Prefix = "other" };
            var drive = new UniLfsSettings { provider = UniLfsSettings.ProviderGoogleDrive, driveFolderId = "folder1" };

            Assert.AreEqual(UniLfsRemoteBlobCache.Fingerprint(a), UniLfsRemoteBlobCache.Fingerprint(same));
            Assert.AreNotEqual(UniLfsRemoteBlobCache.Fingerprint(a), UniLfsRemoteBlobCache.Fingerprint(otherBucket));
            Assert.AreNotEqual(UniLfsRemoteBlobCache.Fingerprint(a), UniLfsRemoteBlobCache.Fingerprint(otherPrefix));
            Assert.AreNotEqual(UniLfsRemoteBlobCache.Fingerprint(a), UniLfsRemoteBlobCache.Fingerprint(drive));
        }

        [Test]
        public void Fingerprint_IgnoresRegion()
        {
            var a = new UniLfsSettings { provider = UniLfsSettings.ProviderS3, s3Endpoint = "https://e", s3Bucket = "one", s3Region = "auto" };
            var b = new UniLfsSettings { provider = UniLfsSettings.ProviderS3, s3Endpoint = "https://e", s3Bucket = "one", s3Region = "us-east-1" };
            Assert.AreEqual(UniLfsRemoteBlobCache.Fingerprint(a), UniLfsRemoteBlobCache.Fingerprint(b));
        }
    }
}
