#if BASIS_FRAMEWORK_EXISTS
using System.Collections.Generic;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using UnityEngine;

namespace Basis.Integration.SlimeVR
{
    /// <summary>
    /// Adds the SlimeVR controls (enable, auto-apply body measurements, status, resets) as a
    /// section inside the framework's Tracker Settings tab via
    /// SettingsProvider.TrackerSettingsExtraBuilder.
    /// </summary>
    public static class SettingsProviderSlimeVR
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Register()
        {
            // Deferred one frame: other packages assign TrackerSettingsExtraBuilder with `=`
            // during RuntimeInitializeOnLoadMethod, which would drop a section subscribed at the
            // same time. The main-thread queue drains after every load method has run, so a `+=`
            // from here always composes.
            BasisDeviceManagement.mainThreadActions.Enqueue(() =>
            {
                SettingsProvider.TrackerSettingsExtraBuilder += BuildSection;
            });
        }

        private static void BuildSection(RectTransform parent)
        {
            PanelElementDescriptor tabDescriptor = parent.GetComponentInParent<PanelElementDescriptor>(true);

            PanelSectionToggle sectionToggle = PanelSectionToggle.CreateNewEntry(parent);
            sectionToggle.SetTitle("SlimeVR");
            int sectionStart = parent.childCount;

            PanelElementDescriptor group = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, parent);
            group.SetDescription("Sets your height and arm span automatically from a running SlimeVR server.");
            var content = group.ContentParent;

            PanelToggle enableToggle = PanelToggle.CreateNewEntry(content);
            enableToggle.Descriptor.SetTitle("Connect To SlimeVR");
            enableToggle.Descriptor.SetDescription("Stay connected to a local SlimeVR server.");
            enableToggle.SetValueWithoutNotify(BasisSlimeVRSettings.Enable.RawValue);
            enableToggle.OnValueChanged += value => BasisSlimeVRSettings.Enable.SetValue(value);

            PanelDropdown transportDropdown = PanelDropdown.CreateNewEntry(content);
            transportDropdown.Descriptor.SetTitle("Connection Method");
            transportDropdown.Descriptor.SetDescription("WebSocket suits every server; Pipe is SlimeVR's native transport.");
            transportDropdown.AssignEntries(
                new List<string> { BasisSlimeVRSettings.TransportWebSocket, BasisSlimeVRSettings.TransportPipe },
                new List<string> { "WebSocket", "Pipe" });
            transportDropdown.SetValueWithoutNotify(BasisSlimeVRSettings.Transport.RawValue);
            transportDropdown.OnValueChanged += value => BasisSlimeVRSettings.Transport.SetValue(value);

            PanelToggle applyToggle = PanelToggle.CreateNewEntry(content);
            applyToggle.Descriptor.SetTitle("Auto Apply Body Measurements");
            applyToggle.Descriptor.SetDescription("Use SlimeVR's eye height and arm span as your body size.");
            applyToggle.SetValueWithoutNotify(BasisSlimeVRSettings.ApplyBodyMeasurements.RawValue);
            applyToggle.OnValueChanged += value => BasisSlimeVRSettings.ApplyBodyMeasurements.SetValue(value);

            PanelToggle autoBindToggle = PanelToggle.CreateNewEntry(content);
            autoBindToggle.Descriptor.SetTitle("Auto Bind SlimeVR Trackers");
            autoBindToggle.Descriptor.SetDescription("Bind trackers to their announced body part automatically.");
            autoBindToggle.SetValueWithoutNotify(BasisSlimeVRSettings.AutoBindSlimeVRTrackers.RawValue);
            autoBindToggle.OnValueChanged += value => BasisSlimeVRSettings.AutoBindSlimeVRTrackers.SetValue(value);

