using System;
using UnityEngine;

namespace Basis.Cinematics
{
    /// <summary>Which frame a positional offset is authored in.</summary>
    public enum BasisCameraBindingMode
    {
        /// <summary>Offset turns with the subject, so it stays in front as they turn.</summary>
        SubjectYaw = 0,
        /// <summary>Offset is fixed to world axes; the subject can turn without moving the camera.</summary>
        WorldSpace = 1,
        /// <summary>Hold the distance but keep the current heading, so the camera never swings around behind.</summary>
        SimpleFollow = 2,
    }

    [Serializable]
    public struct BasisCameraFollowSettings
    {
        [Tooltip("Offset from the subject in the binding frame, in metres at default avatar scale: X right, Y up, Z forward.")]
        public Vector3 positionOffset;

        public BasisCameraBindingMode bindingMode;

        [Tooltip("Seconds to catch up per axis of the binding frame: X sideways, Y vertical, Z on approach.")]
        public Vector3 damping;

        [Tooltip("How strongly sideways subject movement pulls the camera in to keep the shot tight. 0 holds the fixed side offset; 1 closes the side gap fully while strafing.")]
        [Range(0f, 1f)] public float lateralTracking;

        [Tooltip("Snap instead of easing when the target is further away than this, in metres at default avatar scale.")]
        public float teleportDistance;

        public static BasisCameraFollowSettings Default => new BasisCameraFollowSettings
        {
            positionOffset = new Vector3(0.5f, 0f, 1.4f),
            bindingMode = BasisCameraBindingMode.SubjectYaw,
            damping = new Vector3(0.35f, 0.5f, 0.5f),
            lateralTracking = 0.5f,
            teleportDistance = 10f,
        };
    }

    /// <summary>
    /// Who the camera films, as opposed to how it moves — the third slot on the stack. Every
    /// position and rotation modifier resolves its subject through this one, which is why it is a
    /// slot rather than a setting repeated on each of them.
    /// </summary>
    [Serializable]
    public struct BasisCameraSubjectSettings
    {
        public BasisCameraSubjectModifier modifier;

        [Tooltip("Anchor to the subject's centre of mass so room-scale movement keeps them in frame. Off anchors to the playspace origin, which is steadier but ignores physical walking.")]
        public bool anchorToBody;

        [Tooltip("Where on the subject the camera aims and focuses. Full Body also sizes the framing to their full height in place of the framing radius.")]
        public BasisCameraAimPoint aimPoint;

        [Tooltip("Shifts the aim point up or down from where Aim At puts it, in metres at default avatar scale. Negative aims lower down the body.")]
        public float aimHeightOffset;

        [Tooltip("Bounding radius assumed for one subject when framing, in metres at default scale.")]
        public float framingRadius;

        [Tooltip("Frame yourself as part of the group, alongside everyone else in it.")]
        public bool groupIncludesLocal;

        [Tooltip("The world point the camera films while Fixed Point is fitted.")]
        public Vector3 fixedPoint;

        public static BasisCameraSubjectSettings Default => new BasisCameraSubjectSettings
        {
            modifier = BasisCameraSubjectModifier.FollowPlayer,
            anchorToBody = true,
            aimPoint = BasisCameraAimPoint.Normal,
            aimHeightOffset = 0f,
            framingRadius = 0.45f,
            groupIncludesLocal = true,
            fixedPoint = Vector3.zero,
        };
    }

    [Serializable]
    public struct BasisCameraFramingSettings
    {
        [Tooltip("Direction the camera sits in from the subject. Its length is only a starting distance — the framing solve sets the real one.")]
        public Vector3 directionOffset;

        public BasisCameraBindingMode bindingMode;

        [Tooltip("Seconds to catch up per axis of the binding frame: X sideways, Y vertical, Z on approach.")]
        public Vector3 damping;

        [Tooltip("Share of the frame the subject should fill.")]
        [Range(0.05f, 1f)] public float screenFraction;

        public float minDistance;
        public float maxDistance;

        [Tooltip("Hold subject size by changing focal length instead of dollying.")]
        public bool usesZoom;

        [Tooltip("Snap instead of easing when the target is further away than this, in metres at default avatar scale.")]
        public float teleportDistance;

        public static BasisCameraFramingSettings Default => new BasisCameraFramingSettings
        {
            directionOffset = new Vector3(1.2f, 0.4f, 3f),
            bindingMode = BasisCameraBindingMode.SubjectYaw,
            damping = new Vector3(0.35f, 0.5f, 0.5f),
            screenFraction = 0.28f,
            minDistance = 0.6f,
            maxDistance = 12f,
            usesZoom = false,
            teleportDistance = 10f,
        };
    }

