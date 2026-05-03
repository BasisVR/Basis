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

        private Vector3 _lastA;
        private Vector3 _lastB;
        private bool _hasLastPositions;
        private float _emaVelA;
        private float _emaVelB;
        private float _expectedDistance;
        // Smoothed confidence weights, updated each frame from instantaneous
        // surprise. Smoothing is what stops the midpoint from snapping when a
        // moving tracker briefly looks surprising relative to its at-rest baseline.
        private float _smoothedWA = 1f;
        private float _smoothedWB = 1f;

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

            string id = "midpoint:" + (a?.UniqueDeviceIdentifier ?? "?") + "|" + (b?.UniqueDeviceIdentifier ?? "?");
            InitalizeTracking(id, "VirtualMidpoint", "BasisTrackerPairing", false, BasisBoneTrackedRole.CenterEye);

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

            Vector3 mid;
            Quaternion midRot;

            if (!_hasLastPositions)
            {
                _expectedDistance = Vector3.Distance(a, b);
                _emaVelA = 0f;
                _emaVelB = 0f;
                _smoothedWA = 1f;
                _smoothedWB = 1f;
                _hasLastPositions = true;
                mid = (a + b) * 0.5f;
                midRot = SafeSlerp(aRot, bRot, 0.5f);
            }
            else
            {
                // Read the tuning knobs once per frame. Cheap field reads but
                // means the user can tweak the sliders mid-session and the next
                // frame picks up the new values.
                float surprisePenalty = BasisSettingsDefaults.PairingSurprisePenalty.RawValue;
                float surpriseClamp = BasisSettingsDefaults.PairingSurpriseClamp.RawValue;
                float emaFloor = BasisSettingsDefaults.PairingEmaFloor.RawValue;
                float maxCorrectionStrength = BasisSettingsDefaults.PairingMaxCorrectionStrength.RawValue;
                float softSnapHalfLife = Mathf.Max(BasisSettingsDefaults.PairingSoftSnapHalfLife.RawValue, 1e-4f);
                float lockstepTolerance = BasisSettingsDefaults.PairingLockstepTolerance.RawValue;
                float emaAlpha = BasisSettingsDefaults.PairingEmaAlpha.RawValue;
                float distanceEmaAlpha = BasisSettingsDefaults.PairingDistanceEmaAlpha.RawValue;
                float weightSmoothing = Mathf.Clamp01(BasisSettingsDefaults.PairingWeightSmoothing.RawValue);

                float velA = Vector3.Distance(a, _lastA);
                float velB = Vector3.Distance(b, _lastB);

                // Shared baseline: judge BOTH trackers against the higher of the
                // two recent EMAs (plus floor) rather than each against its own.
                // A rigid pair should produce similar velocities; if one tracker
                // sits at zero (frozen / disconnected feed) and the other moves
                // normally, the moving partner's EMA dominates the baseline so
                // the still tracker doesn't get a "low baseline → easy to look
                // surprising" effect, and a real bilateral motion onset doesn't
                // fool both trackers into looking surprising to themselves.
                float sharedBaseline = Mathf.Max(_emaVelA, _emaVelB) + emaFloor;
                float surpriseA = velA / sharedBaseline;
                float surpriseB = velB / sharedBaseline;
                float excessA = Mathf.Max(0f, surpriseA - 1f);
                float excessB = Mathf.Max(0f, surpriseB - 1f);
                float instantWA = 1f / (1f + excessA * excessA * surprisePenalty);
                float instantWB = 1f / (1f + excessB * excessB * surprisePenalty);

                // Smooth the weights themselves. Without this, the per-frame
                // surprise score swings rapidly during normal motion (each step
                // is a brief deviation from the rest baseline) and the midpoint
                // snaps as the blend ratio jumps. The smoothed weight responds
                // over a handful of frames — fast enough to catch real glitches,
                // slow enough that the midpoint stays steady through ordinary
                // movement.
                _smoothedWA = Mathf.Lerp(_smoothedWA, instantWA, weightSmoothing);
                _smoothedWB = Mathf.Lerp(_smoothedWB, instantWB, weightSmoothing);
                float wA = _smoothedWA;
                float wB = _smoothedWB;

                float currentDistance = Vector3.Distance(a, b);
                float distanceError = currentDistance - _expectedDistance;
                float absDistanceError = Mathf.Abs(distanceError);

                // Soft pull toward the rigid rest-distance solution. Strength
                // ramps in continuously with the divergence — zero at lockstep,
                // asymptoting to maxCorrectionStrength as the error grows.
                // Disabled when weights are imbalanced: the symmetric pull would
                // drag the trusted tracker toward the glitching one's wrong
                // position. When a glitch is in progress the weight asymmetry
                // already handles authority, no need to also nudge the trusted
                // half.
                float weightSum = Mathf.Max(wA + wB, 1e-6f);
                float weightBalance = 4f * wA * wB / (weightSum * weightSum); // 1 when wA==wB, → 0 as one dominates
                float correctionStrength = maxCorrectionStrength
                                           * weightBalance
                                           * (1f - 1f / (1f + absDistanceError / softSnapHalfLife));
                Vector3 dir = currentDistance > 1e-6f ? (b - a) / currentDistance : Vector3.zero;
                Vector3 aIdeal = a + dir * (distanceError * 0.5f);
                Vector3 bIdeal = b - dir * (distanceError * 0.5f);
                Vector3 aSoft = Vector3.Lerp(a, aIdeal, correctionStrength);
                Vector3 bSoft = Vector3.Lerp(b, bIdeal, correctionStrength);

                // Confidence-weighted blend. Both halves contribute every frame
                // with weights that smoothly favor whichever is moving more
                // consistently. In equal-confidence lockstep this is the same
                // 50/50 midpoint as before, just without the binary fallback.
                float tB = wB / weightSum;
                mid = aSoft * (1f - tB) + bSoft * tB;
                midRot = SafeSlerp(aRot, bRot, tB);

                // Update the velocity EMA only when the current sample isn't
                // a clear outlier. Otherwise a sustained glitch would silently
                // drag the baseline up and stop being detected.
                if (surpriseA <= surpriseClamp) _emaVelA = Mathf.Lerp(_emaVelA, velA, emaAlpha);
                if (surpriseB <= surpriseClamp) _emaVelB = Mathf.Lerp(_emaVelB, velB, emaAlpha);

                // Update the rest-distance EMA only when both trackers look
                // healthy AND the pair is inside lockstep tolerance — otherwise
                // a divergence would slowly shift our notion of the rigid
                // separation and the soft snap would lose its anchor.
                if (surpriseA <= surpriseClamp && surpriseB <= surpriseClamp && absDistanceError < lockstepTolerance)
                {
                    _expectedDistance = Mathf.Lerp(_expectedDistance, currentDistance, distanceEmaAlpha);
                }
            }

            _lastA = a;
            _lastB = b;

            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, mid);
            UnscaledDeviceCoord.rotation = midRot;
            ConvertToScaledDeviceCoord();
            ControlOnlyAsDevice();
            UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: false);
        }

        // Quaternion.Slerp is undefined if either input is the zero quaternion
        // (default-initialized BasisCalibratedCoords ships with rotation =
        // (0,0,0,0)), so guard against that. The blend ratio is the relative
        // confidence weight of B — at 0.5 it's the geometric midpoint rotation.
        private static Quaternion SafeSlerp(Quaternion a, Quaternion b, float t)
        {
            if (a.x == 0f && a.y == 0f && a.z == 0f && a.w == 0f) a = Quaternion.identity;
            if (b.x == 0f && b.y == 0f && b.z == 0f && b.w == 0f) b = Quaternion.identity;
            return Quaternion.Slerp(a, b, t);
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
