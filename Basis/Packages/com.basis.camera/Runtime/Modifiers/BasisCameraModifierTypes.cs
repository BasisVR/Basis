using System;

namespace Basis.Cinematics
{
    /// <summary>
    /// What decides where the camera sits. Exactly one is fitted at a time, so nothing can ever
    /// contend for the position channel.
    /// </summary>
    public enum BasisCameraPositionModifier
    {
        /// <summary>The operator drives. Sticks, mouse and keyboard move the camera themselves.</summary>
        FreeFly = 0,
        /// <summary>Hold an offset from the subject.</summary>
        FollowSubject = 1,
        /// <summary>Ride a ring around the subject, sweeping between a low, waist and high rig.</summary>
        Orbit = 2,
        /// <summary>Ride the track built from placed waypoints.</summary>
        DollyTrack = 3,
        /// <summary>Dolly in or out to hold the subject at a chosen size in frame.</summary>
        FrameSubject = 4,
        /// <summary>Stay where it was put — a locked-off tripod.</summary>
        LockedOff = 5,
    }

    /// <summary>
    /// What decides who the camera is filming. Every position and rotation modifier resolves its
    /// subject through this one, so it is a slot in its own right rather than a setting on each.
    /// </summary>
    public enum BasisCameraSubjectModifier
    {
        /// <summary>One player: yourself, or whoever is picked from the instance.</summary>
        FollowPlayer = 0,
        /// <summary>Everyone listed at once, framed as a single bounded subject.</summary>
        TargetGroup = 1,
        /// <summary>A fixed point in the world, for filming a place rather than a person.</summary>
        FixedPoint = 2,
        /// <summary>Nobody. Modifiers that need a subject hold wherever they were.</summary>
        None = 3,
    }

    public enum BasisCameraAimPoint
    {
        Normal = 0,
        Head = 1,
        FullBody = 2,
    }

    /// <summary>How the dolly modifier decides where along its track the camera sits.</summary>
    public enum BasisCameraDollyMode
    {
        /// <summary>You place it. The playhead is a slider.</summary>
        Manual = 0,
        /// <summary>Slides to whichever part of the track is nearest the subject.</summary>
        FollowSubject = 1,
        /// <summary>Runs along the track under the transport controls.</summary>
        Play = 2,
    }

    /// <summary>What decides where the camera points. Exactly one is fitted at a time.</summary>
    public enum BasisCameraRotationModifier
    {
        /// <summary>Keep whatever rotation it already had.</summary>
        Hold = 0,
        /// <summary>Subject dead centre, every frame.</summary>
        LookAtSubject = 1,
        /// <summary>Screen composition with a dead zone and a soft zone.</summary>
        Compose = 2,
        /// <summary>Copy the subject's own facing, for over-the-shoulder shots.</summary>
        MatchSubject = 3,
        /// <summary>Point down the dolly track, so the shot looks the way the move is travelling.</summary>
        AimAlongTrack = 4,
    }

    /// <summary>
    /// An extra applied on top of the two slots. Each may be fitted at most once, and each runs at
    /// its own fixed stage rather than wherever it sits in the list.
    /// </summary>
    public enum BasisCameraEffectModifier
    {
        /// <summary>Lead a moving subject so they are not dragged behind.</summary>
        LookAhead = 0,
        /// <summary>Pull in when something solid comes between the camera and the subject.</summary>
        AvoidOcclusion = 1,
        /// <summary>Positional and rotational wander, for a handheld or airborne character.</summary>
        Shake = 2,
        /// <summary>Drive the field of view from the stack instead of the operator's slider.</summary>
        LensOverride = 3,
        /// <summary>Settle the subject before anything films them, so head bob does not reach the shot.</summary>
        SteadySubject = 4,
        /// <summary>Stop the camera travelling through anything solid on its way to where it is going.</summary>
        AvoidCollision = 5,
        /// <summary>Hold the subject the same size while the camera moves, so the world stretches behind them.</summary>
        DollyZoom = 6,
        /// <summary>Give the rig weight, so a fast move carries past the mark and settles back onto it.</summary>
        RigWeight = 7,
    }

    /// <summary>Which part of the pose a modifier writes. Shown as a badge beside its name.</summary>
    [Flags]
    public enum BasisCameraModifierChannel
    {
        None = 0,
        Position = 1 << 0,
        Rotation = 1 << 1,
        Lens = 1 << 2,
        Both = Position | Rotation,
    }

    /// <summary>
    /// When an effect runs. Fixed per effect, not authored: occlusion has to see the solved
    /// position and shake has to be the last thing on top of it, and no ordering an operator could
    /// pick would improve on that.
    /// </summary>
    public enum BasisCameraEffectStage
    {
        /// <summary>Before either slot solves, altering the subject they both read.</summary>
        Subject = 0,
        /// <summary>After the position slot, before the rotation slot.</summary>
        Position = 1,
        /// <summary>Alongside the lens.</summary>
        Lens = 2,
        /// <summary>Last, on the finished pose.</summary>
        Output = 3,
    }

