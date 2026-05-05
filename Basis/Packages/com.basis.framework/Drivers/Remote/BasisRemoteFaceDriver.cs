using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Drives automatic facial blinking using skinned mesh blendshapes.
    /// handles eye movement override aswell
    /// </summary>
    /// <remarks>
    /// This driver schedules pseudo-random blink events and animates eye-closure/opening
    /// by writing weights to a set of blink blendshapes on a <see cref="SkinnedMeshRenderer"/>.
    /// It also observes face visibility via <see cref="BasisPlayer.FaceRenderer"/> and disables
    /// updates when the face is not visible.
    /// </remarks>
    [System.Serializable]
    public class BasisRemoteFaceDriver
    {
        /// <summary>
        /// If set to <c>true</c>, overrides and disables the blinking simulation logic.
        /// </summary>
        public bool OverrideBlinking = false;
        /// <summary>
        /// overrides the eye output
        /// </summary>
        public bool OverrideEye = false;
        /// <summary>
        /// Renderer containing blink blendshapes referenced by <see cref="blendShapeIndices"/>.
        /// </summary>
        public SkinnedMeshRenderer meshRenderer;

        /// <summary>Left eye bone (null when the rig has no eye bones).</summary>
        public Transform LeftEyeTransform;
        /// <summary>Right eye bone (null when the rig has no eye bones).</summary>
        public Transform RightEyeTransform;
        /// <summary>True when both eye bones were resolved from the rig and calibration succeeded.</summary>
        public bool HasEyeBones;
        /// <summary>Per-eye calibration computed once at avatar setup; converts canonical yaw/pitch to rig-local rotation.</summary>
        public BasisEyeCalibration calLeft;
        public BasisEyeCalibration calRight;

        /// <summary>
        /// Blendshape indices on <see cref="meshRenderer"/> used for blinking.
        /// Values of <c>-1</c> in the avatar data are ignored.
        /// </summary>
        public List<int> blendShapeIndices = new List<int>();

        /// <summary>
        /// Cached count of valid blink blendshape indices.
        /// </summary>
        public int blendShapeCount;

        /// <summary>
        /// blendshape mesh count on avatar face blink mesh.
        /// </summary>
        public int meshBlendShapeCount;

        /// <summary>
        /// Player whose face visibility is observed.
        /// </summary>
        public BasisPlayer linkedPlayer;

        /// <summary>
        /// Whether updates are currently enabled (e.g., face visible and renderer present).
        /// </summary>
        public bool BlinkingEnabled;

        /// <summary>
        /// Initializes the blink driver with player and avatar data and wires face visibility callbacks.
        /// </summary>
        /// <param name="player">The owning <see cref="BasisPlayer"/>.</param>
        /// <param name="avatar">Avatar providing blink mesh and viseme indices.</param>
        public void Initialize(BasisPlayer player, BasisAvatar avatar)
        {
            linkedPlayer = player;
            blendShapeIndices.Clear();

            // Eye bones are not part of the network bone sync (LeftEye/RightEye are excluded
            // in BasisBoneRotationCompression). They're driven locally from the eye floats
            // produced by BasisRemoteFaceManagement / EyeTrackingBoneActuation. Calibrate
            // here so ApplyEyeRotations can write rig-local rotations every frame.
            InitializeEyes(player, avatar);

            if (avatar == null || avatar.FaceBlinkMesh == null || avatar.BlinkViseme == null)
            {
                BlinkingEnabled = false;
                return;
            }

            meshRenderer = avatar.FaceBlinkMesh;

            // Collect valid blink viseme indices
            for (int Index = 0; Index < avatar.BlinkViseme.Length; Index++)
            {
                int blinkIndex = avatar.BlinkViseme[Index];
                if (blinkIndex != -1)
                {
                    blendShapeIndices.Add(blinkIndex);
                }
            }

            blendShapeCount = blendShapeIndices.Count;
            meshBlendShapeCount = meshRenderer != null && meshRenderer.sharedMesh != null? meshRenderer.sharedMesh.blendShapeCount: 0;

            // Observe face visibility
            if (linkedPlayer != null && linkedPlayer.FaceRenderer != null)
            {
                linkedPlayer.FaceRenderer.Check += UpdateFaceVisibility;
                UpdateFaceVisibility(linkedPlayer.FaceIsVisible);
            }

            BlinkingEnabled = meshRenderer != null && blendShapeCount > 0 && meshBlendShapeCount > 0;
        }
        /// <summary>
        /// Unsubscribes from face visibility callbacks.
        /// </summary>
        public void OnDestroy()
        {
            if (linkedPlayer != null && linkedPlayer.FaceRenderer != null)
            {
                linkedPlayer.FaceRenderer.Check -= UpdateFaceVisibility;
            }
        }

        /// <summary>
        /// Updates the internal enabled state based on face visibility
        /// and resets blendshape state when disabled.
        /// </summary>
        /// <param name="state">True if the face is currently visible.</param>
        public void UpdateFaceVisibility(bool state)
        {
            BlinkingEnabled = state && meshRenderer != null;

            if (!BlinkingEnabled && meshRenderer != null)
            {
                if (OverrideBlinking == false)
                {
                    for (int i = 0; i < blendShapeCount; i++)
                    {
                        SafeSetBlendShape(blendShapeIndices[i], 0);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether the provided avatar contains the data needed to run the blink driver.
        /// </summary>
        /// <param name="avatar">Avatar to validate.</param>
        /// <returns>
        /// <c>true</c> if a blink mesh is present and at least one valid blink viseme index exists; otherwise <c>false</c>.
        /// </returns>
        public static bool MeetsRequirements(BasisAvatar avatar)
        {
            if (avatar == null || avatar.FaceBlinkMesh == null || avatar.BlinkViseme == null || avatar.BlinkViseme.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < avatar.BlinkViseme.Length; i++)
            {
                if (avatar.BlinkViseme[i] != -1)
                {
                    return true;
                }
            }

            return false;
        }
        public void SafeSetBlendShape(int idx, float blendWeight)
        {
            if (meshRenderer == null) return;
            if (idx >= 0 && idx < meshBlendShapeCount)
            {
                meshRenderer.SetBlendShapeWeight(idx, blendWeight);
            }
        }

        /// <summary>
        /// Resolves the remote avatar's eye bones and computes per-eye calibration so
        /// canonical yaw/pitch input can be transformed into rig-local rotation. Run
        /// once per avatar at calibration time. Sets <see cref="HasEyeBones"/>.
        /// </summary>
        private void InitializeEyes(BasisPlayer player, BasisAvatar avatar)
        {
            HasEyeBones = false;
            LeftEyeTransform = null;
            RightEyeTransform = null;

            if (avatar == null) return;
            if (!(player is BasisRemotePlayer remote)) return;

            var refs = remote.RemoteAvatarDriver?.References;
            if (refs == null || refs.head == null) return;
            if (!refs.HasLeftEye || !refs.HasRightEye) return;

            LeftEyeTransform = refs.LeftEye;
            RightEyeTransform = refs.RightEye;
            calLeft = BasisLocalEyeDriver.CalibrateOneEye(LeftEyeTransform, refs.head);
            calRight = BasisLocalEyeDriver.CalibrateOneEye(RightEyeTransform, refs.head);
            HasEyeBones = true;
        }

        /// <summary>
        /// Applies normalized eye look values to the remote avatar's eye bones.
        /// Inputs are signed [-1, 1] where x is horizontal (yaw) and y is vertical
        /// (pitch). Safe to call every frame; no-ops when <see cref="HasEyeBones"/> is false.
        /// </summary>
        /// <param name="vL">Vertical for the left eye (pitch, +up).</param>
        /// <param name="hL">Horizontal for the left eye (yaw, +right).</param>
        /// <param name="vR">Vertical for the right eye.</param>
        /// <param name="hR">Horizontal for the right eye.</param>
        public void ApplyEyeRotations(float vL, float hL, float vR, float hR)
        {
            if (!HasEyeBones) return;
            if (LeftEyeTransform == null || RightEyeTransform == null) return;

            ApplyOneEye(LeftEyeTransform, calLeft, hL, vL);
            ApplyOneEye(RightEyeTransform, calRight, hR, vR);
        }

        private static void ApplyOneEye(Transform eye, BasisEyeCalibration cal, float x, float y)
        {
            x = SanitizeAndClamp(x);
            y = SanitizeAndClamp(y);

            // Match EyeTrackingBoneActuation.SetEyeRotation: asin maps the [-1, 1]
            // input into a half-pi yaw/pitch range. Negate y so positive vertical
            // means look up.
            float xRad = math.asin(x);
            float yRad = math.asin(-y);
            quaternion yaw = quaternion.AxisAngle(new float3(0, 1, 0), xRad);
            quaternion pitch = quaternion.AxisAngle(new float3(1, 0, 0), yRad);
            quaternion canonical = math.mul(yaw, pitch);

            quaternion rigOffset = math.mul(math.mul(cal.basis, canonical), cal.invBasis);
            eye.localRotation = math.mul(cal.initialRotation, rigOffset);
        }

        private static float SanitizeAndClamp(float v)
        {
            if (!math.isfinite(v)) return 0f;
            return math.clamp(v, -1f, 1f);
        }
    }
}
