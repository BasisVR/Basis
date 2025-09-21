/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Colocation
{
    /// <summary>
    ///     Class used for setting the visibility of logs
    /// </summary>
    internal static class Logger
    {
        private static bool _isVerboseLogVisible;
        private static bool _isInfoLogVisible;
        private static bool _isWarningLogVisible;
        private static bool _isErrorLogVisible;
        private static bool _isSharedSpatialAnchorsErrorVisible;

        public static void Log(string message, LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Verbose:
                    LogVerbose(message);
                    break;
                case LogLevel.Info:
                    LogInfo(message);
                    break;
                case LogLevel.Warning:
                    LogWarning(message);
                    break;
                case LogLevel.Error:
                    LogError(message);
                    break;
                case LogLevel.SharedSpatialAnchorsError:
                    LogSharedSpatialAnchorsError(message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(logLevel), logLevel, $"colocationLogLevel is unknown: {logLevel}");
            }
        }

        private static void LogVerbose(string message)
        {
            if (_isVerboseLogVisible)
            {
                string loggedMessage = GetPrefixMessage(LogLevel.Verbose) + message;
                Debug.Log(loggedMessage);
            }
        }

        private static void LogInfo(string message)
        {
            if (_isInfoLogVisible)
            {
                string loggedMessage = GetPrefixMessage(LogLevel.Info) + message;
                Debug.Log(loggedMessage);
            }
        }

        private static void LogWarning(string message)
        {
            if (_isWarningLogVisible)
            {
                string loggedMessage = GetPrefixMessage(LogLevel.Warning) + message;
                Debug.LogWarning(loggedMessage);
            }
        }

        private static void LogError(string message)
        {
            if (_isErrorLogVisible)
            {
                string loggedMessage = GetPrefixMessage(LogLevel.Error) + message;
                Debug.LogError(loggedMessage);
            }
        }

        private static void LogSharedSpatialAnchorsError(string message)
        {
            if (_isSharedSpatialAnchorsErrorVisible)
            {
                string loggedMessage = GetPrefixMessage(LogLevel.SharedSpatialAnchorsError) + message;
                Debug.LogError(loggedMessage);
            }
        }


        private static string GetPrefixMessage(LogLevel logLevel)
        {
            return $"[{logLevel}] ";
        }

        public static void SetLogLevelVisibility(LogLevel logLevel, bool value)
        {
            switch (logLevel)
            {
                case LogLevel.Verbose:
                    _isVerboseLogVisible = value;
                    break;
                case LogLevel.Info:
                    _isInfoLogVisible = value;
                    break;
                case LogLevel.Warning:
                    _isWarningLogVisible = value;
                    break;
                case LogLevel.Error:
                    _isErrorLogVisible = value;
                    break;
                case LogLevel.SharedSpatialAnchorsError:
                    _isSharedSpatialAnchorsErrorVisible = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(logLevel), logLevel, $"colocationLogLevel is unknown: {logLevel}");
            }
        }

        public static void SetAllLogsVisibility(bool value)
        {
            _isVerboseLogVisible = value;
            _isInfoLogVisible = value;
            _isWarningLogVisible = value;
            _isErrorLogVisible = value;
            _isSharedSpatialAnchorsErrorVisible = value;
        }
    }
}