    public readonly struct BasisCameraEffectDescriptor
    {
        public readonly BasisCameraEffectModifier Effect;
        public readonly BasisCameraModifierChannel Channel;
        public readonly BasisCameraEffectStage Stage;
        public readonly string NameKey;
        public readonly string DescriptionKey;

        public BasisCameraEffectDescriptor(BasisCameraEffectModifier effect, BasisCameraModifierChannel channel,
            BasisCameraEffectStage stage, string nameKey, string descriptionKey)
        {
            Effect = effect;
            Channel = channel;
            Stage = stage;
            NameKey = nameKey;
            DescriptionKey = descriptionKey;
        }
    }

    public static class BasisCameraModifiers
    {
        public const string SubjectSlotKey = "camera.modifier.subject";
        public const string PositionSlotKey = "camera.modifier.position";
        public const string RotationSlotKey = "camera.modifier.rotation";
        public const string EffectsKey = "camera.modifier.effects";

        public static readonly BasisCameraSubjectModifier[] SubjectModifiers =
        {
            BasisCameraSubjectModifier.FollowPlayer,
            BasisCameraSubjectModifier.TargetGroup,
            BasisCameraSubjectModifier.FixedPoint,
            BasisCameraSubjectModifier.None,
        };

        public static readonly BasisCameraAimPoint[] AimPoints =
        {
            BasisCameraAimPoint.Normal,
            BasisCameraAimPoint.Head,
            BasisCameraAimPoint.FullBody,
        };

        public static readonly BasisCameraPositionModifier[] PositionModifiers =
        {
            BasisCameraPositionModifier.FreeFly,
            BasisCameraPositionModifier.FollowSubject,
            BasisCameraPositionModifier.Orbit,
            BasisCameraPositionModifier.DollyTrack,
            BasisCameraPositionModifier.FrameSubject,
            BasisCameraPositionModifier.LockedOff,
        };

        public static readonly BasisCameraRotationModifier[] RotationModifiers =
        {
            BasisCameraRotationModifier.Hold,
            BasisCameraRotationModifier.LookAtSubject,
            BasisCameraRotationModifier.Compose,
            BasisCameraRotationModifier.MatchSubject,
            BasisCameraRotationModifier.AimAlongTrack,
        };

        public static readonly BasisCameraEffectDescriptor[] Effects =
        {
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.SteadySubject, BasisCameraModifierChannel.Both,
                BasisCameraEffectStage.Subject, "camera.modifier.steadySubject", "camera.modifier.steadySubject.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.LookAhead, BasisCameraModifierChannel.Both,
                BasisCameraEffectStage.Subject, "camera.modifier.lookAhead", "camera.modifier.lookAhead.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.AvoidOcclusion, BasisCameraModifierChannel.Position,
                BasisCameraEffectStage.Position, "camera.modifier.avoidOcclusion", "camera.modifier.avoidOcclusion.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.AvoidCollision, BasisCameraModifierChannel.Position,
                BasisCameraEffectStage.Position, "camera.modifier.avoidCollision", "camera.modifier.avoidCollision.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.LensOverride, BasisCameraModifierChannel.Lens,
                BasisCameraEffectStage.Lens, "camera.modifier.lensOverride", "camera.modifier.lensOverride.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.DollyZoom, BasisCameraModifierChannel.Lens,
                BasisCameraEffectStage.Lens, "camera.modifier.dollyZoom", "camera.modifier.dollyZoom.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.RigWeight, BasisCameraModifierChannel.Rotation,
                BasisCameraEffectStage.Output, "camera.modifier.rigWeight", "camera.modifier.rigWeight.description"),
            new BasisCameraEffectDescriptor(BasisCameraEffectModifier.Shake, BasisCameraModifierChannel.Both,
                BasisCameraEffectStage.Output, "camera.modifier.shake", "camera.modifier.shake.description"),
        };

        /// <summary>
        /// Whether the effect has nothing to work on without a subject. The fitted control still
        /// shows — it is the subject slot that is missing, not the setting — but the solve skips it.
        /// </summary>
        public static bool NeedsSubject(BasisCameraEffectModifier effect)
        {
            switch (effect)
            {
                case BasisCameraEffectModifier.SteadySubject:
                case BasisCameraEffectModifier.LookAhead:
                case BasisCameraEffectModifier.AvoidOcclusion:
                case BasisCameraEffectModifier.DollyZoom:
                    return true;
                default:
                    return false;
            }
        }

