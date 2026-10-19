#if BASIS_FRAMEWORK_EXISTS
using Basis.Editor.Localization;
using Basis.Integration.SlimeVR;
using UnityEditor;
using UnityEngine;

namespace Basis.Integration.SlimeVR.Editor
{
    /// <summary>Live view of the SlimeVR connection: body measurements, skeleton parts, trackers and resets.</summary>
    public class BasisSlimeVRDebugWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("Basis/Debug/SlimeVR", false, 607)]
        public static void ShowWindow()
        {
            GetWindow<BasisSlimeVRDebugWindow>(BasisEditorLocalization.Get("editor.slimeVr.debug.title"));
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
            BasisEditorUI.Header("SlimeVR",
                BasisEditorLocalization.Get("editor.slimeVr.debug.subtitle"));

            if (!Application.isPlaying)
            {
                BasisEditorUI.Help(BasisEditorLocalization.Get("editor.slimeVr.debug.enterPlayMode"), MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.slimeVr.debug.connection"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.enabled"), BasisSlimeVRSettings.Enable.RawValue ? BasisEditorLocalization.Get("editor.common.yes") : BasisEditorLocalization.Get("editor.common.no"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.connected"), BasisSlimeVRBridge.IsConnected ? BasisEditorLocalization.Get("editor.common.yes") : BasisEditorLocalization.Get("editor.common.no"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.autoApply"), BasisSlimeVRSettings.ApplyBodyMeasurements.RawValue ? BasisEditorLocalization.Get("editor.common.yes") : BasisEditorLocalization.Get("editor.common.no"));

            EditorGUILayout.Space();
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.slimeVr.debug.bodyMeasurements"));
            if (BasisSlimeVRBridge.HasBodyMetrics)
            {
                var metrics = BasisSlimeVRBridge.LastBodyMetrics;
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.eyeHeight"), $"{metrics.EyeHeightMeters:F3} m");
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.fullHeight"), $"{metrics.FullHeightMeters:F3} m");
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.wristSpan"), $"{metrics.WristSpanMeters:F3} m");
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.controllerSpan"), $"{metrics.ControllerSpanMeters:F3} m");
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.appliedEyeHeight"), $"{BasisHeightDriver.PlayerEyeHeight:F3} m");
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.appliedArmSpan"), $"{BasisHeightDriver.PlayerArmSpan:F3} m");
                EditorGUILayout.LabelField("DeviceScale", $"{BasisHeightDriver.DeviceScale:F4}");
            }
            else
            {
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.noneReceived"));
            }

            var config = BasisSlimeVRBridge.LastSkeletonConfig;
            if (config != null && config.Parts.Count > 0)
            {
                EditorGUILayout.Space();
                BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.slimeVr.debug.skeletonParts", config.Parts.Count));
                foreach (var part in config.Parts)
                {
                    EditorGUILayout.LabelField(part.Key.ToString(), $"{part.Value:F4} m");
                }
            }

            EditorGUILayout.Space();
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.slimeVr.debug.trackers", BasisSlimeVRBridge.Trackers.Count));
            foreach (var tracker in BasisSlimeVRBridge.Trackers)
            {
                string name = !string.IsNullOrEmpty(tracker.CustomName) ? tracker.CustomName
                    : !string.IsNullOrEmpty(tracker.DisplayName) ? tracker.DisplayName
                    : tracker.DeviceName ?? BasisEditorLocalization.Get("editor.slimeVr.debug.unnamed");
                string details = $"{tracker.BodyPart} | {tracker.Status}";
                if (tracker.IsSynthetic)
                {
                    details += " | " + BasisEditorLocalization.Get("editor.slimeVr.debug.synthetic");
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
                    details += " | " + BasisEditorLocalization.Get("editor.slimeVr.debug.mountDrift", BasisSlimeVRBridge.GetMountingDriftDegrees(tracker.BodyPart));
                }
                EditorGUILayout.LabelField(name, details);
            }

            EditorGUILayout.Space();
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.slimeVr.debug.fbtOffsetFreshness"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.autoRecalibrate"), BasisSlimeVRSettings.RecalibrateOnMountingChange.RawValue ? BasisEditorLocalization.Get("editor.common.yes") : BasisEditorLocalization.Get("editor.common.no"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.maxMountingDrift"), $"{BasisSlimeVRBridge.MountingDriftDegrees:F1}°");
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.offsetsStale"), BasisSlimeVRBridge.OffsetsStale ? BasisEditorLocalization.Get("editor.slimeVr.debug.staleRefreshing") : BasisEditorLocalization.Get("editor.common.no"));
            if (!string.IsNullOrEmpty(BasisSlimeVRBridge.LastRecaptureReason))
            {
                float ago = Time.realtimeSinceStartup - BasisSlimeVRBridge.LastRecaptureRealtime;
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.lastRecapture"), BasisEditorLocalization.Get("editor.slimeVr.debug.lastRecaptureValue", BasisSlimeVRBridge.LastRecaptureReason, ago));
            }

            EditorGUILayout.Space();
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.slimeVr.debug.serverTrackerSource"));
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.mode"), BasisSlimeVRSettings.TrackerSource.RawValue);
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.slimeVr.debug.sourcedFromServer"), BasisSlimeVRTrackerSource.SourcedCount.ToString());

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(BasisEditorLocalization.Get("editor.slimeVr.debug.refreshConfig")))
            {
                BasisSlimeVRBridge.RefreshBodyMeasurements();
            }
            PoseCountdownButton(BasisEditorLocalization.Get("editor.slimeVr.debug.yawReset"), BasisSlimeVRPoseAction.YawReset);
            PoseCountdownButton(BasisEditorLocalization.Get("editor.slimeVr.debug.fullReset"), BasisSlimeVRPoseAction.FullReset);
            PoseCountdownButton(BasisEditorLocalization.Get("editor.slimeVr.debug.mountingReset"), BasisSlimeVRPoseAction.MountingReset);
            EditorGUILayout.EndHorizontal();

            PoseCountdownButton(BasisEditorLocalization.Get("editor.slimeVr.debug.recalibrateFbt"), BasisSlimeVRPoseAction.RecalibrateFbt, "manual (debug window)");

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