    /// <summary>How a dolly track is shared with the rest of the instance.</summary>
    public enum BasisCameraDollySync
    {
        /// <summary>Nobody else sees it. No traffic at all — the track is yours alone.</summary>
        LocalOnly = 0,
        /// <summary>Everyone sees it and anyone may pick a point up and move it.</summary>
        Networked = 1,
        /// <summary>Everyone sees it; only you may move the points.</summary>
        NetworkedLocked = 2,
    }

    [Serializable]
    public struct BasisCameraDollySettings
    {
        [Tooltip("Whether anyone else can see the track, and whether they may move its points.")]
        public BasisCameraDollySync syncMode;

        [Tooltip("Where on the track the camera sits, in waypoints. This is the playhead.")]
        public float position;

        public BasisCameraDollyMode mode;

        [Tooltip("Whether the move is running. Pausing holds the camera exactly where it had got to.")]
        public bool playing;

        [Tooltip("Seconds for the camera to catch up to its target point on the track.")]
        public float damping;

        [Tooltip("Metres per second the move travels along the track once it is up to speed.")]
        public float speed;

        [Tooltip("Curve the move gets up to speed on, off the waypoint it starts from.")]
        public BasisCameraEase easeIn;

        [Tooltip("How much of the track is spent getting up to speed.")]
        public float easeInPortion;

        [Tooltip("Curve the move comes off speed on, into the waypoint it ends at.")]
        public BasisCameraEase easeOut;

        [Tooltip("How much of the track is spent coming back down to a stop.")]
        public float easeOutPortion;

        [Tooltip("Offset from the track, in the track's own frame.")]
        public Vector3 offset;

        public static BasisCameraDollySettings Default => new BasisCameraDollySettings
        {
            position = 0f,
            syncMode = BasisCameraDollySync.LocalOnly,
            mode = BasisCameraDollyMode.Manual,
            playing = false,
            damping = 0.5f,
            speed = 1.5f,
            easeIn = BasisCameraEase.Sine,
            easeInPortion = 0.15f,
            easeOut = BasisCameraEase.Sine,
            easeOutPortion = 0.15f,
            offset = Vector3.zero,
        };
    }

    [Serializable]
    public struct BasisCameraLookAtSettings
    {
        [Tooltip("Extra rotation applied after aiming, in degrees.")]
        public Vector3 rotationOffset;

        [Tooltip("Seconds to catch up per rotation axis: X pitch, Y yaw, Z roll.")]
        public Vector3 damping;

        public static BasisCameraLookAtSettings Default => new BasisCameraLookAtSettings
        {
            rotationOffset = Vector3.zero,
            damping = new Vector3(0.4f, 0.35f, 0.8f),
        };
    }

    [Serializable]
    public struct BasisCameraComposeSettings
    {
        public BasisComposerSettings composer;

        [Tooltip("Extra rotation applied after aiming, in degrees.")]
        public Vector3 rotationOffset;

        public static BasisCameraComposeSettings Default => new BasisCameraComposeSettings
        {
            composer = BasisComposerSettings.Default,
            rotationOffset = Vector3.zero,
        };
    }

    [Serializable]
    public struct BasisCameraMatchSubjectSettings
    {
        [Tooltip("Extra rotation applied after matching, in degrees.")]
        public Vector3 rotationOffset;

        [Tooltip("Seconds to catch up per rotation axis: X pitch, Y yaw, Z roll.")]
        public Vector3 damping;

        public static BasisCameraMatchSubjectSettings Default => new BasisCameraMatchSubjectSettings
        {
            rotationOffset = Vector3.zero,
            damping = new Vector3(0.4f, 0.35f, 0.8f),
        };
    }

    /// <summary>
    /// Aims the camera down the dolly track it is riding, so the shot looks where the move is
    /// going. The one aim on the stack that films nothing: it reads the path rather than a subject,
    /// which is what a travelling shot down a corridor or along a wall wants.
    /// </summary>
    [Serializable]
    public struct BasisCameraTrackAimSettings
    {
        [Tooltip("Extra rotation applied after aiming down the track, in degrees. Yaw 180 looks back the way it came.")]
        public Vector3 rotationOffset;

        [Tooltip("Seconds to catch up per rotation axis: X pitch, Y yaw, Z roll.")]
        public Vector3 damping;

        public static BasisCameraTrackAimSettings Default => new BasisCameraTrackAimSettings
        {
            rotationOffset = Vector3.zero,
            damping = new Vector3(0.4f, 0.35f, 0.8f),
        };
    }

    [Serializable]
    public struct BasisCameraLookAheadSettings
    {
        [Tooltip("Seconds of subject motion to lead by.")]
        public float time;

