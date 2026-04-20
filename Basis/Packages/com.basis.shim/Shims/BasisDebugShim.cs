using UnityEngine;

namespace Basis.Shims
{
    public class BasisDebugPropsShim : UnityEngine.Debug
    {
        public static BasisDebug.LogTag LogTag = BasisDebug.LogTag.Props;
        //
        // Log
        //
        [HideInCallstack]
        public static new void Log(object message)
        {
            BasisDebug.Log(message.ToString(), LogTag);
        }
        [HideInCallstack]
        public static new void Log(object message, Object context)
        {
            BasisDebug.Log(message.ToString(), LogTag);
        }
        //
        // LogFormat
        //
        [HideInCallstack]
        public static new void LogFormat(string format, params object[] args)
        {
            BasisDebug.Log(string.Format(format, args), LogTag);
        }
        [HideInCallstack]
        public static new void LogFormat(Object context, string format, params object[] args)
        {
            BasisDebug.Log(string.Format(format, args), LogTag);
        }
        [HideInCallstack]
        public static new void LogFormat(LogType logType, LogOption logOptions, Object context, string format, params object[] args)
        {
            BasisDebug.Log(string.Format(format, args), LogTag);
        }
        //
        // LogError
        //
        [HideInCallstack]
        public static new void LogError(object message)
        {
            BasisDebug.LogError(message.ToString(), LogTag);
        }
        [HideInCallstack]
        public static new void LogError(object message, Object context)
        {
            BasisDebug.LogError(message.ToString(), context, LogTag);
        }
        public static new void LogErrorFormat(string format, params object[] args)
        {
            BasisDebug.LogError(string.Format(format, args), LogTag);
        }
        [HideInCallstack]
        public static new void LogErrorFormat(Object context, string format, params object[] args)
        {
            BasisDebug.LogError(string.Format(format, args), context, LogTag);
        }
        //
        // LogWarning
        //
        [HideInCallstack]
        public static new void LogWarning(object message)
        {
            BasisDebug.LogWarning(message.ToString(), LogTag);
        }
        [HideInCallstack]
        public static new void LogWarning(object message, Object context)
        {
            BasisDebug.LogWarning(message.ToString(), LogTag);
        }
        public static new void LogWarningFormat(string format, params object[] args)
        {
            BasisDebug.LogWarning(string.Format(format, args), LogTag);
        }
        [HideInCallstack]
        public static new void LogWarningFormat(Object context, string format, params object[] args)        {
            BasisDebug.LogWarning(string.Format(format, args), LogTag);
        }
        //
        // Assert
        //
        [HideInCallstack]
        public static new void Assert(bool condition)
        {
            if (!condition)
            {
                BasisDebug.LogError("Assertion failed", LogTag);
            }
        }
        [HideInCallstack]
        public static new void Assert(bool condition, Object context)
        {
            if (!condition)
            {
                BasisDebug.LogError("Assertion failed", context, LogTag);
            }
        }
        [HideInCallstack]
        public static new void Assert(bool condition, object message)
        {
            if (!condition)
            {
                BasisDebug.LogError(message.ToString(), LogTag);
            }
        }
        [HideInCallstack]
        public static new void Assert(bool condition, object message, Object context)
        {
            if (!condition)
            {
                BasisDebug.LogError(message.ToString(), context, LogTag);
            }
        }
        //
        // AssertFormat
        //
        [HideInCallstack]
        public static new void AssertFormat(bool condition, string format, params object[] args)
        {
            if (!condition)
            {
                BasisDebug.LogError(string.Format(format, args), LogTag);
            }
        }
        [HideInCallstack]
        public static new void AssertFormat(bool condition, Object context, string format, params object[] args)
        {
            if (!condition)            {
                BasisDebug.LogError(string.Format(format, args), context, LogTag);
            }
        }
        //
        // LogAssertion
        //
        [HideInCallstack]
        public static new void LogAssertion(object message)
        {
            BasisDebug.LogError(message.ToString(), LogTag);
        }
        [HideInCallstack]
        public static new void LogAssertion(object message, Object context)
        {
            BasisDebug.LogError(message.ToString(), context, LogTag);
        }
        [HideInCallstack]
        public static new void LogAssertionFormat(string format, params object[] args)
        {
            BasisDebug.LogError(string.Format(format, args), LogTag);
        }
        [HideInCallstack]
        public static new void LogAssertionFormat(Object context, string format, params object[] args)
        {
            BasisDebug.LogError(string.Format(format, args), context, LogTag);
        }
    }
}