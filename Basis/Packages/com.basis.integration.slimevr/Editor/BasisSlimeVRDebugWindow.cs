#if BASIS_FRAMEWORK_EXISTS
using Basis.Integration.SlimeVR;
using UnityEditor;
using UnityEngine;

namespace Basis.Integration.SlimeVR.Editor
{
    /// <summary>Live view of the SlimeVR connection: body measurements, skeleton parts, trackers and resets.</summary>
    public class BasisSlimeVRDebugWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("Basis/Tests And Debug/SlimeVR Debug")]
        public static void ShowWindow()
        {
            GetWindow<BasisSlimeVRDebugWindow>("SlimeVR Debug");
        }

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter play mode to talk to SlimeVR.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Enabled", BasisSlimeVRSettings.Enable.RawValue ? "Yes" : "No");
            EditorGUILayout.LabelField("Connected", BasisSlimeVRBridge.IsConnected ? "Yes" : "No");
            EditorGUILayout.LabelField("Auto Apply", BasisSlimeVRSettings.ApplyBodyMeasurements.RawValue ? "Yes" : "No");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Body Measurements", EditorStyles.boldLabel);
            if (BasisSlimeVRBridge.HasBodyMetrics)
            {
                var metrics = BasisSlimeVRBridge.LastBodyMetrics;
                EditorGUILayout.LabelField("Eye Height", $"{metrics.EyeHeightMeters:F3} m");
                EditorGUILayout.LabelField("Full Height (est.)", $"{metrics.FullHeightMeters:F3} m");
                EditorGUILayout.LabelField("Wrist Span", $"{metrics.WristSpanMeters:F3} m");
                EditorGUILayout.LabelField("Controller Span", $"{metrics.ControllerSpanMeters:F3} m");
                EditorGUILayout.LabelField("Applied PlayerEyeHeight", $"{BasisHeightDriver.PlayerEyeHeight:F3} m");
                EditorGUILayout.LabelField("Applied PlayerArmSpan", $"{BasisHeightDriver.PlayerArmSpan:F3} m");
                EditorGUILayout.LabelField("DeviceScale", $"{BasisHeightDriver.DeviceScale:F4}");
            }
            else
            {
                EditorGUILayout.LabelField("(none received yet)");
            }

            var config = BasisSlimeVRBridge.LastSkeletonConfig;
            if (config != null && config.Parts.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Skeleton Parts ({config.Parts.Count})", EditorStyles.boldLabel);
                foreach (var part in config.Parts)
                {
                    EditorGUILayout.LabelField(part.Key.ToString(), $"{part.Value:F4} m");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Trackers ({BasisSlimeVRBridge.Trackers.Count})", EditorStyles.boldLabel);
            foreach (var tracker in BasisSlimeVRBridge.Trackers)
            {
                string name = !string.IsNullOrEmpty(tracker.CustomName) ? tracker.CustomName
                    : !string.IsNullOrEmpty(tracker.DisplayName) ? tracker.DisplayName
                    : tracker.DeviceName ?? "(unnamed)";
                string details = $"{tracker.BodyPart} | {tracker.Status}";
                if (tracker.IsSynthetic)
                {
                    details += " | synthetic";
                }
                if (tracker.IsHmd)
                {
                    details += " | HMD";
                }
                if (tracker.HasBattery)
                {
                    details += $" | {tracker.BatteryPercent:F0}% ({tracker.BatteryVoltage:F2}V)";
                }
                if (tracker.HasRssi)
                {
                    details += $" | {tracker.RssiDbm} dBm";
                }
                if (tracker.IsImu)
                {
                    details += " | IMU";
                }
                if (tracker.HasMountingOrientation || tracker.HasMountingResetOrientation)
                {
                    details += $" | mount drift {BasisSlimeVRBridge.GetMountingDriftDegrees(tracker.BodyPart):F1}°";
                }
                EditorGUILayout.LabelField(name, details);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("FBT Offset Freshness", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Auto Recalibrate On Reset", BasisSlimeVRSettings.RecalibrateOnMountingChange.RawValue ? "Yes" : "No");
            EditorGUILayout.LabelField("Max Mounting Drift", $"{BasisSlimeVRBridge.MountingDriftDegrees:F1}°");
            EditorGUILayout.LabelField("Offsets Stale", BasisSlimeVRBridge.OffsetsStale ? "Yes — refreshing" : "No");
            if (!string.IsNullOrEmpty(BasisSlimeVRBridge.LastRecaptureReason))
            {
                float ago = Time.realtimeSinceStartup - BasisSlimeVRBridge.LastRecaptureRealtime;
                EditorGUILayout.LabelField("Last Recapture", $"{BasisSlimeVRBridge.LastRecaptureReason} ({ago:F0}s ago)");
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server Tracker Source (Experimental)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Mode", BasisSlimeVRSettings.TrackerSource.RawValue);
            EditorGUILayout.LabelField("Sourced From Server", BasisSlimeVRTrackerSource.SourcedCount.ToString());

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Config"))
            {
                BasisSlimeVRBridge.RefreshBodyMeasurements();
            }
            PoseCountdownButton("Yaw Reset", BasisSlimeVRPoseAction.YawReset);
            PoseCountdownButton("Full Reset", BasisSlimeVRPoseAction.FullReset);
            PoseCountdownButton("Mounting Reset", BasisSlimeVRPoseAction.MountingReset);
            EditorGUILayout.EndHorizontal();

            PoseCountdownButton("Recalibrate Full Body From SlimeVR", BasisSlimeVRPoseAction.RecalibrateFbt, "manual (debug window)");

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Pose-sampling actions fire through the bridge's shared countdown so there is time to get
        /// into pose after clicking; the label ticks down (the window repaints every editor update)
        /// and clicking again cancels.
        /// </summary>
        private static void PoseCountdownButton(string label, BasisSlimeVRPoseAction action, string recaptureReason = null)
        {
            bool counting = BasisSlimeVRBridge.HasPoseCountdown && BasisSlimeVRBridge.PoseCountdownAction == action;
            string text = counting ? $"{label} ({BasisSlimeVRBridge.PoseCountdownSecondsRemaining})" : label;
            if (GUILayout.Button(text))
            {
                if (counting)
                {
                    BasisSlimeVRBridge.CancelPoseCountdown();
                }
                else
                {
                    BasisSlimeVRBridge.StartPoseCountdown(action, recaptureReason);
                }
            }
        }
    }
}
#endif
