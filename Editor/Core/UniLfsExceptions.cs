using System;

namespace UniLFS.Editor
{
    /// <summary>Configuration is missing or invalid; the message tells the user where to fix it.</summary>
    public class UniLfsConfigException : Exception
    {
        public UniLfsConfigException(string message) : base(message) { }
    }

    /// <summary>A remote operation failed (network, auth, corrupted data, ...).</summary>
    public class UniLfsStorageException : Exception
    {
        public UniLfsStorageException(string message) : base(message) { }
        public UniLfsStorageException(string message, Exception inner) : base(message, inner) { }
    }
}
