using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.TransformBinders.BoneControl;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Runtime visualisation of the two face anchors every avatar carries — the center eye and the
/// mouth — on the local player and on every remote, driven each frame from
/// <see cref="SMModuleDebugOptions"/> under the master gizmo gate.
///
/// Neither anchor is a bone. Both are authored as a single point per avatar
/// (<c>BasisAvatar.AvatarEyePosition</c> / <c>AvatarMouthPosition</c>, a packed height/forward pair
/// in model metres above the animator root) and are reconstructed at runtime by two completely
/// separate code paths: bone-control locks off the head for the local player, and a Burst FK chain
/// off the networked head for remotes. They are load-bearing — the gaze selector dots
/// <c>forward(rot_CenterEye)</c> to decide who is looking at whom, and a remote's voice AudioSource
/// is parented to the mouth marker — but nothing renders at either point, so an anchor can sit well
/// off the face for a whole session without anyone seeing why the voice or the eye contact is wrong.
///
/// Per anchor this draws:
///  • a ball where the driver actually put it,
///  • a short ray down the anchor's forward — voice direction for the mouth, the facing the gaze
///    selector scores for the eye,
///  • the leg back to the head bone, which is the offset the reconstruction applies,
///  • a red leg out to where the anchor belongs if it were rigidly parented to the head bone as
///    authored, with the ball turning red past a centimetre and the miss printed in the label.
///
/// That last one is the point of the gizmo. The reference is recomputed here from live transforms
/// and the avatar's own authored values, independently of the snapshot each driver took when the
/// avatar was registered — so a stale registration, a rescale that never re-registered, a far-LOD
/// restore that brought back the wrong anchor, or a regression in the reconstruction math all show
/// up as a red leg rather than as a vague report that someone's voice sounds like it comes from
/// their shoulder. When everything agrees the leg is zero-length and invisible.
///
/// The local player draws warm and remotes draw cool, and eyes are lighter than mouths, so one view
/// separates "my own anchors are wrong" from "this specific remote is wrong".
///
/// Expect the local player to sit a little off zero and stay there. Its anchors hang off the head
/// bone CONTROL, which is upstream of the IK pass, while the reference here is measured on the head
/// BONE the IK pass wrote — so head offset, lordosis and head-chop all land in that gap legitimately.
/// A steady sub-centimetre local reading is the rig working; a remote reading that grows as the
/// player turns their head is not.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisMouthEyeGizmos
{
    // Mirrored from settings by SMModuleDebugOptions.
    public static bool Show;
    public static bool ShowLabels;

    // Metres at avatar scale 1.
    private const float EyeBallSize = 0.017f;
    private const float MouthBallSize = 0.021f;
    private const float FacingLength = 0.22f;
    private const float FacingWidth = 0.004f;
    private const float HeadLegWidth = 0.003f;
    private const float ErrorLegWidth = 0.006f;
    private const float LabelLift = 0.06f;
    private const float LabelBaseScale = 0.02f;

    /// <summary>Miss past which the anchor is called wrong rather than noisy, at avatar scale 1.</summary>
    private const float ErrorThreshold = 0.01f;

    private static readonly Color LocalEye = new Color(1f, 0.88f, 0.35f, 1f);
    private static readonly Color LocalMouth = new Color(1f, 0.5f, 0.15f, 1f);
    private static readonly Color RemoteEye = new Color(0.45f, 0.9f, 1f, 1f);
    private static readonly Color RemoteMouth = new Color(0.4f, 0.55f, 1f, 1f);
    private static readonly Color HeadLeg = new Color(0.6f, 0.6f, 0.65f, 1f);
    private static readonly Color ErrorColor = new Color(1f, 0.18f, 0.18f, 1f);

    private struct AnchorVisual
    {
        public int Ball;
        public int Facing;
        public int HeadLeg;
        public int ErrorLeg;
        public int Label;
    }

    private struct AnchorSample
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 HeadPosition;
        public Vector3 Truth;
        public bool HasTruth;
        public bool HasHead;
        public bool Unauthored;
        public bool Mouth;
        public bool Local;
        public float Scale;
    }

    /// <summary>
    /// Nothing fills these in at runtime — the only writers are the SDK's build-time setup, the NDMF
    /// bridge, the far-LOD payload and generic avatar data — so an avatar that shipped without them
    /// keeps a zero here and lands its anchor down at the animator root, around the feet. Worth
    /// calling out on its own: the reference below is derived from the same authored value, so it
    /// would agree with the driver and report a reassuring 0.0 cm for an anchor that is nowhere near
    /// the face.
    /// </summary>
    private static bool IsUnauthored(Vector2 authored) => authored == Vector2.zero;

    private static readonly List<AnchorVisual> _visuals = new List<AnchorVisual>();
    private static readonly List<AnchorSample> _samples = new List<AnchorSample>();
    private static readonly List<KeyValuePair<ushort, BasisRemotePlayer>> _remotes = new List<KeyValuePair<ushort, BasisRemotePlayer>>();
    private static readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(48);

    /// <summary>Per-frame entry point. <paramref name="scale"/> is the local avatar scale, used for label sizing.</summary>
    public static void Tick(float scale)
    {
        if (!Show)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f) scale = 1f;

        _samples.Clear();
        CollectLocal();
        CollectRemotes();

        Vector3 cameraPosition = BasisLocalCameraDriver.Position;

        while (_visuals.Count > _samples.Count)
        {
            Destroy(_visuals[_visuals.Count - 1]);
            _visuals.RemoveAt(_visuals.Count - 1);
        }

        for (int Index = 0; Index < _samples.Count; Index++)
        {
            if (Index >= _visuals.Count)
            {
                _visuals.Add(default);
            }
            _visuals[Index] = Draw(_visuals[Index], _samples[Index], cameraPosition, scale);
        }
    }

    /// <summary>
    /// The local anchors come off the bone controls the IK pass publishes, which is the same data
    /// every local consumer reads — not a re-derivation. Routed through FindBone rather than the
    /// cached statics because a miss there hands back a detached control whose OutgoingWorldData
    /// reads as identity, which would plant an anchor at the world origin.
    /// </summary>
    private static void CollectLocal()
    {
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (player == null || player.BasisAvatar == null || player.LocalBoneDriver == null)
        {
            return;
        }

        BasisLocalBoneDriver driver = player.LocalBoneDriver;
        BasisTransformMapping mapping = BasisLocalAvatarDriver.Mapping;
        Add(BasisBoneTrackedRole.CenterEye, player.BasisAvatar.AvatarEyePosition, mouth: false);
        Add(BasisBoneTrackedRole.Mouth, player.BasisAvatar.AvatarMouthPosition, mouth: true);

        void Add(BasisBoneTrackedRole role, Vector2 authored, bool mouth)
        {
            if (!driver.FindBone(out BasisLocalBoneControl control, role) || control == null)
            {
                return;
            }
            BasisCalibratedCoords driven = control.OutgoingWorldData;
            AnchorSample sample = new AnchorSample
            {
                Position = driven.position,
                Rotation = driven.rotation,
                Mouth = mouth,
                Local = true,
                Scale = 1f,
                Unauthored = IsUnauthored(authored),
            };
            sample.HasTruth = TryHeadParentedTruth(mapping, authored, out sample.Truth, out sample.HeadPosition, out sample.Scale);
            sample.HasHead = sample.HasTruth;
            _samples.Add(sample);
        }
    }

    /// <summary>
    /// Remote mouths are read off the marker Transform itself rather than the frame it was computed
    /// from — that Transform is what the AudioSource is parented to, so reading it is the only way
    /// to catch an apply that never landed. The center eye has no Transform at all and has to come
    /// from the frame array; taking it once for every remote also keeps the job completion the
    /// accessor forces down to a single call per tick. Ordered by player id so an anchor keeps its
    /// gizmo slot between frames instead of swapping labels with another player's.
    /// </summary>
    private static void CollectRemotes()
    {
        _remotes.Clear();
        foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
        {
            if (pair.Value != null && !pair.Value.IsDestroyed)
            {
                _remotes.Add(pair);
            }
        }
        if (_remotes.Count == 0)
        {
            return;
        }
        _remotes.Sort(static (a, b) => a.Key.CompareTo(b.Key));

        int[] indexMap = RemoteBoneJobSystem.GetSOutIndexMap();
        NativeArray<RemoteFrameOutput> frames = RemoteBoneJobSystem.GetRemoteFrameArray();
        bool framesUsable = indexMap != null && frames.IsCreated;

        for (int Index = 0; Index < _remotes.Count; Index++)
        {
            ushort playerId = _remotes[Index].Key;
            BasisRemotePlayer remote = _remotes[Index].Value;
            if (remote.BasisAvatar == null || remote.RemoteAvatarDriver == null)
            {
                continue;
            }
            BasisTransformMapping mapping = remote.RemoteAvatarDriver.References;

            if (remote.MouthTransform != null)
            {
                remote.MouthTransform.GetPositionAndRotation(out Vector3 mouthPosition, out Quaternion mouthRotation);
                AnchorSample mouth = new AnchorSample
                {
                    Position = mouthPosition,
                    Rotation = mouthRotation,
                    Mouth = true,
                    Local = false,
                    Scale = 1f,
                    Unauthored = IsUnauthored(remote.BasisAvatar.AvatarMouthPosition),
                };
                mouth.HasTruth = TryHeadParentedTruth(mapping, remote.BasisAvatar.AvatarMouthPosition,
                    out mouth.Truth, out mouth.HeadPosition, out mouth.Scale);
                mouth.HasHead = mouth.HasTruth;
                _samples.Add(mouth);
            }

            if (!framesUsable || playerId >= indexMap.Length)
            {
                continue;
            }
            int frameIndex = indexMap[playerId];
            if (frameIndex < 0 || frameIndex >= frames.Length)
            {
                continue;
            }
            RemoteFrameOutput frame = frames[frameIndex];
            AnchorSample eye = new AnchorSample
            {
                Position = frame.pos_CenterEye,
                Rotation = frame.rot_CenterEye,
                Mouth = false,
                Local = false,
                Scale = 1f,
                Unauthored = IsUnauthored(remote.BasisAvatar.AvatarEyePosition),
            };
            eye.HasTruth = TryHeadParentedTruth(mapping, remote.BasisAvatar.AvatarEyePosition,
                out eye.Truth, out eye.HeadPosition, out eye.Scale);
            eye.HasHead = eye.HasTruth;
            _samples.Add(eye);
        }
    }

    /// <summary>
    /// Where the anchor belongs: the authored point, expressed relative to the head's T-pose position
    /// in the same frame, carried into world by the head.
    ///
    /// Both the authored Vector2 and TposeWorld are root-relative RENDERED metres — the root's
    /// rotation undone, its scale still in them. TposeFromRoot is the same geometry with the scale
    /// divided out, so it is the wrong operand here and reads correct only on an avatar whose
    /// animator root sits at scale 1. Dividing by the recorded root scale puts the offset into model
    /// units, and the avatar's live world scale then reintroduces size.
    /// </summary>
    private static bool TryHeadParentedTruth(BasisTransformMapping mapping, Vector2 authored,
        out Vector3 truth, out Vector3 headPosition, out float scale)
    {
        truth = default;
        headPosition = default;
        scale = 1f;

        if (mapping == null || !mapping.Hashead || mapping.head == null || mapping.AnimatorRoot == null)
        {
            return false;
        }
        if (!mapping.TposeWorld.TryGetValue(HumanBodyBones.Head, out BasisCalibratedCoords tposeHead))
        {
            return false;
        }

        float recorded = mapping.RootScale.y;
        if (recorded < 1e-5f)
        {
            return false;
        }
        float lossy = mapping.AnimatorRoot.lossyScale.y;
        if (lossy > 1e-5f)
        {
            scale = lossy;
        }

        mapping.head.GetPositionAndRotation(out headPosition, out Quaternion headWorld);
        Vector3 offset = (BasisHelpers.AvatarPositionConversion(authored) - tposeHead.position) / recorded;
        Quaternion frame = headWorld * Quaternion.Inverse(tposeHead.rotation);
        truth = headPosition + frame * (offset * scale);
        return true;
    }

    private static AnchorVisual Draw(AnchorVisual visual, AnchorSample sample, Vector3 cameraPosition, float labelScale)
    {
        float size = (sample.Mouth ? MouthBallSize : EyeBallSize) * sample.Scale;
        float miss = sample.HasTruth ? Vector3.Distance(sample.Position, sample.Truth) : 0f;
        bool wrong = sample.Unauthored || (sample.HasTruth && miss > ErrorThreshold * sample.Scale);

        Color tint = sample.Local
            ? (sample.Mouth ? LocalMouth : LocalEye)
            : (sample.Mouth ? RemoteMouth : RemoteEye);

        EnsureSphere(ref visual.Ball, sample.Position, size, wrong ? ErrorColor : tint);

        Vector3 facingEnd = sample.Position + sample.Rotation * Vector3.forward * (FacingLength * sample.Scale);
        EnsureLine(ref visual.Facing, sample.Position, facingEnd, FacingWidth * sample.Scale, tint);

        // Zero-length when the anchor sits on the head bone, which never happens in practice.
        Vector3 headEnd = sample.HasHead ? sample.HeadPosition : sample.Position;
        EnsureLine(ref visual.HeadLeg, headEnd, sample.Position, HeadLegWidth * sample.Scale, HeadLeg);

        // Degenerate and invisible while the reconstruction agrees with the reference.
        Vector3 truthEnd = sample.HasTruth ? sample.Truth : sample.Position;
        EnsureLine(ref visual.ErrorLeg, sample.Position, truthEnd, ErrorLegWidth * sample.Scale, ErrorColor);

        if (ShowLabels)
        {
            _text.Clear();
            _text.Append(sample.Mouth ? "mouth " : "eye ");
            _text.Append(sample.Local ? "you" : "remote");
            if (sample.Unauthored)
            {
                // Deliberately not a distance: the reference shares the same zero, so any number
                // printed here would read as agreement rather than as an unauthored avatar.
                _text.Append("  UNSET");
            }
            else if (sample.HasTruth)
            {
                _text.Append("  ");
                _text.Append((miss * 100f).ToString("0.0"));
                _text.Append("cm");
            }
            else
            {
                _text.Append("  no ref");
            }
            string label = _text.ToString();

            Vector3 labelPosition = sample.Position + Vector3.up * (LabelLift * sample.Scale);
            Color labelColor = wrong ? ErrorColor : tint;
            if (visual.Label <= 0)
            {
                BasisGizmoManager.CreateTextGizmo("MouthEyeLabel", out visual.Label, labelPosition, label, labelColor);
            }
            else
            {
                Quaternion rotation = BasisGizmoManager.BillboardRotation(labelPosition, cameraPosition);
                BasisGizmoManager.UpdateTextGizmo(visual.Label, labelPosition, rotation, LabelBaseScale * labelScale, label, labelColor);
            }
        }
        else if (visual.Label > 0)
        {
            BasisGizmoManager.DestroyGizmo(visual.Label);
            visual.Label = 0;
        }

        return visual;
    }

    private static void EnsureSphere(ref int id, Vector3 position, float size, Color color)
    {
        if (id <= 0)
        {
            if (BasisGizmoManager.CreateSphereGizmo("MouthEye", out int created, position, size, color))
            {
                id = created;
            }
            return;
        }
        BasisGizmoManager.UpdateSphereGizmo(id, position, Vector3.one * size);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    private static void EnsureLine(ref int id, Vector3 start, Vector3 end, float width, Color color)
    {
        if (id <= 0)
        {
            if (BasisGizmoManager.CreateLineGizmo("MouthEyeLine", out int created, start, end, width, color))
            {
                id = created;
            }
            return;
        }
        BasisGizmoManager.UpdateLineGizmo(id, start, end);
        BasisGizmoManager.UpdateGizmoColor(id, color);
    }

    public static void Shutdown()
    {
        int count = _visuals.Count;
        for (int Index = 0; Index < count; Index++)
        {
            Destroy(_visuals[Index]);
        }
        _visuals.Clear();
        _samples.Clear();
        _remotes.Clear();
    }

    private static void Destroy(AnchorVisual visual)
    {
        if (visual.Ball > 0) BasisGizmoManager.DestroyGizmo(visual.Ball);
        if (visual.Facing > 0) BasisGizmoManager.DestroyGizmo(visual.Facing);
        if (visual.HeadLeg > 0) BasisGizmoManager.DestroyGizmo(visual.HeadLeg);
        if (visual.ErrorLeg > 0) BasisGizmoManager.DestroyGizmo(visual.ErrorLeg);
        if (visual.Label > 0) BasisGizmoManager.DestroyGizmo(visual.Label);
    }
}