            PanelToggle recalToggle = PanelToggle.CreateNewEntry(content);
            recalToggle.Descriptor.SetTitle("Recalibrate On Reset");
            recalToggle.Descriptor.SetDescription("Re-run calibration after a SlimeVR mounting or full reset.");
            recalToggle.SetValueWithoutNotify(BasisSlimeVRSettings.RecalibrateOnMountingChange.RawValue);
            recalToggle.OnValueChanged += value => BasisSlimeVRSettings.RecalibrateOnMountingChange.SetValue(value);

            PanelDropdown trackerSourceDropdown = PanelDropdown.CreateNewEntry(content);
            trackerSourceDropdown.Descriptor.SetTitle("Tracker Source (Experimental)");
            trackerSourceDropdown.Descriptor.SetDescription("Get trackers from the SlimeVR server instead of OpenVR/OpenXR. Needs SteamVR in the background.");
            trackerSourceDropdown.AssignEntries(
                new List<string> { BasisSlimeVRSettings.TrackerSourceOff, BasisSlimeVRSettings.TrackerSourceAuto, BasisSlimeVRSettings.TrackerSourceForce },
                new List<string> { "Off", "Auto (fallback)", "Force (override)" });
            trackerSourceDropdown.SetValueWithoutNotify(BasisSlimeVRSettings.TrackerSource.RawValue);
            trackerSourceDropdown.OnValueChanged += value => BasisSlimeVRSettings.TrackerSource.SetValue(value);

            PanelSlider.CreateAndBind(content,
                new PanelSlider.SliderSettings("Pose Countdown",
                    "Seconds the buttons below give you to get into pose before they fire. 0 fires instantly.",
                    0f, 10f, true, 0, ValueDisplayMode.Raw),
                BasisSlimeVRSettings.PoseCountdownSeconds);

            PanelButton yawReset = PanelButton.CreateNew(content);
            WirePoseCountdownButton(yawReset, "Yaw Reset",
                "Straighten the trackers. Fires after the pose countdown.",
                BasisSlimeVRPoseAction.YawReset);

            PanelButton fullReset = PanelButton.CreateNew(content);
            WirePoseCountdownButton(fullReset, "Full Reset",
                "Full reset — stand straight during the countdown.",
                BasisSlimeVRPoseAction.FullReset);

            PanelButton recalibrate = PanelButton.CreateNew(content);
            WirePoseCountdownButton(recalibrate, "Recalibrate Full Body",
                "Recapture tracker offsets — stand straight during the countdown.",
                BasisSlimeVRPoseAction.RecalibrateFbt, "manual (settings)");

            PanelElementDescriptor statusField = PanelElementDescriptor.CreateNew(
                PanelElementDescriptor.ElementStyles.Group, content);
            statusField.SetTitle("Status");
            statusField.SetDescription(DescribeStatus());

            PanelButton refresh = PanelButton.CreateNew(content);
            refresh.Descriptor.SetTitle("Refresh");
            refresh.Descriptor.SetDescription("Re-read measurements and status.");
            refresh.OnClicked += () =>
            {
                BasisSlimeVRBridge.RefreshBodyMeasurements();
                statusField.SetDescription(DescribeStatus());
            };

            void RefreshStatusText()
            {
                if (statusField == null)
                {
                    BasisSlimeVRBridge.OnConnectionChanged -= OnConnection;
                    BasisSlimeVRBridge.OnBodyMetricsChanged -= OnMetrics;
                    return;
                }
                statusField.SetDescription(DescribeStatus());
            }

            void OnConnection(bool _) => RefreshStatusText();
            void OnMetrics(BasisSlimeVRBodyMetrics _) => RefreshStatusText();

            BasisSlimeVRBridge.OnConnectionChanged += OnConnection;
            BasisSlimeVRBridge.OnBodyMetricsChanged += OnMetrics;

