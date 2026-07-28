using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using GatorDragonGames.JigglePhysics;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Base functionality shared by avatar drivers, including role mapping utilities
    /// and optional runtime jiggle-collider rig management.
    /// </summary>
    /// <remarks>
    /// Provides helpers to translate between Unity humanoid bone enums and internal
    /// <see cref="BasisBoneTrackedRole"/> values, spine membership checks, and utilities
    /// to add/remove serialized jiggle colliders based on a <see cref="BasisTransformMapping"/>.
    /// </remarks>
    [System.Serializable]
    public abstract class BasisAvatarDriver
    {
        /// <summary>
        /// Attempts to convert a Unity <see cref="HumanBodyBones"/> value into a <see cref="BasisBoneTrackedRole"/>.
        /// </summary>
        /// <param name="body">Humanoid bone identifier.</param>
        /// <param name="result">On success, the corresponding tracked role.</param>
        /// <returns>
        /// <c>true</c> if the bone maps to a supported <see cref="BasisBoneTrackedRole"/>; otherwise <c>false</c>.
        /// When <c>false</c>, <paramref name="result"/> is set to <see cref="BasisBoneTrackedRole.Hips"/>.
        /// </returns>
        public static bool TryConvertToBoneTrackingRole(HumanBodyBones body, out BasisBoneTrackedRole result)
        {
            switch (body)
            {
                case HumanBodyBones.Head:
                    result = BasisBoneTrackedRole.Head;
                    return true;
                case HumanBodyBones.Neck:
                    result = BasisBoneTrackedRole.Neck;
                    return true;
                case HumanBodyBones.Chest:
                    result = BasisBoneTrackedRole.Chest;
                    return true;
                case HumanBodyBones.Hips:
                    result = BasisBoneTrackedRole.Hips;
                    return true;
                case HumanBodyBones.Spine:
                    result = BasisBoneTrackedRole.Spine;
                    return true;
                case HumanBodyBones.LeftUpperLeg:
                    result = BasisBoneTrackedRole.LeftUpperLeg;
                    return true;
                case HumanBodyBones.RightUpperLeg:
                    result = BasisBoneTrackedRole.RightUpperLeg;
                    return true;
                case HumanBodyBones.LeftLowerLeg:
                    result = BasisBoneTrackedRole.LeftLowerLeg;
                    return true;
                case HumanBodyBones.RightLowerLeg:
                    result = BasisBoneTrackedRole.RightLowerLeg;
                    return true;
                case HumanBodyBones.LeftFoot:
                    result = BasisBoneTrackedRole.LeftFoot;
                    return true;
                case HumanBodyBones.RightFoot:
                    result = BasisBoneTrackedRole.RightFoot;
                    return true;
                case HumanBodyBones.LeftShoulder:
                    result = BasisBoneTrackedRole.LeftShoulder;
                    return true;
                case HumanBodyBones.RightShoulder:
                    result = BasisBoneTrackedRole.RightShoulder;
                    return true;
                case HumanBodyBones.LeftUpperArm:
                    result = BasisBoneTrackedRole.LeftUpperArm;
                    return true;
                case HumanBodyBones.RightUpperArm:
                    result = BasisBoneTrackedRole.RightUpperArm;
                    return true;
                case HumanBodyBones.LeftLowerArm:
                    result = BasisBoneTrackedRole.LeftLowerArm;
                    return true;
                case HumanBodyBones.RightLowerArm:
                    result = BasisBoneTrackedRole.RightLowerArm;
                    return true;
                case HumanBodyBones.LeftHand:
                    result = BasisBoneTrackedRole.LeftHand;
                    return true;
                case HumanBodyBones.RightHand:
                    result = BasisBoneTrackedRole.RightHand;
                    return true;
                case HumanBodyBones.LeftToes:
                    result = BasisBoneTrackedRole.LeftToes;
                    return true;
                case HumanBodyBones.RightToes:
                    result = BasisBoneTrackedRole.RightToes;
                    return true;
                case HumanBodyBones.Jaw:
                    result = BasisBoneTrackedRole.Mouth;
                    return true;
            }
            result = BasisBoneTrackedRole.Hips;
            return false;
        }

        /// <summary>
        /// Attempts to convert an internal <see cref="BasisBoneTrackedRole"/> into a Unity <see cref="HumanBodyBones"/> value.
        /// </summary>
        /// <param name="role">Tracked role to convert.</param>
        /// <param name="result">On success, the corresponding humanoid bone enum.</param>
        /// <returns>
        /// <c>true</c> if a matching humanoid bone exists; otherwise <c>false</c>.
        /// When <c>false</c>, <paramref name="result"/> is set to <see cref="HumanBodyBones.Hips"/>.
        /// </returns>
        public static bool TryConvertToHumanoidRole(BasisBoneTrackedRole role, out HumanBodyBones result)
        {
            switch (role)
            {
                case BasisBoneTrackedRole.Head:
                    result = HumanBodyBones.Head;
                    return true;
                case BasisBoneTrackedRole.Neck:
                    result = HumanBodyBones.Neck;
                    return true;
                case BasisBoneTrackedRole.Chest:
                    result = HumanBodyBones.Chest;
                    return true;
                case BasisBoneTrackedRole.Hips:
                    result = HumanBodyBones.Hips;
                    return true;
                case BasisBoneTrackedRole.Spine:
                    result = HumanBodyBones.Spine;
                    return true;
                case BasisBoneTrackedRole.LeftUpperLeg:
                    result = HumanBodyBones.LeftUpperLeg;
                    return true;
                case BasisBoneTrackedRole.RightUpperLeg:
                    result = HumanBodyBones.RightUpperLeg;
                    return true;
                case BasisBoneTrackedRole.LeftLowerLeg:
                    result = HumanBodyBones.LeftLowerLeg;
                    return true;
                case BasisBoneTrackedRole.RightLowerLeg:
                    result = HumanBodyBones.RightLowerLeg;
                    return true;
                case BasisBoneTrackedRole.LeftFoot:
                    result = HumanBodyBones.LeftFoot;
                    return true;
                case BasisBoneTrackedRole.RightFoot:
                    result = HumanBodyBones.RightFoot;
                    return true;
                case BasisBoneTrackedRole.LeftShoulder:
                    result = HumanBodyBones.LeftShoulder;
                    return true;
                case BasisBoneTrackedRole.RightShoulder:
                    result = HumanBodyBones.RightShoulder;
                    return true;
                case BasisBoneTrackedRole.LeftUpperArm:
                    result = HumanBodyBones.LeftUpperArm;
                    return true;
                case BasisBoneTrackedRole.RightUpperArm:
                    result = HumanBodyBones.RightUpperArm;
                    return true;
                case BasisBoneTrackedRole.LeftLowerArm:
                    result = HumanBodyBones.LeftLowerArm;
                    return true;
                case BasisBoneTrackedRole.RightLowerArm:
                    result = HumanBodyBones.RightLowerArm;
                    return true;
                case BasisBoneTrackedRole.LeftHand:
                    result = HumanBodyBones.LeftHand;
                    return true;
                case BasisBoneTrackedRole.RightHand:
                    result = HumanBodyBones.RightHand;
                    return true;
                case BasisBoneTrackedRole.LeftToes:
                    result = HumanBodyBones.LeftToes;
                    return true;
                case BasisBoneTrackedRole.RightToes:
                    result = HumanBodyBones.RightToes;
                    return true;
                case BasisBoneTrackedRole.Mouth:
                    result = HumanBodyBones.Jaw;
                    return true;
            }

            result = HumanBodyBones.Hips; // fallback
            return false;
        }

        /// <summary>
        /// Determines whether a tracked role belongs to the vertical spine/head chain.
        /// </summary>
        /// <param name="Role">Role to test.</param>
        /// <returns>
        /// <c>true</c> if the role is one of Hips, Chest, Spine, CenterEye, Mouth, or Head; otherwise <c>false</c>.
        /// </returns>
        public static bool IsApartOfSpineVertical(BasisBoneTrackedRole Role)
        {
            if (Role == BasisBoneTrackedRole.Hips ||
                Role == BasisBoneTrackedRole.Chest ||
                Role == BasisBoneTrackedRole.Hips ||
                Role == BasisBoneTrackedRole.Spine ||
                Role == BasisBoneTrackedRole.CenterEye ||
                Role == BasisBoneTrackedRole.Mouth ||
                Role == BasisBoneTrackedRole.Head)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Backing store for all jiggle colliders created for the avatar rig (every category).
        /// Used for the initial full registration and for teardown. Initialized inline: the remote
        /// driver is a plain object (no Unity serialization to populate this), so without the
        /// initializer it would be null and the jiggle remove/add passes would dereference null.
        /// </summary>
        public List<JiggleColliderSerializable> JiggleColliders = new List<JiggleColliderSerializable>();

        /// <summary>Foot/toe colliders (one capsule or sphere per foot).</summary>
        public readonly List<JiggleColliderSerializable> JiggleCollidersFeet = new List<JiggleColliderSerializable>();
        /// <summary>Upper- and lower-arm capsules (both sides).</summary>
        public readonly List<JiggleColliderSerializable> JiggleCollidersArms = new List<JiggleColliderSerializable>();
        /// <summary>Hand tip spheres (one per hand) — the coarsest stand-in for the whole hand.</summary>
        public readonly List<JiggleColliderSerializable> JiggleCollidersHands = new List<JiggleColliderSerializable>();
        /// <summary>Per-finger capsules and finger-tip spheres (the bulk of the collider count).</summary>
        public readonly List<JiggleColliderSerializable> JiggleCollidersFingers = new List<JiggleColliderSerializable>();

        /// <summary>True once <see cref="AddJiggleRigColliders"/> has built a non-empty collider set.</summary>
        public bool HasJiggleColliders;

        /// <summary>Which detail level of colliders is currently registered with the jiggle system.</summary>
        public BasisJiggleColliderTier RegisteredColliderTier = BasisJiggleColliderTier.Full;

        // Retained so deferred finger colliders can be built lazily on LOD tier-up (see ApplyColliderLOD).
        private BasisTransformMapping _jiggleColliderMapping;
        private bool _fingersBuilt;

        /// <summary>
        /// localScale of the transform the runtime avatar rescale writes to (AnimatorRoot), sampled
        /// before that rescale is first applied. Collider radii are authored in metres at this scale.
        /// Must be captured off the same transform ApplyAvatarScaleJob writes to — BasisAvatar.transform
        /// is not necessarily the animator root, so AvatarInitialScale is not interchangeable here.
        /// </summary>
        public Vector3 ColliderScaleReference = Vector3.one;

        // Ratio between the rescaled root now and the scale the radii were authored against. Collider
        // building is async and races the rescale, so this rebases whatever scale happens to be live at
        // build time back onto the reference — the stored radius then lands at authored metres times
        // the avatar's rescale, independent of when the build ran.
        private float _colliderScaleRebase = 1f;

        private static float AverageAxis(Vector3 v) => (Mathf.Abs(v.x) + Mathf.Abs(v.y) + Mathf.Abs(v.z)) / 3f;

        private void RefreshColliderScaleRebase(Transform scaledRoot)
        {
            float reference = AverageAxis(ColliderScaleReference);
            float now = scaledRoot != null ? AverageAxis(scaledRoot.localScale) : reference;
            _colliderScaleRebase = (reference > 1e-6f && now > 1e-6f) ? now / reference : 1f;
        }

        /// <summary>
        /// Creates a set of jiggle colliders for the provided mapping and registers them with the global <see cref="JigglePhysics"/>.
        /// </summary>
        /// <param name="Mapping">Bone transform mapping used to generate colliders for feet and hands/fingers.</param>
        /// <param name="allowColliderLOD">When true (remote avatars), finger colliders may be deferred and built on demand by the distance LOD instead of at load.</param>
        /// <remarks>
        /// Feet receive a slightly larger default collider radius than hands/fingers.
        /// Created colliders are cached in <see cref="JiggleColliders"/> and added via <see cref="JigglePhysics.AddJiggleCollider(JiggleColliderSerializable)"/>.
        /// </remarks>
        public void AddJiggleRigColliders(BasisTransformMapping Mapping, bool allowColliderLOD = false)
        {
            _jiggleColliderMapping = Mapping;
            _fingersBuilt = false;
            RefreshColliderScaleRebase(Mapping.HasAnimatorRoot ? Mapping.AnimatorRoot : null);

            JiggleCollidersFeet.Clear();
            JiggleCollidersArms.Clear();
            JiggleCollidersHands.Clear();
            JiggleCollidersFingers.Clear();

            if (Mapping.HasleftToes && Mapping.HasleftFoot)
                JiggleCreatorHelperCapsule(JiggleCollidersFeet, new Transform[] { Mapping.leftFoot, Mapping.leftToe }, 0.025f, addTipSphere: false);
            else
                JiggleCreatorHelper(JiggleCollidersFeet, Mapping.leftFoot, 0.025f);

            if (Mapping.HasrightToes && Mapping.HasrightFoot)
                JiggleCreatorHelperCapsule(JiggleCollidersFeet, new Transform[] { Mapping.rightFoot, Mapping.rightToe }, 0.025f, addTipSphere: false);
            else
                JiggleCreatorHelper(JiggleCollidersFeet, Mapping.rightFoot, 0.025f);

            // Arms: upper-arm + forearm capsules go to the Arms bucket; the hand tip sphere goes to
            // the Hands bucket so the distance LOD can keep just the hand. The split reproduces the
            // exact colliders the single tip-sphere call used to emit.
            JiggleCreatorHelperCapsule(JiggleCollidersArms, new Transform[] { Mapping.leftUpperArm, Mapping.leftLowerArm, Mapping.leftHand }, 0.025f, addTipSphere: false);
            JiggleCreatorHelper(JiggleCollidersHands, Mapping.leftHand, 0.015f);
            JiggleCreatorHelperCapsule(JiggleCollidersArms, new Transform[] { Mapping.RightUpperArm, Mapping.RightLowerArm, Mapping.rightHand }, 0.025f, addTipSphere: false);
            JiggleCreatorHelper(JiggleCollidersHands, Mapping.rightHand, 0.015f);

            // Fingers are the bulk of the build cost and the first category the distance LOD drops.
            // For remote avatars with the LOD on, skip them at load and register at NoFingers; the LOD
            // pass builds them lazily only if the avatar turns out to be close enough to need them.
            bool deferFingers = allowColliderLOD && BasisJiggleColliderLOD.Enabled;
            if (!deferFingers)
            {
                BuildFingerColliders(Mapping);
                _fingersBuilt = true;
            }

            JiggleColliders.Clear();
            int total = JiggleCollidersFeet.Count + JiggleCollidersArms.Count + JiggleCollidersHands.Count + JiggleCollidersFingers.Count;
            if (JiggleColliders.Capacity < total) JiggleColliders.Capacity = total;
            JiggleColliders.AddRange(JiggleCollidersFeet);
            JiggleColliders.AddRange(JiggleCollidersArms);
            JiggleColliders.AddRange(JiggleCollidersHands);
            JiggleColliders.AddRange(JiggleCollidersFingers);

            HasJiggleColliders = total > 0 || deferFingers;
            RegisteredColliderTier = deferFingers ? BasisJiggleColliderTier.NoFingers : BasisJiggleColliderTier.Full;

            // Batch-add the full set at once to avoid O(n²) dedup in JiggleMemoryBus. The distance
            // LOD pass (BasisTransmissionResults) trims remote avatars back down afterward.
            JigglePhysics.AddJiggleColliders(JiggleColliders);
        }

        private void BuildFingerColliders(BasisTransformMapping Mapping)
        {
            // The LOD can call this many frames after the initial build, at a different root scale.
            // The rebase has to be sampled at the same moment as the per-bone invAvgScale it pairs
            // with, or the two no longer cancel.
            RefreshColliderScaleRebase(Mapping.HasAnimatorRoot ? Mapping.AnimatorRoot : null);

            JiggleCollidersFingers.Clear();
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.LeftThumb);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.LeftIndex);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.LeftMiddle);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.LeftRing);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.LeftLittle);

            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.RightThumb);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.RightIndex);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.RightMiddle);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.RightRing);
            JiggleCreatorHelperCapsule(JiggleCollidersFingers, Mapping.RightLittle);
        }

        /// <summary>
        /// Adds or removes whole collider categories so the registered set matches the requested
        /// detail tier. No-op when the tier is unchanged or no colliders were ever built. Only the
        /// categories that differ between the old and new tier are touched.
        /// </summary>
        public void ApplyColliderLOD(BasisJiggleColliderTier target)
        {
            if (!HasJiggleColliders || target == RegisteredColliderTier)
            {
                return;
            }

            bool[] cur = BasisJiggleColliderLOD.ActiveCategories(RegisteredColliderTier);
            bool[] next = BasisJiggleColliderLOD.ActiveCategories(target);

            // Build the deferred finger colliders the first time a tier actually needs them, and add
            // them to the teardown union so RemoveJiggleRigColliders unregisters them later.
            if (next[3] && !_fingersBuilt && _jiggleColliderMapping != null)
            {
                BuildFingerColliders(_jiggleColliderMapping);
                JiggleColliders.AddRange(JiggleCollidersFingers);
                _fingersBuilt = true;
            }

            ApplyColliderCategoryDelta(JiggleCollidersFeet, cur[0], next[0]);
            ApplyColliderCategoryDelta(JiggleCollidersArms, cur[1], next[1]);
            ApplyColliderCategoryDelta(JiggleCollidersHands, cur[2], next[2]);
            ApplyColliderCategoryDelta(JiggleCollidersFingers, cur[3], next[3]);

            RegisteredColliderTier = target;
        }

        private static void ApplyColliderCategoryDelta(List<JiggleColliderSerializable> colliders, bool wasActive, bool nowActive)
        {
            if (colliders.Count == 0 || wasActive == nowActive)
            {
                return;
            }
            if (nowActive)
            {
                JigglePhysics.AddJiggleColliders(colliders);
            }
            else
            {
                JigglePhysics.RemoveJiggleColliders(colliders);
            }
        }

        /// <summary>
        /// Helper that creates jiggle colliders for an array of transforms using the default hand/finger scale.
        /// </summary>
        /// <param name="target">List the created colliders are appended to.</param>
        /// <param name="Parents">Transforms that will each receive a collider.</param>
        public void JiggleCreatorHelper(List<JiggleColliderSerializable> target, Transform[] Parents)
        {
            int count = Parents.Length;
            for (int i = 0; i < count; i++)
            {
                JiggleCreatorHelper(target, Parents[i]);
            }
        }

        /// <summary>
        /// Creates capsule jiggle colliders along consecutive bone pairs in an array (e.g. finger bones).
        /// Each pair of consecutive transforms gets a capsule spanning the bone length.
        /// The last bone in the array receives a sphere collider for the tip.
        /// </summary>
        /// <param name="target">List the created colliders are appended to.</param>
        /// <param name="Parents">Ordered bone transforms (e.g. proximal, intermediate, distal).</param>
        /// <param name="Radius">Base radius for the capsule/sphere. Default is <c>0.005</c>.</param>
        public void JiggleCreatorHelperCapsule(List<JiggleColliderSerializable> target, Transform[] Parents, float Radius = 0.005f, bool addTipSphere = true, float tipRadius = -1f)
        {
            int count = Parents.Length;
            if (count == 0) return;
            if (tipRadius < 0f) tipRadius = Radius;

            int needed = target.Count + count;
            if (target.Capacity < needed) target.Capacity = needed;

            Matrix4x4 cachedMatrix = default;
            Vector3 cachedPos = default;
            bool hasCached = false;

            for (int i = 0; i < count; i++)
            {
                Transform t = Parents[i];
                if (t == null) { hasCached = false; continue; }

                Matrix4x4 m;
                Vector3 pos;
                if (hasCached)
                {
                    m = cachedMatrix;
                    pos = cachedPos;
                }
                else
                {
                    m = t.localToWorldMatrix;
                    pos = new Vector3(m.m03, m.m13, m.m23);
                }

                float sx = Mathf.Sqrt(m.m00 * m.m00 + m.m10 * m.m10 + m.m20 * m.m20);
                float sy = Mathf.Sqrt(m.m01 * m.m01 + m.m11 * m.m11 + m.m21 * m.m21);
                float sz = Mathf.Sqrt(m.m02 * m.m02 + m.m12 * m.m12 + m.m22 * m.m22);

                // A zeroed bone axis (a common way to hide a bone on stylized avatars) divides to
                // NaN/Inf below, and that NaN rides the collider into every jiggle point it touches.
                const float MinBoneScale = 1e-6f;
                if (sx < MinBoneScale || sy < MinBoneScale || sz < MinBoneScale)
                {
                    hasCached = false;
                    continue;
                }

                // Radii are authored in world metres, but JiggleCollider.Read multiplies whatever is
                // stored by the bone's own average scale — so they have to be pre-divided by it here,
                // exactly like height is. Bone lossyScale is arbitrary rig-authoring data, not a
                // measure of rendered size, so letting it through produces wildly different collider
                // sizes on avatars that look identical.
                float invAvgScale = 3f / (sx + sy + sz);

                Transform tNext = (i + 1 < count) ? Parents[i + 1] : null;
                if (tNext == null && !addTipSphere)
                {
                    hasCached = false;
                    continue;
                }
                if (tNext != null)
                {
                    Matrix4x4 mNext = tNext.localToWorldMatrix;
                    Vector3 posNext = new Vector3(mNext.m03, mNext.m13, mNext.m23);
                    cachedMatrix = mNext;
                    cachedPos = posNext;
                    hasCached = true;

                    float dx = posNext.x - pos.x;
                    float dy = posNext.y - pos.y;
                    float dz = posNext.z - pos.z;
                    float boneLength = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);

                    float rawDotX = m.m00 * dx + m.m10 * dy + m.m20 * dz;
                    float rawDotY = m.m01 * dx + m.m11 * dy + m.m21 * dz;
                    float rawDotZ = m.m02 * dx + m.m12 * dy + m.m22 * dz;
                    float dotX = Mathf.Abs(rawDotX) / sx;
                    float dotY = Mathf.Abs(rawDotY) / sy;
                    float dotZ = Mathf.Abs(rawDotZ) / sz;

                    JiggleCollider.CapsuleAxis axis;
                    if (dotX >= dotY && dotX >= dotZ) axis = JiggleCollider.CapsuleAxis.X;
                    else if (dotY >= dotZ) axis = JiggleCollider.CapsuleAxis.Y;
                    else axis = JiggleCollider.CapsuleAxis.Z;

                    var localOffset = new Unity.Mathematics.float3(
                        rawDotX / (sx * sx),
                        rawDotY / (sy * sy),
                        rawDotZ / (sz * sz)) * 0.5f;

                    target.Add(new JiggleColliderSerializable
                    {
                        collider = new JiggleCollider()
                        {
                            type = JiggleCollider.JiggleColliderType.Capsule,
                            localToWorldMatrix = m,
                            radius = Radius * invAvgScale * _colliderScaleRebase,
                            height = boneLength * invAvgScale,
                            capsuleAxis = axis,
                            localOffset = localOffset
                        },
                        transform = t
                    });
                }
                else
                {
                    hasCached = false;

                    target.Add(new JiggleColliderSerializable
                    {
                        collider = new JiggleCollider()
                        {
                            type = JiggleCollider.JiggleColliderType.Sphere,
                            localToWorldMatrix = m,
                            radius = tipRadius * invAvgScale * _colliderScaleRebase
                        },
                        transform = t
                    });
                }
            }
        }

        /// <summary>
        /// Creates a single spherical jiggle collider for a given transform and appends it to <paramref name="target"/>.
        /// </summary>
        /// <param name="target">List the created collider is appended to.</param>
        /// <param name="Parent">Transform that defines the collider's transform and space.</param>
        /// <param name="Scale">
        /// Radius in world metres. Stored pre-divided by the bone's average scale, because
        /// JiggleCollider.Read multiplies it back each frame — bone lossyScale is arbitrary rig data,
        /// not a measure of rendered size, so passing it through unscaled made the world radius depend
        /// on how the armature happened to be authored. Default is <c>0.005</c>.
        /// </param>
        public void JiggleCreatorHelper(List<JiggleColliderSerializable> target, Transform Parent, float Scale = 0.005f)
        {
            if (Parent != null)
            {
                Matrix4x4 m = Parent.localToWorldMatrix;

                float sx = Mathf.Sqrt(m.m00 * m.m00 + m.m10 * m.m10 + m.m20 * m.m20);
                float sy = Mathf.Sqrt(m.m01 * m.m01 + m.m11 * m.m11 + m.m21 * m.m21);
                float sz = Mathf.Sqrt(m.m02 * m.m02 + m.m12 * m.m12 + m.m22 * m.m22);
                float avgScale = (sx + sy + sz) / 3f;
                if (avgScale < 1e-6f)
                {
                    return;
                }

                target.Add(new JiggleColliderSerializable
                {
                    collider = new JiggleCollider()
                    {
                        type = JiggleCollider.JiggleColliderType.Sphere,
                        localToWorldMatrix = m,
                        radius = Scale / avgScale * _colliderScaleRebase
                    },
                    transform = Parent
                });
            }
        }

        /// <summary>
        /// Unregisters all colliders previously added via <see cref="AddJiggleRigColliders(BasisTransformMapping)"/> and clears the cache.
        /// </summary>
        public void RemoveJiggleRigColliders()
        {
            // Batch-remove the full set at once to avoid O(n²) linear scans in JiggleMemoryBus.
            // Removing the union covers whatever the LOD pass left registered (matched by transform).
            JigglePhysics.RemoveJiggleColliders(JiggleColliders);
            JiggleColliders.Clear();
            JiggleCollidersFeet.Clear();
            JiggleCollidersArms.Clear();
            JiggleCollidersHands.Clear();
            JiggleCollidersFingers.Clear();
            HasJiggleColliders = false;
            RegisteredColliderTier = BasisJiggleColliderTier.Full;
            _fingersBuilt = false;
            _jiggleColliderMapping = null;
        }
        /// <summary>
        /// Applies per-renderer flags for local-avatar SkinnedMeshRenderers and ensures the face mesh has its shadow-only clone.
        /// </summary>
        public static void LocalRenderMeshSettings(int layer, int skinnedMeshRendererLength, SkinnedMeshRenderer[] skinnedMeshRenderers, SkinnedMeshRenderer FaceMesh)
        {
            RemoveOldShadowClones();

            for (int index = 0; index < skinnedMeshRendererLength; index++)
            {
                var Render = skinnedMeshRenderers[index];
                //  Render.shadowCastingMode = ShadowCastingMode.On;
                Render.updateWhenOffscreen = true;
                Render.forceMatrixRecalculationPerRender = true;
                Render.gameObject.layer = layer;
                Render.forceMeshLod = 0;
            }
            if (FaceMesh != null)
            {
                FaceMesh.shadowCastingMode = ShadowCastingMode.Off;
                EnsureShadowOnlyClone(FaceMesh, layer);
            }
        }
        /// <summary>
        /// Applies per-renderer flags for remote-avatar SkinnedMeshRenderers.
        /// </summary>
        public static void RemoteRenderMeshSettings(int layer, int skinnedMeshRendererLength, SkinnedMeshRenderer[] skinnedMeshRenderers)
        {
            for (int index = 0; index < skinnedMeshRendererLength; index++)
            {
                var r = skinnedMeshRenderers[index];
                r.updateWhenOffscreen = false;
                r.forceMatrixRecalculationPerRender = false;
                r.gameObject.layer = layer;
            }
        }
        private struct BasisShadowCloneEntry
        {
            public SkinnedMeshRenderer Source;
            public SkinnedMeshRenderer Clone;
            public int BlendShapeCount;
            public float[] Values;
        }
        private static List<BasisShadowCloneBlendshapeSync> ShadowCloneSyncs = new();
        public static void RemoveOldShadowClones()
        {
            for (int i = 0; i < ShadowCloneSyncs.Count; i++)
            {
                ShadowCloneSyncs[i].Dispose();

                var clone = ShadowCloneSyncs[i].Clone;
                if (clone != null)
                {
                    GameObject.Destroy(clone.gameObject);
                }
            }
            ShadowCloneSyncs.Clear();
        }
        private static void EnsureShadowOnlyClone(SkinnedMeshRenderer source, int layer)
        {
            if (source.enabled && source.gameObject.activeSelf)
            {
                // Create clone object as sibling (keeps hierarchy simple)
                var cloneGO = new GameObject(source.gameObject.name + "_ShadowOnly");
                var sourcetransform = source.transform;
                var clonetransform = cloneGO.transform;
                clonetransform.SetParent(sourcetransform.parent, worldPositionStays: false);
                sourcetransform.GetPositionAndRotation(out UnityEngine.Vector3 position, out Quaternion rotation);
                clonetransform.SetPositionAndRotation(position, rotation);
                clonetransform.localScale = sourcetransform.localScale;
                cloneGO.layer = layer;
                // Clone SMR setup
                var LocalShadowClone = cloneGO.AddComponent<SkinnedMeshRenderer>();
                // The whole point:
                LocalShadowClone.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                LocalShadowClone.receiveShadows = false;
                LocalShadowClone.lightProbeUsage = LightProbeUsage.Off;
                int blendShapeCount = 0;
                if (source.sharedMesh != null)
                {
                    LocalShadowClone.sharedMesh = source.sharedMesh;
                    blendShapeCount = source.sharedMesh.blendShapeCount;
                    for (int Index = 0; Index < blendShapeCount; Index++)
                    {
                        LocalShadowClone.SetBlendShapeWeight(Index, source.GetBlendShapeWeight(Index));
                    }
                }
                if (source.sharedMaterials != null)
                {
                    LocalShadowClone.sharedMaterials = source.sharedMaterials;
                }
                if (source.bones != null)
                {
                    LocalShadowClone.bones = source.bones;
                }
                if (source.rootBone != null)
                {
                    LocalShadowClone.rootBone = source.rootBone;
                }
                LocalShadowClone.updateWhenOffscreen = false;
                LocalShadowClone.forceMatrixRecalculationPerRender = false;
                LocalShadowClone.quality = SkinQuality.Bone1;
                LocalShadowClone.allowOcclusionWhenDynamic = true;
                LocalShadowClone.skinnedMotionVectors = false;
                LocalShadowClone.localBounds = source.localBounds;
                LocalShadowClone.forceMeshLod = -1;
                ShadowCloneSyncs.Add(new BasisShadowCloneBlendshapeSync(source, LocalShadowClone, blendShapeCount));
            }
        }
        public static bool hasBlendShapeJobScheduled = false;
        public static unsafe void ScheduleReadBlendShapes(float epsilon = 0.001f)
        {
            for (int s = 0; s < ShadowCloneSyncs.Count; s++)
            {
                var sync = ShadowCloneSyncs[s];

                if (sync.Source == null || sync.Clone == null)
                {
                    continue;
                }
                if (hasBlendShapeJobScheduled)
                {
                    sync.Handle.Complete();
                    hasBlendShapeJobScheduled = false;
                }
                int count = sync.Count;

                float* pCurrent = (float*)sync.Current.GetUnsafePtr();
                SkinnedMeshRenderer source = sync.Source;
                for (int Index = 0; Index < count; Index++)
                {
                    pCurrent[Index] = source.GetBlendShapeWeight(Index);
                }

                // Schedule job
                var job = new BasisDiffBlendShapesJob
                {
                    Current = sync.Current,
                    Previous = sync.Previous,
                    ChangedMask = sync.ChangedMask,
                    Epsilon = epsilon
                };

                // Batch size can be tuned; 32 is a decent start
                sync.Handle = job.Schedule(count, 32);
                hasBlendShapeJobScheduled = true;
            }
        }
        public static unsafe void ApplyShadowCloneBlendShapes()
        {
            int syncCount = ShadowCloneSyncs.Count;
            for (int s = 0; s < syncCount; s++)
            {
                var sync = ShadowCloneSyncs[s];

                if (sync.Source == null || sync.Clone == null)
                {
                    continue;
                }
                if (hasBlendShapeJobScheduled)
                {
                    sync.Handle.Complete();
                    hasBlendShapeJobScheduled = false;
                }

                int count = sync.Count;
                var clone = sync.Clone;
                byte* changedMask = (byte*)sync.ChangedMask.GetUnsafeReadOnlyPtr();
                float* previous = (float*)sync.Previous.GetUnsafeReadOnlyPtr();
                for (int Index = 0; Index < count; Index++)
                {
                    if (changedMask[Index] != 0)
                    {
                        clone.SetBlendShapeWeight(Index, previous[Index]);
                    }
                }
            }
        }
    }
}
