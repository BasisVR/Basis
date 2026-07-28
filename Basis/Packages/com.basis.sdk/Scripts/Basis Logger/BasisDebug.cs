using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using UnityEngine;

public static class BasisDebug
{
    /// <summary>
    /// When true, all BasisDebug Log/LogWarning/LogError calls are suppressed.
    /// Wired to the DisableLogging setting from BasisSettingsDefaults.
    /// </summary>
    public static bool LoggingDisabled;

    public static LogTag? TagFilter;

    public static MessageType MinimumLevel = MessageType.Info;

    private static bool ShouldEmit(LogTag logTag, MessageType messageType)
    {
        if (LoggingDisabled) return false;
        if ((int)messageType < (int)MinimumLevel) return false;
        if (TagFilter.HasValue && TagFilter.Value != logTag) return false;
        return true;
    }

    [HideInCallstack]
    public static void LogError(string message, LogTag logTag = LogTag.System)
    {
        if (!ShouldEmit(logTag, MessageType.Error)) return;
        Debug.unityLogger.LogError("",FormatMessage(message, logTag, MessageType.Error));
    }
    [HideInCallstack]
    public static void LogError(string message, UnityEngine.Object Object, LogTag logTag = LogTag.System)
    {
        if (!ShouldEmit(logTag, MessageType.Error)) return;
        Debug.unityLogger.LogError("", FormatMessage(message, logTag, MessageType.Error), Object);
    }
    [HideInCallstack]
    public static void LogError(Exception message, LogTag logTag = LogTag.System)
    {
        if (!ShouldEmit(logTag, MessageType.Error)) return;
        Debug.unityLogger.LogError("", FormatMessage($"{message.Message} {message.StackTrace}", logTag, MessageType.Error));
    }
    /// <summary>
    /// Logs at error level but marks the emission as not-reportable, so BasisExceptionNotifier
    /// skips the user-facing dialogue and the crash report sent to the server. Use for genuine
    /// errors that are caused by remote content or the environment rather than a client defect
    /// (bad media URL, unapproved component in someone else's avatar, no XR runtime present) —
    /// they belong in the log, but they are not this client crashing.
    /// Thread-safe: the flag is per-thread, and Unity raises the log callback synchronously on
    /// the thread that emitted it.
    /// </summary>
    [ThreadStatic] private static bool _reportSuppressed;
    public static bool ReportSuppressed => _reportSuppressed;

    [HideInCallstack]
    public static void LogErrorUnreported(string message, LogTag logTag = LogTag.System)
    {
        _reportSuppressed = true;
        try
        {
            LogError(message, logTag);
        }
        finally
        {
            _reportSuppressed = false;
        }
    }

    [HideInCallstack]
    public static void LogErrorUnreported(string message, UnityEngine.Object Object, LogTag logTag = LogTag.System)
    {
        _reportSuppressed = true;
        try
        {
            LogError(message, Object, logTag);
        }
        finally
        {
            _reportSuppressed = false;
        }
    }

    [HideInCallstack]
    public static void LogWarning(string message, LogTag logTag = LogTag.System)
    {
        if (!ShouldEmit(logTag, MessageType.Warning)) return;
        LogInternal(message, logTag, MessageType.Warning);
    }
    [HideInCallstack]
    public static void Log(string message, LogTag logTag = LogTag.System)
    {
        if (!ShouldEmit(logTag, MessageType.Info)) return;
        LogInternal(message, logTag, MessageType.Info);
    }
    [HideInCallstack]
    public static void LogInternal(string message, LogTag logTag, MessageType messageType)
    {
        Debug.unityLogger.Log(FormatMessage(message, logTag, messageType));
    }

    private static readonly ConcurrentDictionary<(string, int), byte> SiteOnceKeys = new ConcurrentDictionary<(string, int), byte>();
    private static readonly ConcurrentDictionary<string, byte> NamedOnceKeys = new ConcurrentDictionary<string, byte>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnceKeys()
    {
        SiteOnceKeys.Clear();
        NamedOnceKeys.Clear();
    }

    private static bool FirstTimeAtSite(string file, int line)
    {
        return SiteOnceKeys.TryAdd((file, line), 0);
    }

    private static bool FirstTimeForKey(string key)
    {
        return NamedOnceKeys.TryAdd(key, 0);
    }

