#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Platform
{
    public static partial class BasisDesktopFileDrop
    {
        [NoAutoStaticsCleanup] private static AndroidJavaClass _intake;
        [NoAutoStaticsCleanup] private static AndroidJavaObject _activity;
        [NoAutoStaticsCleanup] private static bool _intakeFailed;
        private static int _nextIntakePoll;

        private static bool EnsureIntake()
        {
            if (_intakeFailed) return false;
            if (_intake != null) return true;
            try
            {
                _intake = new AndroidJavaClass("com.basis.platform.BasisFileIntake");
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                return true;
            }
            catch (Exception exception)
            {
                _intakeFailed = true;
                _intake = null;
                BasisDebug.LogWarning($"File intake: unavailable ({exception.Message}); shared files will not be received.", LogTag);
                return false;
            }
        }

        private static void PollPlatform()
        {
            if (Time.frameCount < _nextIntakePoll) return;
            _nextIntakePoll = Time.frameCount + 15;
            if (!EnsureIntake()) return;
            try
            {
                string batch;
                while ((batch = _intake.CallStatic<string>("poll", _activity)) != null)
                {
                    string[] paths = batch.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (paths.Length > 0) _pendingBatches.Enqueue(paths);
                }
            }
            catch (Exception exception)
            {
                _intakeFailed = true;
                BasisDebug.LogWarning($"File intake: polling failed ({exception.Message}); shared files will not be received.", LogTag);
            }
        }

        private static void PickPlatform()
        {
            if (!EnsureIntake()) return;
            try
            {
                _intake.CallStatic("pick", _activity);
            }
            catch (Exception exception)
            {
                BasisDebug.LogWarning($"File intake: picker failed ({exception.Message}).", LogTag);
            }
        }
    }
}
#endif
