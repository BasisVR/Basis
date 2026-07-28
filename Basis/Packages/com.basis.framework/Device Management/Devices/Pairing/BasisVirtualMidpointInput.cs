using Basis.BasisUI;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Pairing
{
    /// <summary>
    /// Virtual <see cref="BasisInput"/> created by the pairing service when the user
    /// links two physical trackers together. Each frame it polls both partners,
    /// produces a single midpoint pose, and publishes that pose as if it were a
    /// real device. Calibration sees only this virtual sample for the pair (the
    /// physical bases are skipped via <see cref="BasisInput.IsLinked"/>), so a
    /// linked pair claims one body role together.
    ///
    /// The fusion is a continuous confidence-weighted blend rather than a hard
    /// "trust one, drop the other" choice. Each tracker carries a per-frame
    /// confidence derived from how surprising its current motion is relative to
    /// its recent baseline (a sudden velocity spike — typical of an occluded or
    /// glitching tracker — collapses its weight; smooth motion keeps it near 1).
    /// On top of that, both halves are softly pulled toward the calibrated rest
    /// distance whenever they drift apart, with strength that ramps continuously
    /// with the divergence so small flex/slop barely shifts anything but a real
    /// divergence is partly absorbed. Both sources always contribute, which gives
    /// the noise-averaging benefit of two measurements while still degrading
    /// gracefully when one half misbehaves.
    /// </summary>
    public class BasisVirtualMidpointInput : BasisInput
    {
        public BasisInput PartnerA;
        public BasisInput PartnerB;

        // All tunables for the fusion live as user settings on
        // BasisSettingsDefaults.Pairing*; see those for descriptions and ranges.

        // Per-frame fusion state (velocity EMAs, smoothed confidence weights, rest distance, half-rest offset,
        // rotation low-pass). The step itself lives in BasisMidpointFusionCore so the offline temporal sweep
        // runs the identical stateful math frame-by-frame with no scene.
        private BasisMidpointFusionState _fusion = BasisMidpointFusionState.Fresh();

        /// <summary>
        /// Set up this virtual as the merged proxy for the two partner trackers.
        /// Marks both partners as <see cref="BasisInput.IsLinked"/> so calibration
        /// skips them, and clears any FB role they had so the rig stops following
        /// either tracker individually until the user recalibrates.
        /// </summary>
        public void Initialize(BasisInput a, BasisInput b)
        {
            PartnerA = a;
            PartnerB = b;
            if (a != null) a.IsLinked = true;
            if (b != null) b.IsLinked = true;

            // The bases keep any device-matcher-pinned role (head, named hands)
            // because UnAssignFullBodyTrackers only touches free FB roles. If
            // they had a previous FB role from an earlier calibration pass,
            // it's cleared now so the user gets clean rig behavior between
            // pairing and the next calibrate.
            a?.UnAssignFullBodyTrackers();
            b?.UnAssignFullBodyTrackers();

            // Adopt PartnerA's parent so ApplyFinalMovement's SetLocalPositionAndRotation
            // lands the virtual in the same coordinate frame the partners use. The
            // static OffsetCoords transform is shared by every BasisInput, so as long
            // as we share a parent with at least one partner, the local-space pose we
            // hand to the transform is interpreted consistently.
            if (a != null && a.transform.parent != null)
            {
                transform.SetParent(a.transform.parent, worldPositionStays: false);
            }

            // The midpoint is only as steady as the noisier half it averages, so it inherits that half
            // rather than reading as an untyped virtual device.
            BasisTrackingHardware hardwareA = a != null ? a.TrackingHardware : BasisTrackingHardware.Unknown;
            BasisTrackingHardware hardwareB = b != null ? b.TrackingHardware : BasisTrackingHardware.Unknown;
            TrackingHardware = (byte)hardwareA >= (byte)hardwareB ? hardwareA : hardwareB;

            string id = "midpoint:" + (a?.UniqueDeviceIdentifier ?? "?") + "|" + (b?.UniqueDeviceIdentifier ?? "?");
            InitializeTracking(id, "VirtualMidpoint", "BasisTrackerPairing", false, BasisBoneTrackedRole.CenterEye);

            // Prime the pose so calibration (and any same-frame consumer) sees a
            // sensible position before the next AfterSimulateOnRender runs and
            // ApplyFinalMovement updates the transform on its own.
            LateDoPollData();
            transform.SetLocalPositionAndRotation(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation);
        }

        /// <summary>
        /// Reverse of <see cref="Initialize"/>: clear the linked flag on the
        /// partners and stop driving any role. The pairing service destroys the
        /// hosting GameObject after this returns.
        /// </summary>
        public void Teardown()
        {
            if (PartnerA != null)
            {
                PartnerA.IsLinked = false;
            }
            if (PartnerB != null)
            {
                PartnerB.IsLinked = false;
            }
            PartnerA = null;
            PartnerB = null;
            StopTracking();
        }

        public override void LateDoPollData()
        {
            if (PartnerA == null || PartnerB == null)
            {
                return;
            }

            Vector3 a = PartnerA.UnscaledDeviceCoord.position;
            Vector3 b = PartnerB.UnscaledDeviceCoord.position;
            Quaternion aRot = PartnerA.UnscaledDeviceCoord.rotation;
            Quaternion bRot = PartnerB.UnscaledDeviceCoord.rotation;

            BasisMidpointFusionCore.Step(ref _fusion, a, b, aRot, bRot,
                BasisMidpointFusionTunables.FromSettings(), Time.deltaTime,
                out Vector3 mid, out Quaternion midRot, out float wA, out float wB, out float blendT);

            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, mid);
            UnscaledDeviceCoord.rotation = midRot;
            ConvertToScaledDeviceCoord();
            ControlOnlyAsDevice();
            Basis.Scripts.Drivers.BasisPairingRotationRecorder.Sample(aRot, bRot, midRot, ScaledDeviceCoord.rotation, wA, wB, blendT);
            UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: false);
        }

        public override void ShowTrackedVisual()
        {
            // Virtual device — no model attached. The physical bases keep their
            // own visuals from their respective subsystems.
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
        }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
    }
}
