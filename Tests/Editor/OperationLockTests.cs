using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UniLFS.Editor;

namespace UniLFS.Editor.Tests
{
    public class OperationLockTests
    {
        [TearDown]
        public void TearDown()
        {
            Assert.IsFalse(UniLfsOperationLock.IsBusy, "a test leaked the operation lock");
        }

        [Test]
        public void Acquire_ReportsTheRunningOperation()
        {
            using (UniLfsOperationLock.Acquire("Push"))
            {
                Assert.IsTrue(UniLfsOperationLock.IsBusy);
                Assert.AreEqual("Push", UniLfsOperationLock.Running);
            }
            Assert.IsFalse(UniLfsOperationLock.IsBusy);
            Assert.IsNull(UniLfsOperationLock.Running);
        }

        [Test]
        public void Acquire_WhileHeld_Throws()
        {
            using (UniLfsOperationLock.Acquire("Push"))
            {
                var e = Assert.Throws<UniLfsBusyException>(() => UniLfsOperationLock.Acquire("Pull"));
                Assert.AreEqual("Push", e.Running);
            }
        }

        [Test]
        public void Acquire_ReleasesWhenTheBodyThrows()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (UniLfsOperationLock.Acquire("Push"))
                    throw new InvalidOperationException("boom");
            });
            Assert.IsFalse(UniLfsOperationLock.IsBusy);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            var scope = UniLfsOperationLock.Acquire("Push");
            scope.Dispose();
            using (UniLfsOperationLock.Acquire("Pull"))
            {
                // A second dispose of the stale scope must not release the lock
                // somebody else now holds.
                scope.Dispose();
                Assert.AreEqual("Pull", UniLfsOperationLock.Running);
            }
        }

        /// <summary>
        /// The regression this lock exists for: a manual Push and an Auto Push
        /// overlapping, where whichever saved the manifest last discarded the
        /// other one's entries.
        /// </summary>
        [Test]
        public void ConcurrentOperations_NeverOverlap()
        {
            int concurrent = 0, maxConcurrent = 0, rejected = 0;
            var gate = new object();

            var tasks = new Task[32];
            for (int i = 0; i < tasks.Length; i++)
            {
                string name = "op" + i;
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        using (UniLfsOperationLock.Acquire(name))
                        {
                            int now = Interlocked.Increment(ref concurrent);
                            lock (gate) if (now > maxConcurrent) maxConcurrent = now;
                            await Task.Delay(10).ConfigureAwait(false);
                            Interlocked.Decrement(ref concurrent);
                        }
                    }
                    catch (UniLfsBusyException)
                    {
                        Interlocked.Increment(ref rejected);
                    }
                });
            }
            Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), "operations did not finish");

            Assert.AreEqual(1, maxConcurrent, "two operations ran at the same time");
            Assert.Greater(rejected, 0, "expected the losers to be rejected");
            Assert.IsFalse(UniLfsOperationLock.IsBusy);
        }
    }
}
