using NUnit.Framework;
using System;
using System.Collections.Generic;
using Basis.Scripts.Device_Management;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Basis.Scripts.Drivers;
namespace Basis.Tests.IK
{
    public class BasisPerSlotSmoothingTests
    {
        const float Dt = 1f / 90f;      // VR frame time
        const int WarmupFrames = 90;    // one second: let the euro state settle before measuring
        const int MeasureFrames = 270;  // three seconds of steady-state
        // Residual tracking noise of a player trying to hold still: zero-mean, high frequency. The derivative
        // low-pass sees ~0 mean velocity, so the cutoff stays at its floor and the signal is heavily smoothed.
        static float Jitter(float t) => 0.01f * (0.6f * Mathf.Sin(2f * Mathf.PI * 5f * t) + 0.4f * Mathf.Sin(2f * Mathf.PI * 11f * t + 1.3f));
        // Heavy = IMU-class tracker (low cutoff floor). Light = lighthouse-class (high floor, near passthrough).
        static readonly float4 HeavyTuning = new float4(0.5f, 0f, 1f, 0.08f);
        static readonly float4 LightTuning = new float4(20f, 0f, 3f, 0.85f);
        const int SlotHeavy = 0, SlotLight = 1, SlotCount = 2;
        static float RunPositionFilter(byte mode, float4 heavyTuning, float4 lightTuning, int readSlot)
        {
            var modes = new NativeArray<byte>(SlotCount, Allocator.TempJob);
            var tuning = new NativeArray<float4>(SlotCount, Allocator.TempJob);
            var inputs = new NativeArray<float3>(SlotCount, Allocator.TempJob);
            var outputs = new NativeArray<float3>(SlotCount, Allocator.TempJob);
            var euroStates = new NativeArray<BasisEuroVec3State>(SlotCount, Allocator.TempJob);
            var fallbackStates = new NativeArray<float3>(SlotCount, Allocator.TempJob);

            modes[SlotHeavy] = mode;
            modes[SlotLight] = mode;
            tuning[SlotHeavy] = heavyTuning;
            tuning[SlotLight] = lightTuning;

            float min = float.MaxValue, max = float.MinValue;

            try
            {
                for (int frame = 0; frame < WarmupFrames + MeasureFrames; frame++)
                {
                    // Both slots see the IDENTICAL signal -- any divergence is the tuning, nothing else.
                    float noisy = Jitter(frame * Dt);
                    inputs[SlotHeavy] = new float3(noisy, 0f, 0f);
                    inputs[SlotLight] = new float3(noisy, 0f, 0f);

                    var job = new BasisBatchPositionFilterJob
                    {
                        mode = modes,
                        rawInputs = inputs,
                        tuning = tuning,
                        euroStates = euroStates,
                        fallbackStates = fallbackStates,
                        outputs = outputs,
                        dt = Dt,
                        playspaceToWorld = float4x4.identity,
                    };
                    job.Run(SlotCount);

                    if (frame < WarmupFrames) continue;

                    float value = outputs[readSlot].x;
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
            }
            finally
            {
                modes.Dispose();
                tuning.Dispose();
                inputs.Dispose();
                outputs.Dispose();
                euroStates.Dispose();
                fallbackStates.Dispose();
            }

            return max - min;
        }
        [Test]
        public void EuroTuningIsPerSlot()
        {
            float heavyP2P = RunPositionFilter((byte)BasisFilterMode.Euro, HeavyTuning, LightTuning, SlotHeavy);
            float lightP2P = RunPositionFilter((byte)BasisFilterMode.Euro, HeavyTuning, LightTuning, SlotLight);

            // The heavily-tuned slot must be materially stiller than the lightly-tuned one in the SAME batch.
            // A 2x margin is well inside the measured gap and leaves room for Burst/mono float drift.
            Assert.Less(heavyP2P, lightP2P * 0.5f, $"per-slot euro tuning did not diverge: heavy p2p {heavyP2P:F6} vs light p2p {lightP2P:F6}");
        }
        [Test]
        public void FallbackAlphaIsPerSlot()
        {
            float heavyP2P = RunPositionFilter((byte)BasisFilterMode.Fallback, HeavyTuning, LightTuning, SlotHeavy);
            float lightP2P = RunPositionFilter((byte)BasisFilterMode.Fallback, HeavyTuning, LightTuning, SlotLight);

            Assert.Less(heavyP2P, lightP2P * 0.5f, $"per-slot fallback alpha did not diverge: heavy p2p {heavyP2P:F6} vs light p2p {lightP2P:F6}");
        }
        [Test]
        public void UniformTuningKeepsSlotsIdentical()
        {
            // The shipped default is one profile per group at the same values the single global setting used,
            // so identical tuning must still produce identical slots -- the defaults are a no-op.
            float slotA = RunPositionFilter((byte)BasisFilterMode.Euro, HeavyTuning, HeavyTuning, SlotHeavy);
            float slotB = RunPositionFilter((byte)BasisFilterMode.Euro, HeavyTuning, HeavyTuning, SlotLight);

            Assert.AreEqual(slotA, slotB, 1e-9f, $"uniform tuning diverged across slots: {slotA:F9} vs {slotB:F9}");
        }
        [Test]
        public void PassthroughIgnoresTuning()
        {
            float heavyP2P = RunPositionFilter((byte)BasisFilterMode.Passthrough, HeavyTuning, LightTuning, SlotHeavy);
            float lightP2P = RunPositionFilter((byte)BasisFilterMode.Passthrough, HeavyTuning, LightTuning, SlotLight);

            Assert.AreEqual(heavyP2P, lightP2P, 1e-9f,"Passthrough must emit the raw signal regardless of tuning");
        }
        [Test]
        public void RotationTuningIsPerSlot()
        {
            var modes = new NativeArray<byte>(SlotCount, Allocator.TempJob);
            var tuning = new NativeArray<float4>(SlotCount, Allocator.TempJob);
            var inputs = new NativeArray<quaternion>(SlotCount, Allocator.TempJob);
            var outputs = new NativeArray<quaternion>(SlotCount, Allocator.TempJob);
            var euroStates = new NativeArray<BasisEuroQuatState>(SlotCount, Allocator.TempJob);
            var fallbackStates = new NativeArray<quaternion>(SlotCount, Allocator.TempJob);

            modes[SlotHeavy] = (byte)BasisFilterMode.Euro;
            modes[SlotLight] = (byte)BasisFilterMode.Euro;
            tuning[SlotHeavy] = HeavyTuning;
            tuning[SlotLight] = LightTuning;
            fallbackStates[SlotHeavy] = quaternion.identity;
            fallbackStates[SlotLight] = quaternion.identity;

            float heavyMin = float.MaxValue, heavyMax = float.MinValue;
            float lightMin = float.MaxValue, lightMax = float.MinValue;

            try
            {
                for (int frame = 0; frame < WarmupFrames + MeasureFrames; frame++)
                {
                    // Same yaw jitter into both slots, scaled to a few degrees of shimmer.
                    quaternion noisy = quaternion.AxisAngle(math.up(), Jitter(frame * Dt) * 100f * Mathf.Deg2Rad);
                    inputs[SlotHeavy] = noisy;
                    inputs[SlotLight] = noisy;

                    var job = new BasisBatchRotationFilterJob
                    {
                        mode = modes,
                        rawInputs = inputs,
                        tuning = tuning,
                        euroStates = euroStates,
                        fallbackStates = fallbackStates,
                        outputs = outputs,
                        dt = Dt,
                        playspaceRotation = quaternion.identity,
                    };
                    job.Run(SlotCount);

                    if (frame < WarmupFrames) continue;

                    float heavyYaw = ((Quaternion)outputs[SlotHeavy]).eulerAngles.y;
                    float lightYaw = ((Quaternion)outputs[SlotLight]).eulerAngles.y;
                    if (heavyYaw > 180f) heavyYaw -= 360f;
                    if (lightYaw > 180f) lightYaw -= 360f;

                    if (heavyYaw < heavyMin) heavyMin = heavyYaw;
                    if (heavyYaw > heavyMax) heavyMax = heavyYaw;
                    if (lightYaw < lightMin) lightMin = lightYaw;
                    if (lightYaw > lightMax) lightMax = lightYaw;
                }
            }
            finally
            {
                modes.Dispose();
                tuning.Dispose();
                inputs.Dispose();
                outputs.Dispose();
                euroStates.Dispose();
                fallbackStates.Dispose();
            }

            float heavyP2P = heavyMax - heavyMin, lightP2P = lightMax - lightMin;

            Assert.Less(heavyP2P, lightP2P * 0.5f, $"per-slot euro tuning did not diverge on rotation: heavy p2p {heavyP2P:F4}deg vs light p2p {lightP2P:F4}deg");
        }
        [Test]
        public void EverySlotMapsToAValidGroup()
        {
            byte[] map = BasisSmoothingProfiles.SlotGroup;
            Assert.AreEqual(BasisLocalRigDriver.SlotCount, map.Length, "slot->group map must cover every filter slot");

            var seen = new bool[BasisSmoothingProfiles.GroupCount];
            for (int slot = 0; slot < map.Length; slot++)
            {
                Assert.Less(map[slot], BasisSmoothingProfiles.GroupCount, $"slot {slot} maps outside the group table");
                seen[map[slot]] = true;
            }

            // Every group must own at least one slot, or its UI row would be inert.
            for (int group = 0; group < seen.Length; group++)
            {
                Assert.IsTrue(seen[group], $"smoothing group {(BasisSmoothingGroup)group} owns no slot");
            }
        }
        [Test]
        public void PresetTableIsConsistent()
        {
            Assert.AreEqual(BasisSmoothingProfiles.PresetOrder.Length, BasisSmoothingProfiles.PresetLocalizationKeys.Length,"every preset needs a localization key");

            // Off must not resolve to a tuning curve -- it is a passthrough sentinel handled by the driver.
            Assert.IsTrue(BasisSmoothingProfiles.IsOff(BasisSmoothingProfiles.PresetOff));
            Assert.IsFalse(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetOff, out _));

            // Standard deliberately has no table entry: it falls through to the live global tuning statics.
            Assert.IsFalse(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetStandard, out _));

