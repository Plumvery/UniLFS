using System;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    /// <summary>
    /// UNILFS_NO_PROMPTS is the per-machine escape hatch for an editor nobody
    /// is watching: Auto Pull and Auto Push live in committed project settings,
    /// so turning them off is a decision for the whole team, not for the one
    /// machine running an unattended editor. What matters is that the variable
    /// reads the way the other UNILFS_ flags do — present and not "0" means on
    /// — so a shell that exports it as 0 does not silence prompts by accident.
    /// </summary>
    public class PromptSuppressionTests
    {
        string _saved;

        [SetUp]
        public void SetUp()
        {
            _saved = Environment.GetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts, _saved);
        }

        [Test]
        public void Unset_DoesNotSuppress()
        {
            Environment.SetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts, null);
            Assert.IsFalse(UniLfsPrompt.SuppressedByEnvironment);
        }

        [Test]
        public void Empty_DoesNotSuppress()
        {
            Environment.SetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts, "");
            Assert.IsFalse(UniLfsPrompt.SuppressedByEnvironment);
        }

        [Test]
        public void Zero_DoesNotSuppress()
        {
            Environment.SetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts, "0");
            Assert.IsFalse(UniLfsPrompt.SuppressedByEnvironment);
        }

        [Test]
        public void One_Suppresses()
        {
            Environment.SetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts, "1");
            Assert.IsTrue(UniLfsPrompt.SuppressedByEnvironment);
        }

        [Test]
        public void AnyOtherValue_Suppresses()
        {
            Environment.SetEnvironmentVariable(UniLfsPrompt.EnvNoPrompts, "true");
            Assert.IsTrue(UniLfsPrompt.SuppressedByEnvironment);
        }

        [Test]
        public void NoPromptIsOpenByDefault()
        {
            // The guard the auto-sync checks lean on: with nothing on screen it
            // must not claim a prompt already owns the decision, or Auto Pull
            // would never ask at all.
            Assert.IsFalse(UniLfsPrompt.IsOpen);
        }
    }
}
