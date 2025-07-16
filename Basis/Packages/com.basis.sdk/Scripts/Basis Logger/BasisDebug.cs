using System;
using UnityEngine;

public static class BasisDebug
{
    public static void LogError(string message, LogTag logTag = LogTag.System)
    {
        Debug.unityLogger.LogError("",FormatMessage(message, logTag, MessageType.Error));
    }
    public static void LogError(string message, UnityEngine.Object Object, LogTag logTag = LogTag.System)
    {
        Debug.unityLogger.LogError("", FormatMessage(message, logTag, MessageType.Error), Object);
    }
    public static void LogError(Exception message, LogTag logTag = LogTag.System)
    {
        Debug.unityLogger.LogError("", FormatMessage($"{message.Message} {message.StackTrace}", logTag, MessageType.Error));
    }

    public static void LogWarning(string message, LogTag logTag = LogTag.System)
    {
        LogInternal(message, logTag, MessageType.Warning);
    }

    public static void Log(string message, LogTag logTag = LogTag.System)
    {
        LogInternal(message, logTag, MessageType.Info);
    }

    public static void LogInternal(string message, LogTag logTag, MessageType messageType)
    {
        Debug.unityLogger.Log(FormatMessage(message, logTag, messageType));
    }

    public static string FormatMessage(string message, LogTag logTag, MessageType messageType)
    {
        // Retrieve colors for the tag and message type
        string logTagColor = GetTagColor(logTag);
        string messageTypeColor = GetMessageTypeColor(messageType);

        // Format the message with proper syntax
        return $"<color=#242424>[<color={logTagColor}>{logTag}</color>]</color> <color={messageTypeColor}>{message}</color>";
    }

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
            _ => "#FFFFFF"                    // Default White
        };
    }

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
    }

    public enum MessageType
    {
        Info,
        Warning,
        Error
    }
}