        public static string NameKey(BasisCameraSubjectModifier modifier)
        {
            switch (modifier)
            {
                case BasisCameraSubjectModifier.TargetGroup: return "camera.modifier.targetGroup";
                case BasisCameraSubjectModifier.FixedPoint: return "camera.modifier.fixedPoint";
                case BasisCameraSubjectModifier.None: return "camera.modifier.noSubject";
                default: return "camera.modifier.followPlayer";
            }
        }

        public static string NameKey(BasisCameraAimPoint aim)
        {
            switch (aim)
            {
                case BasisCameraAimPoint.Head: return "camera.aimPoint.head";
                case BasisCameraAimPoint.FullBody: return "camera.aimPoint.fullBody";
                default: return "camera.aimPoint.normal";
            }
        }

        public static string NameKey(BasisCameraPositionModifier modifier)
        {
            switch (modifier)
            {
                case BasisCameraPositionModifier.FollowSubject: return "camera.modifier.followSubject";
                case BasisCameraPositionModifier.Orbit: return "camera.modifier.orbit";
                case BasisCameraPositionModifier.DollyTrack: return "camera.modifier.dollyTrack";
                case BasisCameraPositionModifier.FrameSubject: return "camera.modifier.frameSubject";
                case BasisCameraPositionModifier.LockedOff: return "camera.modifier.lockedOff";
                default: return "camera.modifier.freeFly";
            }
        }

        public static string NameKey(BasisCameraRotationModifier modifier)
        {
            switch (modifier)
            {
                case BasisCameraRotationModifier.LookAtSubject: return "camera.modifier.lookAtSubject";
                case BasisCameraRotationModifier.Compose: return "camera.modifier.compose";
                case BasisCameraRotationModifier.MatchSubject: return "camera.modifier.matchSubject";
                case BasisCameraRotationModifier.AimAlongTrack: return "camera.modifier.aimAlongTrack";
                default: return "camera.modifier.hold";
            }
        }

        public static string DescriptionKey(BasisCameraSubjectModifier modifier)
            => NameKey(modifier) + ".description";

        public static string DescriptionKey(BasisCameraPositionModifier modifier)
            => NameKey(modifier) + ".description";

        public static string DescriptionKey(BasisCameraRotationModifier modifier)
            => NameKey(modifier) + ".description";

        public static bool TryGetDescriptor(BasisCameraEffectModifier effect, out BasisCameraEffectDescriptor descriptor)
        {
            for (int Index = 0; Index < Effects.Length; Index++)
            {
                if (Effects[Index].Effect == effect)
                {
                    descriptor = Effects[Index];
                    return true;
                }
            }
            descriptor = default;
            return false;
        }

        public static string NameKey(BasisCameraEffectModifier effect)
            => TryGetDescriptor(effect, out BasisCameraEffectDescriptor descriptor) ? descriptor.NameKey : string.Empty;

        public static string DescriptionKey(BasisCameraEffectModifier effect)
            => TryGetDescriptor(effect, out BasisCameraEffectDescriptor descriptor) ? descriptor.DescriptionKey : string.Empty;

        public static BasisCameraEffectStage StageOf(BasisCameraEffectModifier effect)
            => TryGetDescriptor(effect, out BasisCameraEffectDescriptor descriptor) ? descriptor.Stage : BasisCameraEffectStage.Output;

        public static BasisCameraModifierChannel ChannelOf(BasisCameraEffectModifier effect)
            => TryGetDescriptor(effect, out BasisCameraEffectDescriptor descriptor) ? descriptor.Channel : BasisCameraModifierChannel.None;

        /// <summary>Whether the position slot takes the channel off the operator.</summary>
        public static bool DrivesPosition(BasisCameraPositionModifier modifier)
            => modifier != BasisCameraPositionModifier.FreeFly;

        /// <summary>Whether the rotation slot takes the channel off the operator.</summary>
        public static bool DrivesRotation(BasisCameraRotationModifier modifier)
            => modifier != BasisCameraRotationModifier.Hold;

        /// <summary>Whether the fitted subject slot resolves to anybody at all.</summary>
        public static bool ResolvesSubject(BasisCameraSubjectModifier modifier)
            => modifier != BasisCameraSubjectModifier.None;

        /// <summary>Whether the fitted slot needs somebody to film.</summary>
        public static bool NeedsSubject(BasisCameraPositionModifier modifier)
        {
            switch (modifier)
            {
                case BasisCameraPositionModifier.FollowSubject:
                case BasisCameraPositionModifier.Orbit:
                case BasisCameraPositionModifier.FrameSubject:
                    return true;
                default:
                    return false;
            }
        }

        public static bool NeedsSubject(BasisCameraRotationModifier modifier)
        {
            switch (modifier)
            {
                case BasisCameraRotationModifier.LookAtSubject:
                case BasisCameraRotationModifier.Compose:
                case BasisCameraRotationModifier.MatchSubject:
                    return true;
                default:
                    return false;
            }
        }
    }
}