    [HideInCallstack]
    public static void LogOnce(string message, LogTag logTag = LogTag.System, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!FirstTimeAtSite(file, line)) return;
        Log(message, logTag);
    }

    [HideInCallstack]
    public static void LogWarningOnce(string message, LogTag logTag = LogTag.System, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!FirstTimeAtSite(file, line)) return;
        LogWarning(message, logTag);
    }

    [HideInCallstack]
    public static void LogErrorOnce(string message, LogTag logTag = LogTag.System, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!FirstTimeAtSite(file, line)) return;
        LogError(message, logTag);
    }

    [HideInCallstack]
    public static void LogOnce(ref bool fired, string message, LogTag logTag = LogTag.System)
    {
        if (fired) return;
        fired = true;
        Log(message, logTag);
    }

    [HideInCallstack]
    public static void LogWarningOnce(ref bool fired, string message, LogTag logTag = LogTag.System)
    {
        if (fired) return;
        fired = true;
        LogWarning(message, logTag);
    }

    [HideInCallstack]
    public static void LogErrorOnce(ref bool fired, string message, LogTag logTag = LogTag.System)
    {
        if (fired) return;
        fired = true;
        LogError(message, logTag);
    }

    [HideInCallstack]
    public static void LogOnce(string key, string message, LogTag logTag = LogTag.System)
    {
        if (!FirstTimeForKey(key)) return;
        Log(message, logTag);
    }

    [HideInCallstack]
    public static void LogWarningOnce(string key, string message, LogTag logTag = LogTag.System)
    {
        if (!FirstTimeForKey(key)) return;
        LogWarning(message, logTag);
    }

    [HideInCallstack]
    public static void LogErrorOnce(string key, string message, LogTag logTag = LogTag.System)
    {
        if (!FirstTimeForKey(key)) return;
        LogError(message, logTag);
    }

    [HideInCallstack]
    public static string FormatMessage(string message, LogTag logTag, MessageType messageType)
    {
        // Retrieve colors for the tag and message type
        string logTagColor = GetTagColor(logTag);
        string messageTypeColor = GetMessageTypeColor(messageType);

        // Format the message with proper syntax
        return $"<color=#242424>[<color={logTagColor}>{logTag}</color>]</color> <color={messageTypeColor}>{message}</color>";
    }
    [HideInCallstack]
    public static string GetTagColor(LogTag logTag)
    {
        return logTag switch
        {
            LogTag.System => "#9370DB",       // Medium Purple
            LogTag.Voice => "#FF69B4",        // Hot Pink
            LogTag.Networking => "#1E90FF",   // Dodger Blue
            LogTag.IK => "#32CD32",           // Lime Green
            LogTag.Core => "#FFD700",         // Gold
            LogTag.Event => "#FF4500",        // Orange Red
            LogTag.Device => "#00CED1",       // Dark Turquoise
            LogTag.Avatar => "#8B0000",       // Dark Red
            LogTag.Input => "#808000",        // Olive
            LogTag.Gizmo => "#FF6347",        // Tomato
            LogTag.Scene => "#4682B4",        // Steel Blue
            LogTag.Editor => "#4B0082",       // Indigo
            LogTag.Pickups => "#DAA520",      // Goldenrod
            LogTag.Camera => "#2E8B57",       // Sea Green
            LogTag.Mirror => "#708090",       // Slate Gray
            LogTag.Local => "#20B2AA",        // Light Sea Green
            LogTag.Remote => "#DC143C",       // Crimson
            LogTag.Video => "#00ffff",        // Cyan
            LogTag.Shims => "#FF00FF",        // Magenta
            LogTag.Props => "#FFB6C1",        // Light Pink
            LogTag.LocalNetwork => "#ff0055",
            LogTag.AuthoredMotion => "#BA55D3", // Medium Orchid
            LogTag.Rendering => "#ADFF2F",    // Green Yellow
            LogTag.TrackerObjects => "#F4A460", // Sandy Brown
            _ => "#FFFFFF"                    // Default White
        };
    }
    [HideInCallstack]
    public static string GetMessageTypeColor(MessageType messageType)
    {
        return messageType switch
        {
            MessageType.Error => "#FF0000",    // Red for errors
            MessageType.Warning => "#FFA500", // Orange for warnings
            MessageType.Info => "#FFFFFF",    // Green for logs
            _ => "#FFFFFF"                    // Default White
        };
    }
    [HideInCallstack]
    public static string FormatLogMessage(LogTag logTag, MessageType messageType, string message)
    {
        string tagColor = GetTagColor(logTag);
        string messageTypeColor = GetMessageTypeColor(messageType);

        // Apply colors
        string formattedTag = $"<color=#808080>[{logTag}]</color>"; // Grey color for tag brackets
        string formattedMessage = $"<color={tagColor}>{message}</color>"; // Tag color for message text

        return $"{formattedTag} <color=#FFFFFF>{messageTypeColor}: {formattedMessage}</color>";
    }
    public enum LogTag
    {
        System,
        Voice,
        Networking,
        IK,
        Core,
        Event,
        Device,
        Avatar,
        Input,
        Gizmo,
        Scene,
        Editor,
        Pickups,
        Camera,
        Mirror,
        Local,
        Remote,
        Video,
        Shims,
        Props,
        LocalNetwork,
        AuthoredMotion,
        Rendering,
        TrackerObjects,
    }

    public enum MessageType
    {
        Info,
        Warning,
        Error
    }
}
