using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Cinematics
{
    /// <summary>
    /// How the camera is driven: one modifier in the position slot, one in the rotation slot, and
    /// any number of effects on top. Every modifier carries its own settings block, so a setting is
    /// only ever reachable while the modifier that reads it is fitted — no control in the panel can
    /// be live and inert at the same time.
    /// </summary>
    [Serializable]
    public class BasisCameraModifierStack
    {
        public const int MaxEffects = 8;

        /// <summary>
        /// False on a stack Unity deserialized from a component that had never stored one. Unity
        /// fills a serialized class field by type default rather than by constructor, so without
        /// this the defaults below would arrive as zeroes — a follow offset of (0,0,0) and a
        /// teleport distance of 0, which parks the camera inside the subject and snaps every frame.
        /// </summary>
        [SerializeField] private bool configured;

        public BasisCameraSubjectSettings subject;

        public BasisCameraPositionModifier positionModifier;
        public BasisCameraRotationModifier rotationModifier;

        /// <summary>Fitted effects as <see cref="BasisCameraEffectModifier"/> values, in the order they were added.</summary>
        public List<int> effects = new List<int>();

        public BasisCameraFollowSettings follow;
        public BasisCameraOrbitSettings orbit;
        public BasisCameraDollySettings dolly;
        public BasisCameraFramingSettings framing;

        public BasisCameraLookAtSettings lookAt;
        public BasisCameraComposeSettings compose;
        public BasisCameraMatchSubjectSettings matchSubject;
        public BasisCameraTrackAimSettings trackAim;

        public BasisCameraLookAheadSettings lookAhead;
        public BasisCameraOcclusionSettings occlusion;
        public BasisCameraNoiseSettings shake;
        public BasisCameraLensSettings lens;
        public BasisCameraSteadySettings steady;
        public BasisCameraCollisionSettings collision;
        public BasisCameraDollyZoomSettings dollyZoom;
        public BasisCameraRigWeightSettings rigWeight;

        public BasisCameraModifierStack() => ResetToDefaults();

        public void ResetToDefaults()
        {
            configured = true;
            effects ??= new List<int>();
            effects.Clear();

            subject = BasisCameraSubjectSettings.Default;

            positionModifier = BasisCameraPositionModifier.FreeFly;
            rotationModifier = BasisCameraRotationModifier.Hold;

            follow = BasisCameraFollowSettings.Default;
            orbit = BasisCameraOrbitSettings.Default;
            dolly = BasisCameraDollySettings.Default;
            framing = BasisCameraFramingSettings.Default;

            lookAt = BasisCameraLookAtSettings.Default;
            compose = BasisCameraComposeSettings.Default;
            matchSubject = BasisCameraMatchSubjectSettings.Default;
            trackAim = BasisCameraTrackAimSettings.Default;

            lookAhead = BasisCameraLookAheadSettings.Default;
            occlusion = BasisCameraOcclusionSettings.Default;
            shake = BasisCameraNoiseSettings.ForProfile(BasisCameraNoiseProfile.Handheld);
            lens = BasisCameraLensSettings.Default;
            steady = BasisCameraSteadySettings.Default;
            collision = BasisCameraCollisionSettings.Default;
            dollyZoom = BasisCameraDollyZoomSettings.Default;
            rigWeight = BasisCameraRigWeightSettings.Default;
        }

        public bool DrivesPosition => BasisCameraModifiers.DrivesPosition(positionModifier);

        public bool DrivesRotation => BasisCameraModifiers.DrivesRotation(rotationModifier);

        /// <summary>Whether anything at all is fitted, so the camera knows to run the solve.</summary>
        public bool DrivesAnything => DrivesPosition || DrivesRotation || EffectCount > 0;

        /// <summary>Whether the subject slot resolves to anything at all.</summary>
        public bool ResolvesSubject => BasisCameraModifiers.ResolvesSubject(subject.modifier);

        /// <summary>Whether the fitted stack needs somebody to film.</summary>
        public bool NeedsSubject =>
            BasisCameraModifiers.NeedsSubject(positionModifier) ||
            BasisCameraModifiers.NeedsSubject(rotationModifier);

        public bool DrivesLens => HasEffect(BasisCameraEffectModifier.LensOverride) ||
            HasEffect(BasisCameraEffectModifier.DollyZoom) ||
            (positionModifier == BasisCameraPositionModifier.FrameSubject && framing.usesZoom);

        public int EffectCount => effects?.Count ?? 0;

        public bool HasEffect(BasisCameraEffectModifier effect)
        {
            if (effects == null)
            {
                return false;
            }
            for (int Index = 0; Index < effects.Count; Index++)
            {
                if (effects[Index] == (int)effect)
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryGetEffectAt(int index, out BasisCameraEffectModifier effect)
        {
            if (effects != null && index >= 0 && index < effects.Count &&
                Enum.IsDefined(typeof(BasisCameraEffectModifier), effects[index]))
            {
                effect = (BasisCameraEffectModifier)effects[index];
                return true;
            }
            effect = default;
            return false;
        }

        public bool AddEffect(BasisCameraEffectModifier effect)
        {
            effects ??= new List<int>();
            if (HasEffect(effect) || effects.Count >= MaxEffects ||
                !Enum.IsDefined(typeof(BasisCameraEffectModifier), (int)effect))
            {
                return false;
            }
            effects.Add((int)effect);
            return true;
        }

        public bool RemoveEffect(BasisCameraEffectModifier effect)
        {
            if (effects == null)
            {
                return false;
            }
            for (int Index = 0; Index < effects.Count; Index++)
            {
                if (effects[Index] == (int)effect)
                {
                    effects.RemoveAt(Index);
                    return true;
                }
            }
            return false;
        }

        public void ClearEffects() => effects?.Clear();

        /// <summary>
        /// Drops values a file cannot be trusted to hold: an unknown enum, a duplicated or unknown
        /// effect, or a damp time a hand-edited file has made negative.
        /// </summary>
        public void Sanitize()
        {
            if (!configured)
            {
                ResetToDefaults();
                return;
            }

            if (!Enum.IsDefined(typeof(BasisCameraSubjectModifier), subject.modifier))
            {
                subject.modifier = BasisCameraSubjectModifier.FollowPlayer;
            }
            if (!Enum.IsDefined(typeof(BasisCameraAimPoint), subject.aimPoint))
            {
                subject.aimPoint = BasisCameraAimPoint.Normal;
            }
            subject.framingRadius = Mathf.Max(0.05f, subject.framingRadius);

            if (!Enum.IsDefined(typeof(BasisCameraPositionModifier), positionModifier))
            {
                positionModifier = BasisCameraPositionModifier.FreeFly;
            }
            if (!Enum.IsDefined(typeof(BasisCameraRotationModifier), rotationModifier))
            {
                rotationModifier = BasisCameraRotationModifier.Hold;
            }

            effects ??= new List<int>();
            for (int Index = effects.Count - 1; Index >= 0; Index--)
            {
                bool valid = Enum.IsDefined(typeof(BasisCameraEffectModifier), effects[Index]);
                if (valid)
                {
                    for (int Earlier = 0; Earlier < Index; Earlier++)
                    {
                        if (effects[Earlier] == effects[Index])
                        {
                            valid = false;
                            break;
                        }
                    }
                }
                if (!valid)
                {
                    effects.RemoveAt(Index);
                }
            }
            while (effects.Count > MaxEffects)
            {
                effects.RemoveAt(effects.Count - 1);
            }

            if (!ResolvesSubject)
            {
                if (BasisCameraModifiers.NeedsSubject(positionModifier)) positionModifier = BasisCameraPositionModifier.FreeFly;
                if (BasisCameraModifiers.NeedsSubject(rotationModifier)) rotationModifier = BasisCameraRotationModifier.Hold;
                if (dolly.mode == BasisCameraDollyMode.FollowSubject) dolly.mode = BasisCameraDollyMode.Manual;
                for (int Index = effects.Count - 1; Index >= 0; Index--)
                {
                    if (BasisCameraModifiers.NeedsSubject((BasisCameraEffectModifier)effects[Index])) effects.RemoveAt(Index);
                }
            }
            else if (subject.modifier == BasisCameraSubjectModifier.FixedPoint && rotationModifier == BasisCameraRotationModifier.MatchSubject)
            {
                rotationModifier = BasisCameraRotationModifier.Hold;
            }

            follow.damping = ClampDamping(follow.damping);
            follow.lateralTracking = Mathf.Clamp01(follow.lateralTracking);
            follow.teleportDistance = Mathf.Max(0.1f, follow.teleportDistance);

            framing.damping = ClampDamping(framing.damping);
            framing.screenFraction = Mathf.Clamp(framing.screenFraction, 0.05f, 1f);
            framing.minDistance = Mathf.Max(0.05f, framing.minDistance);
            framing.maxDistance = Mathf.Max(framing.minDistance, framing.maxDistance);
            framing.teleportDistance = Mathf.Max(0.1f, framing.teleportDistance);

            dolly.damping = Mathf.Max(0f, dolly.damping);
            if (!Enum.IsDefined(typeof(BasisCameraDollyMode), dolly.mode))
            {
                dolly.mode = BasisCameraDollyMode.Manual;
            }
            if (!Enum.IsDefined(typeof(BasisCameraDollySync), dolly.syncMode))
            {
                dolly.syncMode = BasisCameraDollySync.LocalOnly;
            }
            if (!BasisCameraEasing.IsDefined(dolly.easeIn))
            {
                dolly.easeIn = BasisCameraEase.Linear;
            }
            if (!BasisCameraEasing.IsDefined(dolly.easeOut))
            {
                dolly.easeOut = BasisCameraEase.Linear;
            }
            dolly.easeInPortion = Mathf.Clamp(dolly.easeInPortion, 0f, BasisCameraDollySpeed.MaximumEasePortion);
            dolly.easeOutPortion = Mathf.Clamp(dolly.easeOutPortion, 0f, BasisCameraDollySpeed.MaximumEasePortion);

            lookAt.damping = ClampDamping(lookAt.damping);
            matchSubject.damping = ClampDamping(matchSubject.damping);
            trackAim.damping = ClampDamping(trackAim.damping);

            lookAhead.time = Mathf.Max(0f, lookAhead.time);
            lookAhead.limit = Mathf.Max(0f, lookAhead.limit);

            occlusion.padding = Mathf.Max(0f, occlusion.padding);
            occlusion.minDistance = Mathf.Max(0.05f, occlusion.minDistance);
            occlusion.returnDamping = Mathf.Max(0f, occlusion.returnDamping);
            occlusion.probeRadius = Mathf.Clamp(occlusion.probeRadius, 0.01f, 1f);

            lens.fov = Mathf.Clamp(lens.fov, 5f, 120f);
            lens.damping = Mathf.Max(0f, lens.damping);

            orbit.headingDamping = Mathf.Max(0f, orbit.headingDamping);
            orbit.verticalDamping = Mathf.Max(0f, orbit.verticalDamping);
            orbit.verticalAxis = Mathf.Clamp01(orbit.verticalAxis);

            steady.smoothing = Mathf.Max(0f, steady.smoothing);
            steady.verticalDeadZone = Mathf.Max(0f, steady.verticalDeadZone);

            collision.radius = Mathf.Clamp(collision.radius, 0.01f, 1f);
            collision.padding = Mathf.Max(0f, collision.padding);

            dollyZoom.minFov = Mathf.Clamp(dollyZoom.minFov, 5f, 120f);
            dollyZoom.maxFov = Mathf.Clamp(dollyZoom.maxFov, dollyZoom.minFov, 120f);

            rigWeight.responsiveness = Mathf.Clamp(rigWeight.responsiveness, 0.2f, 20f);
            rigWeight.bounce = Mathf.Clamp01(rigWeight.bounce);
        }

        private static Vector3 ClampDamping(Vector3 damping)
            => new Vector3(Mathf.Max(0f, damping.x), Mathf.Max(0f, damping.y), Mathf.Max(0f, damping.z));

        public BasisCameraModifierStack Clone()
        {
            BasisCameraModifierStack copy = (BasisCameraModifierStack)MemberwiseClone();
            copy.effects = effects == null ? new List<int>() : new List<int>(effects);
            return copy;
        }

        public void CopyFrom(BasisCameraModifierStack source)
        {
            if (source == null)
            {
                return;
            }

            configured = true;
            subject = source.subject;
            positionModifier = source.positionModifier;
            rotationModifier = source.rotationModifier;

            effects ??= new List<int>();
            effects.Clear();
            if (source.effects != null)
            {
                effects.AddRange(source.effects);
            }

            follow = source.follow;
            orbit = source.orbit;
            dolly = source.dolly;
            framing = source.framing;

            lookAt = source.lookAt;
            compose = source.compose;
            matchSubject = source.matchSubject;
            trackAim = source.trackAim;

            lookAhead = source.lookAhead;
            occlusion = source.occlusion;
            shake = source.shake;
            lens = source.lens;
            steady = source.steady;
            collision = source.collision;
            dollyZoom = source.dollyZoom;
            rigWeight = source.rigWeight;

            Sanitize();
        }

        /// <summary>
        /// Field-by-field equality, for the saved-mode drift check. Hand-written rather than
        /// reflective because it runs on the panel tick and a reflective walk would box every float.
        /// </summary>
        public static bool Matches(BasisCameraModifierStack a, BasisCameraModifierStack b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }

            if (a.positionModifier != b.positionModifier || a.rotationModifier != b.rotationModifier)
            {
                return false;
            }

            if (!MatchesSubject(a.subject, b.subject))
            {
                return false;
            }

            int countA = a.EffectCount;
            if (countA != b.EffectCount)
            {
                return false;
            }
            for (int Index = 0; Index < countA; Index++)
            {
                if (a.effects[Index] != b.effects[Index])
                {
                    return false;
                }
            }

            return MatchesFollow(a.follow, b.follow)
                && MatchesFraming(a.framing, b.framing)
                && MatchesDolly(a.dolly, b.dolly)
                && MatchesOrbit(a.orbit, b.orbit)
                && MatchesLookAt(a.lookAt, b.lookAt)
                && MatchesCompose(a.compose, b.compose)
                && MatchesMatchSubject(a.matchSubject, b.matchSubject)
                && MatchesTrackAim(a.trackAim, b.trackAim)
                && MatchesLookAhead(a.lookAhead, b.lookAhead)
                && MatchesOcclusion(a.occlusion, b.occlusion)
                && MatchesShake(a.shake, b.shake)
                && MatchesLens(a.lens, b.lens)
                && MatchesSteady(a.steady, b.steady)
                && MatchesCollision(a.collision, b.collision)
                && MatchesDollyZoom(a.dollyZoom, b.dollyZoom)
                && MatchesRigWeight(a.rigWeight, b.rigWeight);
        }

        private const float Epsilon = 0.0001f;

        private static bool Near(float a, float b) => Mathf.Abs(a - b) <= Epsilon;

        private static bool Near(Vector3 a, Vector3 b) => Near(a.x, b.x) && Near(a.y, b.y) && Near(a.z, b.z);

        private static bool MatchesSubject(in BasisCameraSubjectSettings a, in BasisCameraSubjectSettings b)
            => a.modifier == b.modifier && a.aimPoint == b.aimPoint && a.anchorToBody == b.anchorToBody &&
               a.groupIncludesLocal == b.groupIncludesLocal &&
               Near(a.aimHeightOffset, b.aimHeightOffset) && Near(a.framingRadius, b.framingRadius) &&
               Near(a.fixedPoint, b.fixedPoint);

        private static bool MatchesFollow(in BasisCameraFollowSettings a, in BasisCameraFollowSettings b)
            => Near(a.positionOffset, b.positionOffset) && a.bindingMode == b.bindingMode &&
               Near(a.damping, b.damping) && Near(a.lateralTracking, b.lateralTracking) &&
               Near(a.teleportDistance, b.teleportDistance);

        private static bool MatchesFraming(in BasisCameraFramingSettings a, in BasisCameraFramingSettings b)
            => Near(a.directionOffset, b.directionOffset) && a.bindingMode == b.bindingMode &&
               Near(a.damping, b.damping) && Near(a.screenFraction, b.screenFraction) &&
               Near(a.minDistance, b.minDistance) && Near(a.maxDistance, b.maxDistance) &&
               a.usesZoom == b.usesZoom && Near(a.teleportDistance, b.teleportDistance);

        private static bool MatchesDolly(in BasisCameraDollySettings a, in BasisCameraDollySettings b)
            => Near(a.position, b.position) && a.mode == b.mode && a.playing == b.playing &&
               a.syncMode == b.syncMode &&
               Near(a.damping, b.damping) &&
               Near(a.speed, b.speed) && Near(a.offset, b.offset) &&
               a.easeIn == b.easeIn && a.easeOut == b.easeOut &&
               Near(a.easeInPortion, b.easeInPortion) && Near(a.easeOutPortion, b.easeOutPortion);

        private static bool MatchesOrbit(in BasisCameraOrbitSettings a, in BasisCameraOrbitSettings b)
            => Near(a.top.height, b.top.height) && Near(a.top.radius, b.top.radius) &&
               Near(a.middle.height, b.middle.height) && Near(a.middle.radius, b.middle.radius) &&
               Near(a.bottom.height, b.bottom.height) && Near(a.bottom.radius, b.bottom.radius) &&
               Near(a.verticalAxis, b.verticalAxis) && Near(a.heading, b.heading) &&
               Near(a.headingDamping, b.headingDamping) && Near(a.verticalDamping, b.verticalDamping) &&
               a.followSubjectHeading == b.followSubjectHeading;

        private static bool MatchesLookAt(in BasisCameraLookAtSettings a, in BasisCameraLookAtSettings b)
            => Near(a.rotationOffset, b.rotationOffset) && Near(a.damping, b.damping);

        private static bool MatchesCompose(in BasisCameraComposeSettings a, in BasisCameraComposeSettings b)
            => Near(a.rotationOffset, b.rotationOffset) &&
               Near(a.composer.screenX, b.composer.screenX) && Near(a.composer.screenY, b.composer.screenY) &&
               Near(a.composer.deadZoneWidth, b.composer.deadZoneWidth) && Near(a.composer.deadZoneHeight, b.composer.deadZoneHeight) &&
               Near(a.composer.softZoneWidth, b.composer.softZoneWidth) && Near(a.composer.softZoneHeight, b.composer.softZoneHeight) &&
               Near(a.composer.biasX, b.composer.biasX) && Near(a.composer.biasY, b.composer.biasY) &&
               Near(a.composer.horizontalDamping, b.composer.horizontalDamping) &&
               Near(a.composer.verticalDamping, b.composer.verticalDamping);

        private static bool MatchesMatchSubject(in BasisCameraMatchSubjectSettings a, in BasisCameraMatchSubjectSettings b)
            => Near(a.rotationOffset, b.rotationOffset) && Near(a.damping, b.damping);

        private static bool MatchesTrackAim(in BasisCameraTrackAimSettings a, in BasisCameraTrackAimSettings b)
            => Near(a.rotationOffset, b.rotationOffset) && Near(a.damping, b.damping);

        private static bool MatchesLookAhead(in BasisCameraLookAheadSettings a, in BasisCameraLookAheadSettings b)
            => Near(a.time, b.time) && Near(a.limit, b.limit);

        private static bool MatchesOcclusion(in BasisCameraOcclusionSettings a, in BasisCameraOcclusionSettings b)
            => Near(a.padding, b.padding) && Near(a.minDistance, b.minDistance) &&
               Near(a.returnDamping, b.returnDamping) && Near(a.probeRadius, b.probeRadius);

        private static bool MatchesShake(in BasisCameraNoiseSettings a, in BasisCameraNoiseSettings b)
            => a.profile == b.profile && Near(a.amplitudeGain, b.amplitudeGain) && Near(a.frequencyGain, b.frequencyGain) &&
               Near(a.positionAmplitude, b.positionAmplitude) && Near(a.positionFrequency, b.positionFrequency) &&
               Near(a.rotationAmplitude, b.rotationAmplitude) && Near(a.rotationFrequency, b.rotationFrequency);

        private static bool MatchesLens(in BasisCameraLensSettings a, in BasisCameraLensSettings b)
            => Near(a.fov, b.fov) && Near(a.damping, b.damping);

        private static bool MatchesSteady(in BasisCameraSteadySettings a, in BasisCameraSteadySettings b)
            => Near(a.smoothing, b.smoothing) && Near(a.verticalDeadZone, b.verticalDeadZone);

        private static bool MatchesCollision(in BasisCameraCollisionSettings a, in BasisCameraCollisionSettings b)
            => Near(a.radius, b.radius) && Near(a.padding, b.padding);

        private static bool MatchesDollyZoom(in BasisCameraDollyZoomSettings a, in BasisCameraDollyZoomSettings b)
            => Near(a.minFov, b.minFov) && Near(a.maxFov, b.maxFov);

        private static bool MatchesRigWeight(in BasisCameraRigWeightSettings a, in BasisCameraRigWeightSettings b)
            => Near(a.responsiveness, b.responsiveness) && Near(a.bounce, b.bounce);
    }
}