        [Tooltip("Furthest the lead is allowed to reach, in metres.")]
        public float limit;

        public static BasisCameraLookAheadSettings Default => new BasisCameraLookAheadSettings
        {
            time = 0.25f,
            limit = 2f,
        };
    }

    /// <summary>
    /// Cleans up the subject before either slot reads them, as opposed to the damping on the slots
    /// themselves, which chases the camera to wherever the subject already is. A head-tracked
    /// subject is the jitteriest thing in a shot, and every modifier that aims at one inherits it.
    /// </summary>
    [Serializable]
    public struct BasisCameraSteadySettings
    {
        [Tooltip("Seconds of subject movement to average out before anything films them.")]
        public float smoothing;

        [Tooltip("How far the subject may move up and down before the shot follows, in metres at default avatar scale. Absorbs a jump or a crouch without the camera bobbing after it.")]
        public float verticalDeadZone;

        public static BasisCameraSteadySettings Default => new BasisCameraSteadySettings
        {
            smoothing = 0.18f,
            verticalDeadZone = 0.12f,
        };
    }

    /// <summary>
    /// Keeps the camera out of the scenery. Distinct from occlusion, which only shortens the shot
    /// along the sight line to a subject: this sweeps wherever the camera is actually going, so it
    /// covers a hand-flown camera and a dolly track laid through a wall, neither of which has a
    /// sight line to shorten.
    /// </summary>
    [Serializable]
    public struct BasisCameraCollisionSettings
    {
        [Tooltip("Radius of the cast that sweeps the camera's path, in metres. Roughly how much of a corner the camera keeps clear of.")]
        public float radius;

        [Tooltip("Clearance kept from whatever the camera would otherwise have hit, in metres.")]
        public float padding;

        public static BasisCameraCollisionSettings Default => new BasisCameraCollisionSettings
        {
            radius = 0.18f,
            padding = 0.08f,
        };
    }

    /// <summary>
    /// The vertigo shot: hold the subject at the size they were when the effect was fitted, and let
    /// the field of view make up whatever the camera's movement changed. The reference is taken from
    /// the live shot rather than authored, so the move starts from the framing already set up.
    /// </summary>
    [Serializable]
    public struct BasisCameraDollyZoomSettings
    {
        [Tooltip("Narrowest the lens may go while holding subject size.")]
        [Range(5f, 120f)] public float minFov;

        [Tooltip("Widest the lens may go while holding subject size.")]
        [Range(5f, 120f)] public float maxFov;

        public static BasisCameraDollyZoomSettings Default => new BasisCameraDollyZoomSettings
        {
            minFov = 12f,
            maxFov = 100f,
        };
    }

    /// <summary>
    /// Weight in the rig, so a fast move carries past the mark and settles back. Damping cannot do
    /// this — it converges on its target and never crosses it — and it is the overshoot rather than
    /// the lag that reads as somebody holding the camera.
    /// </summary>
    [Serializable]
    public struct BasisCameraRigWeightSettings
    {
        [Tooltip("How quickly the rig catches up to where it is being aimed, in hertz. Lower is heavier.")]
        public float responsiveness;

        [Tooltip("How far past the mark a fast move carries before settling. At 0 the rig only lags, and never overshoots.")]
        [Range(0f, 1f)] public float bounce;

        public static BasisCameraRigWeightSettings Default => new BasisCameraRigWeightSettings
        {
            responsiveness = 5f,
            bounce = 0.35f,
        };
    }

    [Serializable]
    public struct BasisCameraOcclusionSettings
    {
        [Tooltip("Extra distance kept clear of whatever is in the way, in metres.")]
        public float padding;

        [Tooltip("Closest the camera may be pulled toward the subject, in metres.")]
        public float minDistance;

        [Tooltip("Seconds to ease back out once the shot is clear again. Pulling in is always instant.")]
        public float returnDamping;

        [Tooltip("Radius of the cast that looks for geometry between the camera and its subject.")]
        public float probeRadius;

        public static BasisCameraOcclusionSettings Default => new BasisCameraOcclusionSettings
        {
            padding = 0.25f,
            minDistance = 0.4f,
            returnDamping = 0.6f,
            probeRadius = 0.12f,
        };
    }

    [Serializable]
    public struct BasisCameraLensSettings
    {
        [Range(5f, 120f)] public float fov;

        [Tooltip("Seconds for the lens to reach a changed field of view.")]
        public float damping;

        public static BasisCameraLensSettings Default => new BasisCameraLensSettings
        {
            fov = 40f,
            damping = 0.5f,
        };
    }
}
