
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Basis.ZeroMessenger.Internal {

    internal static class ThrowHelper
    {
        internal static void ThrowArgumentNullIfNull([NotNull] object? argument, [CallerArgumentExpression("argument")] string? paramName = null)
        {
            if (argument is null)
            {
                ThrowArgumentNullException(paramName);
            }
        }

        internal static void ThrowObjectDisposedIf([DoesNotReturnIf(true)] bool condition, Type type)
        {
            if (condition)
            {
                ThrowObjectDisposedException(type);
            }
        }

        [DoesNotReturn]
        internal static void ThrowArgumentNullException(string? paramName) => throw new ArgumentNullException(paramName);

        [DoesNotReturn]
        internal static void ThrowArgumentException(string? message) => throw new ArgumentException(message);

        [DoesNotReturn]
        internal static void ThrowObjectDisposedException(Type? type) => throw new ObjectDisposedException(type?.FullName);

        internal static void ThrowIfMessageHandlerIsAssigned<T>(MessageHandler<T> handler)
        {
            if (handler.Parent != null)
            {
                ThrowArgumentException("Message handler is already assigned.");
            }
        }

        internal static void ThrowIfMessageHandlerIsAssigned<T>(AsyncMessageHandler<T> handler)
        {
            if (handler.Parent != null)
            {
                ThrowArgumentException("Message handler is already assigned.");
            }
        }

        internal static void ThrowIfMessageHandlerIsNotAssigned<T>(MessageHandler<T> handler)
        {
            if (handler.Parent == null)
            {
                ThrowArgumentException("Message handler is not assigned.");
            }
        }

        internal static void ThrowIfMessageHandlerIsNotAssigned<T>(AsyncMessageHandler<T> handler)
        {
            if (handler.Parent == null)
            {
                ThrowArgumentException("Message handler is not assigned.");
            }
        }
    }
}