            // Custom is a sentinel too -- the driver swaps in the per-group sliders before ever consulting
            // the table. If it ever resolved here it would silently outrank the user's own values.
            Assert.IsTrue(BasisSmoothingProfiles.IsCustom(BasisSmoothingProfiles.PresetCustom));
            Assert.IsFalse(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetCustom, out _));
            Assert.IsFalse(BasisSmoothingProfiles.IsOff(BasisSmoothingProfiles.PresetCustom));
            Assert.IsFalse(BasisSmoothingProfiles.IsCustom(BasisSmoothingProfiles.PresetStandard));

            Assert.AreEqual(BasisSmoothingProfiles.PresetOrder.Length, new HashSet<string>(BasisSmoothingProfiles.PresetOrder).Count,"preset ids must be unique -- the dropdown resolves a selection back to a value by string match");

            Assert.IsTrue(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetLight, out var light));
            Assert.IsTrue(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetHeavy, out var heavy));
            Assert.IsTrue(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetOptical, out var optical));

            // Heavier presets must sit at a lower cutoff floor (stiller at rest) and lean harder on beta to
            // recover responsiveness during fast motion. If this ordering inverts, the names lie to the user.
            Assert.Less(heavy.MinCutoff, light.MinCutoff, "Heavy must filter harder at rest than Light");
            Assert.Less(optical.MinCutoff, heavy.MinCutoff, "Optical must filter harder at rest than Heavy");
            Assert.Greater(heavy.Beta, light.Beta, "Heavy needs more speed adaptation than Light");
            Assert.Greater(optical.Beta, heavy.Beta, "Optical needs more speed adaptation than Heavy");
        }
        [Test]
        public void AutoResolvesEachHardwareToASelectablePreset()
        {
            // Auto is a sentinel like Off and Custom -- the driver substitutes a real preset before the
            // table is ever consulted.
            Assert.IsTrue(BasisSmoothingProfiles.IsAuto(BasisSmoothingProfiles.PresetAuto));
            Assert.IsFalse(BasisSmoothingProfiles.TryGetPreset(BasisSmoothingProfiles.PresetAuto, out _));

            Assert.AreEqual(BasisSmoothingProfiles.PresetHeavy, BasisSmoothingProfiles.PresetForHardware(BasisTrackingHardware.Inertial),"IMU trackers drift and buzz -- filtering them lightly is the bug Auto exists to prevent");
            Assert.AreEqual(BasisSmoothingProfiles.PresetLight, BasisSmoothingProfiles.PresetForHardware(BasisTrackingHardware.Lighthouse));
            Assert.AreEqual(BasisSmoothingProfiles.PresetOptical, BasisSmoothingProfiles.PresetForHardware(BasisTrackingHardware.Optical));
            Assert.AreEqual(BasisSmoothingProfiles.PresetStandard, BasisSmoothingProfiles.PresetForHardware(BasisTrackingHardware.Unknown),"unidentified hardware must behave exactly as it did before Auto existed");

            foreach (BasisTrackingHardware hardware in Enum.GetValues(typeof(BasisTrackingHardware)))
            {
                string resolved = BasisSmoothingProfiles.PresetForHardware(hardware);
                CollectionAssert.Contains(BasisSmoothingProfiles.PresetOrder, resolved, $"{hardware} resolved to a preset that is not selectable");
                Assert.IsFalse(BasisSmoothingProfiles.IsAuto(resolved), $"{hardware} resolved Auto back to itself");
                Assert.IsFalse(BasisSmoothingProfiles.IsCustom(resolved), $"{hardware} handed the user's own sliders back as an automatic choice");
            }
        }
        [Test]
        public void TrackingHardwareIsOrderedByFilteringNeed()
        {
            Assert.Less((byte)BasisTrackingHardware.Unknown, (byte)BasisTrackingHardware.Simulated,"Unknown must lose to any device that identified itself");
            Assert.Less((byte)BasisTrackingHardware.Simulated, (byte)BasisTrackingHardware.Lighthouse);
            Assert.Less((byte)BasisTrackingHardware.Lighthouse, (byte)BasisTrackingHardware.InsideOut);
            Assert.Less((byte)BasisTrackingHardware.InsideOut, (byte)BasisTrackingHardware.Optical);
            Assert.Less((byte)BasisTrackingHardware.Optical, (byte)BasisTrackingHardware.Estimated);
            Assert.Less((byte)BasisTrackingHardware.Estimated, (byte)BasisTrackingHardware.Inertial);
        }
    }
}
