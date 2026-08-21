using System.IO;
using System.Text;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// UserSettings/UniLFS.json is written per machine and never migrated, so
    /// the only thing standing between a file from an earlier version and a
    /// lost refresh token is JsonUtility leaving a field the file never mentions
    /// at its default. That is an assumption, and this is where it is checked.
    /// </summary>
    public class UniLfsUserSettingsTests
    {
        [Test]
        public void LoadFrom_DefaultsTheAddressForAFileThatPredatesIt()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "{\"version\":1,\"s3AccessKeyId\":\"\",\"s3SecretAccessKey\":\"\",\"driveClientId\":\"\","
                    + "\"driveClientSecret\":\"\",\"driveRefreshToken\":\"tok\"}",
                    new UTF8Encoding(false));

                var user = UniLfsUserSettings.LoadFrom(path);
                Assert.AreEqual("tok", user.driveRefreshToken);
                Assert.AreEqual("", user.driveAccountEmail);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void LoadFrom_ReadsBackARecordedAddress()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "{\"version\":1,\"driveRefreshToken\":\"tok\",\"driveAccountEmail\":\"a@b.example\"}",
                    new UTF8Encoding(false));

                var user = UniLfsUserSettings.LoadFrom(path);
                Assert.AreEqual("tok", user.driveRefreshToken);
                Assert.AreEqual("a@b.example", user.driveAccountEmail);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