            PanelSectionToggleHelpers.FinalizeFlatSectionFromIndex(sectionToggle, parent, sectionStart, false, _ =>
            {
                tabDescriptor?.ForceRebuild();
            });
        }

        /// <summary>
        /// Routes a button through the bridge's pose countdown: pressing starts the countdown (the
        /// title ticks down), pressing again cancels. The countdown itself runs on
        /// <see cref="BasisSlimeVRBridge"/>, so closing the menu mid-count still fires the action.
        /// </summary>
        private static void WirePoseCountdownButton(PanelButton button, string title, string description,
            BasisSlimeVRPoseAction action, string recaptureReason = null)
        {
            button.Descriptor.SetTitle(title);
            button.Descriptor.SetDescription(description);

            button.OnClicked += () =>
            {
                if (BasisSlimeVRBridge.HasPoseCountdown && BasisSlimeVRBridge.PoseCountdownAction == action)
                {
                    BasisSlimeVRBridge.CancelPoseCountdown();
                }
                else
                {
                    BasisSlimeVRBridge.StartPoseCountdown(action, recaptureReason);
                }
            };

            void OnTick(BasisSlimeVRPoseAction tickAction, int secondsRemaining)
            {
                if (button == null)
                {
                    BasisSlimeVRBridge.OnPoseCountdownTick -= OnTick;
                    BasisSlimeVRBridge.OnPoseCountdownEnded -= OnEnded;
                    return;
                }
                if (tickAction != action)
                {
                    return;
                }
                button.Descriptor.SetTitle($"{title} ({secondsRemaining})");
                button.Descriptor.SetDescription("Get into pose — press again to cancel.");
            }

            void OnEnded(BasisSlimeVRPoseAction endedAction, bool _)
            {
                if (button == null)
                {
                    BasisSlimeVRBridge.OnPoseCountdownTick -= OnTick;
                    BasisSlimeVRBridge.OnPoseCountdownEnded -= OnEnded;
                    return;
                }
                if (endedAction != action)
                {
                    return;
                }
                button.Descriptor.SetTitle(title);
                button.Descriptor.SetDescription(description);
            }

            BasisSlimeVRBridge.OnPoseCountdownTick += OnTick;
            BasisSlimeVRBridge.OnPoseCountdownEnded += OnEnded;

            // A rebuilt panel (tab reopened mid-count) picks the running countdown back up.
            if (BasisSlimeVRBridge.HasPoseCountdown && BasisSlimeVRBridge.PoseCountdownAction == action)
            {
                OnTick(action, BasisSlimeVRBridge.PoseCountdownSecondsRemaining);
            }
        }

        private static string DescribeStatus()
        {
            if (!BasisSlimeVRSettings.Enable.RawValue)
            {
                return "Disabled.";
            }
            if (!BasisSlimeVRBridge.IsConnected)
            {
                return "Looking for a SlimeVR server...";
            }

            var text = new StringBuilder("Connected.");
            if (BasisSlimeVRBridge.HasBodyMetrics)
            {
                var metrics = BasisSlimeVRBridge.LastBodyMetrics;
                text.Append($" Eye height {metrics.EyeHeightMeters:F2}m, full height {metrics.FullHeightMeters:F2}m, arm span {metrics.ControllerSpanMeters:F2}m.");
            }

            int physical = 0;
            float lowestBattery = float.MaxValue;
            foreach (var tracker in BasisSlimeVRBridge.Trackers)
            {
                if (tracker.IsSynthetic)
                {
                    continue;
                }
                physical++;
                if (tracker.HasBattery && tracker.BatteryPercent < lowestBattery)
                {
                    lowestBattery = tracker.BatteryPercent;
                }
            }
            if (physical > 0)
            {
                text.Append($" {physical} trackers");
                if (lowestBattery < float.MaxValue)
                {
                    text.Append($", lowest battery {lowestBattery:F0}%");
                }
                text.Append('.');
            }
            if (BasisSlimeVRBridge.OffsetsStale)
            {
                text.Append($" Tracker mounting changed ({BasisSlimeVRBridge.MountingDriftDegrees:F0}°) — refreshing full-body offsets.");
            }
            return text.ToString();
        }
    }
}
#endif
