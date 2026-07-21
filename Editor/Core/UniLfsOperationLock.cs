using System;

namespace UniLFS.Editor
{
    /// <summary>Thrown when an operation is started while another one is still running.</summary>
    public class UniLfsBusyException : Exception
    {
        public UniLfsBusyException(string running)
            : base("Another UniLFS operation (" + running + ") is already running. Wait for it to finish and try again.")
        {
            Running = running;
        }

        /// <summary>Name of the operation currently holding the lock.</summary>
        public string Running { get; private set; }
    }

    /// <summary>
    /// Process-wide mutual exclusion for the operations that rewrite the
    /// manifest, the managed .gitignore block and the state cache.
    ///
    /// The editor window, Auto Pull/Push and the asset menu each fire
    /// independently and used to guard only themselves, so a manual Push could
    /// overlap an Auto Push — and whichever saved the manifest last silently
    /// discarded the other one's entries.
    /// </summary>
    public static class UniLfsOperationLock
    {
        static readonly object Gate = new object();
        static string _running;

        /// <summary>Name of the running operation, or null when idle.</summary>
        public static string Running
        {
            get { lock (Gate) return _running; }
        }

        public static bool IsBusy
        {
            get { return Running != null; }
        }

        /// <summary>Takes the lock, or throws <see cref="UniLfsBusyException"/> when it is held.</summary>
        public static IDisposable Acquire(string name)
        {
            lock (Gate)
            {
                if (_running != null) throw new UniLfsBusyException(_running);
                _running = name;
            }
            return new Scope();
        }

        sealed class Scope : IDisposable
        {
            bool _released;

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                lock (Gate) _running = null;
            }
        }
    }
}
