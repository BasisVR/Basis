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
        /// Backing store for jiggle colliders created for the avatar rig.
        /// </summary>
        public List<JiggleColliderSerializable> JiggleColliders;

        /// <summary>
        /// Creates a set of jiggle colliders for the provided mapping and registers them with the global <see cref="JigglePhysics"/>.
        /// </summary>
        /// <param name="Mapping">Bone transform mapping used to generate colliders for feet and hands/fingers.</param>
        /// <remarks>
        /// Feet receive a slightly larger default collider radius than hands/fingers.
        /// Created colliders are cached in <see cref="JiggleColliders"/> and added via <see cref="JigglePhysics.AddJiggleCollider(JiggleColliderSerializable)"/>.
        /// </remarks>
        public void AddJiggleRigColliders(BasisTransformMapping Mapping)
        {
            JiggleCreatorHelper(Mapping.leftFoot, 0.015f);
            JiggleCreatorHelper(Mapping.rightFoot, 0.015f);

            // Arms: upper-arm capsule + forearm capsule + hand tip sphere, all from one array.
            JiggleCreatorHelperCapsule(new Transform[] { Mapping.leftUpperArm, Mapping.leftLowerArm, Mapping.leftHand }, 0.025f);
            JiggleCreatorHelperCapsule(new Transform[] { Mapping.RightUpperArm, Mapping.RightLowerArm, Mapping.rightHand }, 0.025f);

            JiggleCreatorHelperCapsule(Mapping.LeftThumb);
            JiggleCreatorHelperCapsule(Mapping.LeftIndex);
            JiggleCreatorHelperCapsule(Mapping.LeftMiddle);
            JiggleCreatorHelperCapsule(Mapping.LeftRing);
            JiggleCreatorHelperCapsule(Mapping.LeftLittle);

            JiggleCreatorHelperCapsule(Mapping.RightThumb);
            JiggleCreatorHelperCapsule(Mapping.RightIndex);
            JiggleCreatorHelperCapsule(Mapping.RightMiddle);
            JiggleCreatorHelperCapsule(Mapping.RightRing);
            JiggleCreatorHelperCapsule(Mapping.RightLittle);

            // Batch-add all colliders at once to avoid O(n²) dedup in JiggleMemoryBus.
            // ScheduleAdd does a linear scan of pendingSceneColliderAdd for each call,
            // so adding 32 colliders one-at-a-time when the pending list is large is expensive.
            JigglePhysics.AddJiggleColliders(JiggleColliders);
        }

        /// <summary>
        /// Helper that creates jiggle colliders for an array of transforms using the default hand/finger scale.
        /// </summary>
        /// <param name="Parents">Transforms that will each receive a collider.</param>
        public void JiggleCreatorHelper(Transform[] Parents)
        {
            int count = Parents.Length;
            for (int i = 0; i < count; i++)
            {
                JiggleCreatorHelper(Parents[i]);
            }
        }

        /// <summary>
        /// Creates capsule jiggle colliders along consecutive bone pairs in an array (e.g. finger bones).
        /// Each pair of consecutive transforms gets a capsule spanning the bone length.
        /// The last bone in the array receives a sphere collider for the tip.
        /// </summary>
        /// <param name="Parents">Ordered bone transforms (e.g. proximal, intermediate, distal).</param>
        /// <param name="Radius">Base radius for the capsule/sphere. Default is <c>0.005</c>.</param>
        public void JiggleCreatorHelperCapsule(Transform[] Parents, float Radius = 0.005f)
        {
            int count = Parents.Length;
            for (int i = 0; i < count; i++)
            {
                if (Parents[i] == null) continue;

                if (i < count - 1 && Parents[i + 1] != null)
                {
                    float boneLength = Vector3.Distance(Parents[i].position, Parents[i + 1].position);
                    Vector3 boneDir = (Parents[i + 1].position - Parents[i].position).normalized;

                    // Determine which local axis best aligns with the bone direction.
                    float dotX = Mathf.Abs(Vector3.Dot(boneDir, Parents[i].right));
                    float dotY = Mathf.Abs(Vector3.Dot(boneDir, Parents[i].up));
                    float dotZ = Mathf.Abs(Vector3.Dot(boneDir, Parents[i].forward));

                    JiggleCollider.CapsuleAxis axis;
                    if (dotX >= dotY && dotX >= dotZ) axis = JiggleCollider.CapsuleAxis.X;
                    else if (dotY >= dotZ) axis = JiggleCollider.CapsuleAxis.Y;
                    else axis = JiggleCollider.CapsuleAxis.Z;

                    Vector3 lossyScale = Parents[i].lossyScale;
                    float avgScale = (Mathf.Abs(lossyScale.x) + Mathf.Abs(lossyScale.y) + Mathf.Abs(lossyScale.z)) / 3f;

                    JiggleColliders.Add(new JiggleColliderSerializable
                    {
                        collider = new JiggleCollider()
                        {
                            type = JiggleCollider.JiggleColliderType.Capsule,
                            localToWorldMatrix = Parents[i].localToWorldMatrix,
                            radius = Radius / avgScale,
                            height = boneLength / avgScale,
                            capsuleAxis = axis
                        },
                        transform = Parents[i]
                    });
                }
                else
                {
                    // Tip bone: sphere collider for the fingertip.
                    JiggleCreatorHelper(Parents[i], Radius);
                }
            }
        }

        /// <summary>
        /// Creates a single spherical jiggle collider for a given transform and stores it in <see cref="JiggleColliders"/>.
        /// </summary>
        /// <param name="Parent">Transform that defines the collider's transform and space.</param>
        /// <param name="Scale">
        /// Base radius used to size the collider. Final radius is scaled by <c>1 / (Parent.lossyScale.magnitude / 3)</c>.
        /// Default is <c>0.005</c>.
        /// </param>
        public void JiggleCreatorHelper(Transform Parent, float Scale = 0.005f)
        {
            if (Parent != null)
            {
                JiggleColliders.Add(new JiggleColliderSerializable
                {
                    collider = new JiggleCollider()
                    {
                        type = JiggleCollider.JiggleColliderType.Sphere,
                        localToWorldMatrix = Parent.localToWorldMatrix,
                        radius = Scale / (Parent.lossyScale.magnitude / 3)
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
            // Batch-remove all colliders at once to avoid O(n²) linear scans in JiggleMemoryBus.
            JigglePhysics.RemoveJiggleColliders(JiggleColliders);
            JiggleColliders.Clear();
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
