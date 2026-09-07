using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Device_Management.EyeTracking;
using Basis.Scripts.Drivers;
using Basis.Scripts.Rendering;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Device_Management.EyeTracking.Editor
{
    /// <summary>
    /// Live view of the unified eye-tracking pipeline: what each device is feeding in
    /// (OpenXR / OpenVR / OSC), how arbitration merges it, the combined output, and which
    /// consumers (VRS, HAI eye bones) are receiving it — plus what is blocking the flow.
    /// </summary>
    public sealed class BasisEyePipelineTab : BasisEditorTabPage
    {
        public override string Title => "Pipeline";
        public override string Subtitle =>
            "What each device feeds in, how arbitration merges it, and who is consuming the result.";

        private static readonly Color Green = new Color(0.45f, 0.9f, 0.45f);
        private static readonly Color Red = new Color(0.95f, 0.5f, 0.5f);
        private static readonly Color Grey = new Color(0.65f, 0.65f, 0.65f);

        private Vector2 _scroll;

        public override void OnEnable() => EditorApplication.update += OnEditorUpdate;
        public override void OnDisable() => EditorApplication.update -= OnEditorUpdate;

        private void OnEditorUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Host.Repaint();
            }
        }

        public override void Draw()
        {
            if (!EditorApplication.isPlaying)
            {
                BasisEditorUI.Help("Enter Play Mode to see live eye-tracking data.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            List<string> blockers = new List<string>();
            DrawIncoming(blockers);
            DrawArbitration();
            DrawOutput();
            DrawConsumers(blockers);
            DrawFoveation(blockers);
            DrawBlockers(blockers);

            EditorGUILayout.EndScrollView();
        }

        private void DrawIncoming(List<string> blockers)
        {
            Header("Incoming — Devices");

            IReadOnlyList<IBasisEyeTrackingProvider> providers = BasisEyeTrackingManager.Providers;
            if (providers == null || providers.Count == 0)
            {
                BasisEditorUI.Note("No providers registered.");
                blockers.Add("No eye providers registered — no headset eye tracking is running and no OSC face-tracking component is on the local avatar.");
                return;
            }

            bool hmdPresent = false, hmdActive = false, oscPresent = false, oscActive = false;

            for (int i = 0; i < providers.Count; i++)
            {
                IBasisEyeTrackingProvider provider = providers[i];
                if (provider == null)
                {
                    continue;
                }

                bool active = provider.IsActive;
                if (provider.Source == BasisEyeSource.Hmd) { hmdPresent = true; hmdActive |= active; }
                else { oscPresent = true; oscActive |= active; }

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                Dot(DeviceLabel(provider), active);

                if (active)
                {
                    BasisEyeTrackingData d = default;
                    if (provider.TryGetEyeData(ref d))
                    {
                        if (d.HasWorldRay)
                        {
                            Field("World ray", $"o {Fmt(d.GazeOrigin)}  dir {Fmt(d.GazeDirection)}");
                        }
                        if (d.HasPerEyeAngles)
                        {
                            Field("Per-eye angles", $"L {Ang(d.LeftAngles)}   R {Ang(d.RightAngles)}");
                        }
                        if (d.HasOpenness)
                        {
                            Field("Openness", $"L {d.LeftOpenness:F2}  R {d.RightOpenness:F2}");
                        }
                        if (!d.HasWorldRay && !d.HasPerEyeAngles && !d.HasOpenness)
                        {
                            Field("", "active but produced no data this frame");
                        }
                    }
                }
                else
                {
                    string reason = provider.Source == BasisEyeSource.Hmd
                        ? "no valid eye-gaze pose (headset not reporting eye tracking this frame)"
                        : "inactive — no recent OSC eye params / EyeTrackingActive = 0";
                    EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }

            if (hmdPresent && !hmdActive)
            {
                blockers.Add("HMD eye source present but not tracking — the headset may not support eye tracking, or the gaze pose is invalid this frame.");
            }
            if (oscPresent && !oscActive)
            {
                blockers.Add("OSC face tracking present but inactive — no recent eye parameters were received (EyeTrackingActive = 0).");
            }
        }

        private void DrawArbitration()
        {
            Header("Arbitration");

            BasisEyeTrackingManager.BasisEyeArbitration a = BasisEyeTrackingManager.Arbitration;

            EditorGUILayout.LabelField("Preferred source", a.PreferOsc ? "OSC (face tracking)" : "HMD (eye gaze)");

            string gazeWinner = a.GazeFromOsc ? "OSC" : a.GazeFromHmd ? "HMD" : "none";
            Dot($"Gaze from: {gazeWinner}", a.GazeFromOsc || a.GazeFromHmd);

            if (a.OscHasGaze && a.HmdHasGaze && !a.GazeFromOsc)
            {
                BasisEditorUI.Note("OSC has gaze but HMD is preferred — OSC direction suppressed.");
            }
            if (a.GazeFromOsc && a.HmdHasGaze)
            {
                BasisEditorUI.Note("HMD has gaze but OSC is preferred — HMD direction suppressed.");
            }

            Dot($"Openness from: {(a.OpennessFromOsc ? "OSC" : "none")}", a.OpennessFromOsc);
        }

        private void DrawOutput()
        {
            Header("Output — Combined (Current)");

            BasisEyeTrackingData c = BasisEyeTrackingManager.Current;
            Dot("World ray", c.HasWorldRay);
            if (c.HasWorldRay)
            {
                Field("", $"o {Fmt(c.GazeOrigin)}  dir {Fmt(c.GazeDirection)}");
            }
            Dot("Per-eye angles", c.HasPerEyeAngles);
            if (c.HasPerEyeAngles)
            {
                Field("", $"L {Ang(c.LeftAngles)}   R {Ang(c.RightAngles)}");
            }
            Dot("Openness", c.HasOpenness);
        }

        private void DrawConsumers(List<string> blockers)
        {
            Header("Consumers");

            bool hasEyeGaze = BasisLocalCameraDriver.HasEyeGaze;
            Dot("VRS / foveation (BasisLocalCameraDriver.HasEyeGaze)", hasEyeGaze);
            if (!hasEyeGaze)
            {
                BasisEditorUI.Note("VRS foveal center falls back to screen center.");
            }

            Dot("Eye-tracking headset present (auto-foveation trigger)", BasisEyeTrackingManager.HmdEyeTrackingPresent);
            Dot("VR VRS enabled (DevVariableRateShading)", BasisSettingsDefaults.DevVariableRateShading.RawValue);

            bool eyeEnabled = BasisLocalEyeDriver.IsEnabled;
            bool overrideOn = BasisLocalEyeDriver.Override;
            Dot("Eye bones via HAI — driver enabled", eyeEnabled);
            Dot("Eye bones via HAI — override applied", overrideOn);

            BasisEyeTrackingData c = BasisEyeTrackingManager.Current;
            if (c.HasPerEyeAngles)
            {
                if (!eyeEnabled)
                {
                    blockers.Add("Gaze is available but the eye driver is disabled — the avatar has no eye bones, so eyes won't move (VRS still works).");
                }
                else if (!overrideOn)
                {
                    blockers.Add("Gaze is available and the eye driver is enabled, but Override is OFF — nothing is applying it to the eye bones. Is the HAI EyeTrackingBoneActuation component present on the local avatar?");
                }
            }
        }

        private void DrawFoveation(List<string> blockers)
        {
            Header("Foveated Rendering (VRS)");

            bool api = BasisVariableRateShadingFeature.IsSupported;
            Dot("Graphics API supports image VRS (Direct3D 12)", api);
            if (!api)
            {
                BasisEditorUI.Note("On every other graphics API the VRS feature is fully inactive.");
                return;
            }

            bool perTile = ShadingRateInfo.supportsPerImageTile;
            Dot("GPU/driver reports per-tile shading rate", perTile);
            Field("Hardware rates", RateListLabel());

            bool vrsOn = BasisSettingsDefaults.DevVariableRateShading.RawValue || BasisSettingsDefaults.DevVariableRateShadingDesktop.RawValue;
            Dot("Enabled (VR or Desktop toggle)", vrsOn);

            bool live = BasisVariableRateShadingFeature.LastDispatchFrame > 0
                && Time.frameCount - BasisVariableRateShadingFeature.LastDispatchFrame <= 2;
            Dot("Build pass dispatching", live);
            if (live)
            {
                Vector2Int tiles = BasisVariableRateShadingFeature.LastTiles;
                Field("Tiles", $"{tiles.x} x {tiles.y}");
                Field("Gaze weight", $"{BasisVariableRateShadingFeature.LastGazeWeight:F2}  (1 = tracked, 0 = optical-axis fallback)");
                Field("Build GPU cost", $"{BasisVariableRateShadingFeature.GpuMilliseconds:F3} ms");
            }

            if (vrsOn && !perTile)
            {
                blockers.Add("VRS is enabled but this GPU/driver does not expose per-tile shading rate — foveation cannot run.");
            }
            else if (vrsOn && !live)
            {
                blockers.Add("VRS is enabled but the build pass has not dispatched recently — is the local player camera rendering, and does the renderer feature have its build shader assigned?");
            }
        }

        private static string RateListLabel()
        {
            ShadingRateFragmentSize[] sizes = ShadingRateInfo.availableFragmentSizes;
            if (sizes == null || sizes.Length == 0)
            {
                return "none reported";
            }
            string label = "";
            for (int i = 0; i < sizes.Length; i++)
            {
                if (i > 0) label += ", ";
                label += sizes[i].ToString().Replace("FragmentSize", "");
            }
            return label;
        }

        private void DrawBlockers(List<string> blockers)
        {
            Header("Blockers");
            if (blockers.Count == 0)
            {
                GUI.color = Green;
                EditorGUILayout.LabelField("● Pipeline healthy — no blockers.");
                GUI.color = Color.white;
                return;
            }
            for (int i = 0; i < blockers.Count; i++)
            {
                BasisEditorUI.Help(blockers[i], MessageType.Warning);
            }
        }

        private static void Header(string text)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
        }

        private static void Dot(string label, bool ok)
        {
            Color prev = GUI.color;
            GUI.color = ok ? Green : Red;
            EditorGUILayout.LabelField($"{(ok ? "●" : "○")}  {label}");
            GUI.color = prev;
        }

        private static void Field(string label, string value)
        {
            Color prev = GUI.color;
            GUI.color = Grey;
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(string.IsNullOrEmpty(label) ? value : $"{label}: {value}", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
            GUI.color = prev;
        }

        private static string DeviceLabel(IBasisEyeTrackingProvider provider)
        {
            string type = provider.GetType().Name;
            if (type.Contains("OpenXR")) return "OpenXR (HMD eye gaze)";
            if (type.Contains("OpenVR")) return "OpenVR (HMD eye gaze)";
            if (type.Contains("Osc")) return "OSC (face tracking)";
            return $"{type} ({provider.Source})";
        }

        private static string Fmt(float3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";

        private static string Ang(float2 yawPitch) =>
            $"yaw {math.degrees(yawPitch.x):F1}°, pitch {math.degrees(yawPitch.y):F1}°";
    }
}
