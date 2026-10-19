using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Editor.Localization;
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
        public override string Title => BasisEditorLocalization.Get("editor.eyePipeline.title");
        public override string Subtitle =>
            BasisEditorLocalization.Get("editor.eyePipeline.subtitle");

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
                BasisEditorUI.Help(BasisEditorLocalization.Get("editor.eyePipeline.playModeOnly"), MessageType.Info);
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
            Header(BasisEditorLocalization.Get("editor.eyePipeline.incoming"));

            IReadOnlyList<IBasisEyeTrackingProvider> providers = BasisEyeTrackingManager.Providers;
            if (providers == null || providers.Count == 0)
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eyePipeline.noProviders"));
                blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.noProviders"));
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
                            Field(BasisEditorLocalization.Get("editor.eyePipeline.worldRay"), $"o {Fmt(d.GazeOrigin)}  dir {Fmt(d.GazeDirection)}");
                        }
                        if (d.HasPerEyeAngles)
                        {
                            Field(BasisEditorLocalization.Get("editor.eyePipeline.perEyeAngles"), $"L {Ang(d.LeftAngles)}   R {Ang(d.RightAngles)}");
                        }
                        if (d.HasOpenness)
                        {
                            Field(BasisEditorLocalization.Get("editor.eyePipeline.openness"), $"L {d.LeftOpenness:F2}  R {d.RightOpenness:F2}");
                        }
                        if (!d.HasWorldRay && !d.HasPerEyeAngles && !d.HasOpenness)
                        {
                            Field("", BasisEditorLocalization.Get("editor.eyePipeline.noDataThisFrame"));
                        }
                    }
                }
                else
                {
                    string reason = provider.Source == BasisEyeSource.Hmd
                        ? BasisEditorLocalization.Get("editor.eyePipeline.reason.hmdInvalid")
                        : BasisEditorLocalization.Get("editor.eyePipeline.reason.oscInactive");
                    EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }

            if (hmdPresent && !hmdActive)
            {
                blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.hmdNotTracking"));
            }
            if (oscPresent && !oscActive)
            {
                blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.oscInactive"));
            }
        }

        private void DrawArbitration()
        {
            Header(BasisEditorLocalization.Get("editor.eyePipeline.arbitration"));

            BasisEyeTrackingManager.BasisEyeArbitration a = BasisEyeTrackingManager.Arbitration;

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyePipeline.preferredSource"), BasisEditorLocalization.Get(a.PreferOsc ? "editor.eyePipeline.sourceOsc" : "editor.eyePipeline.sourceHmd"));

            string gazeLabel = BasisEditorLocalization.Get(a.GazeFromOsc ? "editor.eyePipeline.gazeFromOsc" : a.GazeFromHmd ? "editor.eyePipeline.gazeFromHmd" : "editor.eyePipeline.gazeFromNone");
            Dot(gazeLabel, a.GazeFromOsc || a.GazeFromHmd);

            if (a.OscHasGaze && a.HmdHasGaze && !a.GazeFromOsc)
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eyePipeline.oscSuppressed"));
            }
            if (a.GazeFromOsc && a.HmdHasGaze)
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eyePipeline.hmdSuppressed"));
            }

            Dot(BasisEditorLocalization.Get(a.OpennessFromOsc ? "editor.eyePipeline.opennessFromOsc" : "editor.eyePipeline.opennessFromNone"), a.OpennessFromOsc);
        }

        private void DrawOutput()
        {
            Header(BasisEditorLocalization.Get("editor.eyePipeline.output"));

            BasisEyeTrackingData c = BasisEyeTrackingManager.Current;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.worldRay"), c.HasWorldRay);
            if (c.HasWorldRay)
            {
                Field("", $"o {Fmt(c.GazeOrigin)}  dir {Fmt(c.GazeDirection)}");
            }
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.perEyeAngles"), c.HasPerEyeAngles);
            if (c.HasPerEyeAngles)
            {
                Field("", $"L {Ang(c.LeftAngles)}   R {Ang(c.RightAngles)}");
            }
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.openness"), c.HasOpenness);
        }

        private void DrawConsumers(List<string> blockers)
        {
            Header(BasisEditorLocalization.Get("editor.eyePipeline.consumers"));

            bool hasEyeGaze = BasisLocalCameraDriver.HasEyeGaze;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.consumerVrs"), hasEyeGaze);
            if (!hasEyeGaze)
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eyePipeline.vrsCenterFallback"));
            }

            Dot(BasisEditorLocalization.Get("editor.eyePipeline.headsetPresent"), BasisEyeTrackingManager.HmdEyeTrackingPresent);
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.vrVrsEnabled"), BasisSettingsDefaults.DevVariableRateShading.RawValue);

            bool eyeEnabled = BasisLocalEyeDriver.IsEnabled;
            bool overrideOn = BasisLocalEyeDriver.Override;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.haiDriverEnabled"), eyeEnabled);
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.haiOverrideApplied"), overrideOn);

            BasisEyeTrackingData c = BasisEyeTrackingManager.Current;
            if (c.HasPerEyeAngles)
            {
                if (!eyeEnabled)
                {
                    blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.driverDisabled"));
                }
                else if (!overrideOn)
                {
                    blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.overrideOff"));
                }
            }
        }

        private void DrawFoveation(List<string> blockers)
        {
            Header(BasisEditorLocalization.Get("editor.eyePipeline.foveation"));

            bool api = BasisVariableRateShadingFeature.IsSupported;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.apiSupportsVrs"), api);
            if (!api)
            {
                BasisEditorUI.Note(BasisEditorLocalization.Get("editor.eyePipeline.vrsInactiveOtherApis"));
                return;
            }

            bool perTile = ShadingRateInfo.supportsPerImageTile;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.perTileRate"), perTile);
            Field(BasisEditorLocalization.Get("editor.eyePipeline.hardwareRates"), RateListLabel());

            bool vrsOn = BasisSettingsDefaults.DevVariableRateShading.RawValue || BasisSettingsDefaults.DevVariableRateShadingDesktop.RawValue;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.vrsEnabled"), vrsOn);

            bool live = BasisVariableRateShadingFeature.LastDispatchFrame > 0
                && Time.frameCount - BasisVariableRateShadingFeature.LastDispatchFrame <= 2;
            Dot(BasisEditorLocalization.Get("editor.eyePipeline.buildPassDispatching"), live);
            if (live)
            {
                Vector2Int tiles = BasisVariableRateShadingFeature.LastTiles;
                Field(BasisEditorLocalization.Get("editor.eyePipeline.tiles"), $"{tiles.x} x {tiles.y}");
                Field(BasisEditorLocalization.Get("editor.eyePipeline.gazeWeight"), BasisEditorLocalization.Get("editor.eyePipeline.gazeWeightValue", BasisVariableRateShadingFeature.LastGazeWeight));
                Field(BasisEditorLocalization.Get("editor.eyePipeline.buildGpuCost"), $"{BasisVariableRateShadingFeature.GpuMilliseconds:F3} ms");
            }

            if (vrsOn && !perTile)
            {
                blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.noPerTile"));
            }
            else if (vrsOn && !live)
            {
                blockers.Add(BasisEditorLocalization.Get("editor.eyePipeline.blocker.notDispatching"));
            }
        }

        private static string RateListLabel()
        {
            ShadingRateFragmentSize[] sizes = ShadingRateInfo.availableFragmentSizes;
            if (sizes == null || sizes.Length == 0)
            {
                return BasisEditorLocalization.Get("editor.eyePipeline.rateNone");
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
            Header(BasisEditorLocalization.Get("editor.eyePipeline.blockers"));
            if (blockers.Count == 0)
            {
                GUI.color = Green;
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.eyePipeline.healthy"));
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
            if (type.Contains("OpenXR")) return BasisEditorLocalization.Get("editor.eyePipeline.deviceOpenXr");
            if (type.Contains("OpenVR")) return BasisEditorLocalization.Get("editor.eyePipeline.deviceOpenVr");
            if (type.Contains("Osc")) return BasisEditorLocalization.Get("editor.eyePipeline.sourceOsc");
            return $"{type} ({provider.Source})";
        }

        private static string Fmt(float3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";

        private static string Ang(float2 yawPitch) =>
            BasisEditorLocalization.Get("editor.eyePipeline.angles", math.degrees(yawPitch.x), math.degrees(yawPitch.y));
    }
}
