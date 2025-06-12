using System;

namespace OpusSharp.Core
{
    /// <summary>
    /// An opus exception.
    /// </summary>
    public class OpusException : Exception
    {
        /// <summary>
        /// Constructs an opus exception.
        /// </summary>
        public OpusException() { }

        /// <summary>
        /// Constructs an opus exception.
        /// </summary>
        /// <param name="message">The message of the exception.</param>
        public OpusException(string message) : base(message) { }

        /// <summary>
        /// Constructs an opus exception.
        /// </summary>
        /// <param name="message">The message of the exception.</param>
        /// <param name="innerException">The root exception.</param>
        public OpusException(string message, Exception innerException) : base(message, innerException) { }
    }
}
