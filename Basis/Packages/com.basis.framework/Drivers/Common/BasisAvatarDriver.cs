using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using GatorDragonGames.JigglePhysics;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Material = UnityEngine.Material;

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

            JiggleCreatorHelper(Mapping.LeftThumb);
            JiggleCreatorHelper(Mapping.LeftIndex);
            JiggleCreatorHelper(Mapping.LeftMiddle);
            JiggleCreatorHelper(Mapping.LeftRing);
            JiggleCreatorHelper(Mapping.LeftLittle);
            JiggleCreatorHelper(Mapping.leftHand);

            JiggleCreatorHelper(Mapping.RightThumb);
            JiggleCreatorHelper(Mapping.RightIndex);
            JiggleCreatorHelper(Mapping.RightMiddle);
            JiggleCreatorHelper(Mapping.RightRing);
            JiggleCreatorHelper(Mapping.RightLittle);
            JiggleCreatorHelper(Mapping.rightHand);

            //   BasisDebug.Log("Creating Collider Rigs");
            foreach (JiggleColliderSerializable Jiggle in JiggleColliders)
            {
                JigglePhysics.AddJiggleCollider(Jiggle);
            }
        }

        /// <summary>
        /// Helper that creates jiggle colliders for an array of transforms using the default hand/finger scale.
        /// </summary>
        /// <param name="Parents">Transforms that will each receive a collider.</param>
        public void JiggleCreatorHelper(Transform[] Parents)
        {
            foreach (Transform Parent in Parents)
            {
                JiggleCreatorHelper(Parent);
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
                JiggleColliderSerializable jiggleColliderSerializable = new JiggleColliderSerializable
                {
                    collider = new JiggleCollider()
                    {
                        type = JiggleCollider.JiggleColliderType.Sphere,
                        localToWorldMatrix = Parent.localToWorldMatrix,
                        radius = Scale / (Parent.lossyScale.magnitude / 3) // Scaled radius
                    },
                    transform = Parent
                };

                JiggleColliders.Add(jiggleColliderSerializable);
            }
        }

        /// <summary>
        /// Unregisters all colliders previously added via <see cref="AddJiggleRigColliders(BasisTransformMapping)"/> and clears the cache.
        /// </summary>
        public void RemoveJiggleRigColliders()
        {
            // BasisDebug.Log("Removed Collider Rigs");
            foreach (JiggleColliderSerializable Jiggle in JiggleColliders)
            {
                JigglePhysics.RemoveJiggleCollider(Jiggle);
            }
            JiggleColliders.Clear();
        }
        // Common albedo/main texture property names across built-in/URP/custom shaders.
        private static readonly string[] AlbedoProps =
        {
        "_MainTex",          // Some custom / Pyi
        "_BaseMap",          // URP Lit/Unlit
        "_Albedo",           // Some custom
        "_Diffuse",          // Some custom
        "_BaseColorMap",     // Some ShaderGraph setups
        "_ColorTexture",     // Some toon shaders
        "_Tex",              // Generic
        "_Texture"           // Generic
    };
        private static readonly string[] NormalProps =
{
    "_BumpMap", "_NormalMap", "_NormalTex", "_NormalTexture"
};

        private static readonly string[] MetallicProps =
        {
    "_MetallicGlossMap", "_MetallicMap", "_MetalMap", "_MetallicTex"
};

        private static readonly string[] OcclusionProps =
        {
    "_OcclusionMap", "_Occlusion", "_AOMap", "_AmbientOcclusionMap"
};

        private static readonly string[] ColorProps =
        {
    "_BaseColor", "_Color", "_Tint", "_MainColor"
};

        /// <summary>
        /// Fix renderers + repair broken shaders by swapping to a URP shader and copying over textures/colors.
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
                MaterialCorrection(Render, BundledContentHolder.Instance.UrpShader);
            }
            if (FaceMesh != null)
            {
                FaceMesh.shadowCastingMode = ShadowCastingMode.Off;
                EnsureShadowOnlyClone(FaceMesh, layer);
            }
        }
        /// <summary>
        /// Fix renderers + repair broken shaders by swapping to a URP shader and copying over textures/colors.
        /// </summary>
        public static void RemoteRenderMeshSettings(int layer, int skinnedMeshRendererLength, SkinnedMeshRenderer[] skinnedMeshRenderers)
        {
            for (int index = 0; index < skinnedMeshRendererLength; index++)
            {
                var r = skinnedMeshRenderers[index];
                r.updateWhenOffscreen = false;
                r.forceMatrixRecalculationPerRender = false;
                r.gameObject.layer = layer;
                MaterialCorrection(r, BundledContentHolder.Instance.UrpShader);
            }
        }
        public static List<SkinnedMeshRenderer> LocalShadowClones = new List<SkinnedMeshRenderer>();
        private static void RemoveOldShadowClones()
        {
            int count = LocalShadowClones.Count;
            for (int Index = 0; Index < count; Index++)
            {
                SkinnedMeshRenderer Renderer = LocalShadowClones[Index];
                if (Renderer != null)
                {
                    GameObject.Destroy(Renderer.gameObject);
                }
            }
            LocalShadowClones.Clear();
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
                LocalShadowClones.Add(LocalShadowClone);
                // The whole point:
                LocalShadowClone.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                LocalShadowClone.receiveShadows = false;
                LocalShadowClone.lightProbeUsage = LightProbeUsage.Off;
                if (source.sharedMesh != null)
                {
                    LocalShadowClone.sharedMesh = source.sharedMesh;
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
            }
        }

        private static bool TryGetFirstColor(Material mat, out Color value, out string foundProp)
        {
            for (int i = 0; i < ColorProps.Length; i++)
            {
                string p = ColorProps[i];
                if (mat.HasProperty(p))
                {
                    value = mat.GetColor(p);
                    foundProp = p;
                    return true;
                }
            }
            value = Color.white;
            foundProp = string.Empty;
            return false;
        }

        private static bool TryGetFirstTextureWithScaleAndOffset(Material mat, string[] props, out TexTransform result, out string foundProp)
        {
            for (int i = 0; i < props.Length; i++)
            {
                string p = props[i];
                if (TryGetTextureWithScaleAndOffset(mat, p, out result))
                {
                    foundProp = p;
                    return true;
                }
            }
            result = default;
            foundProp = string.Empty;
            return false;
        }
        public static bool TryGetTextureWithScaleAndOffset(Material mat, string propertyName, out TexTransform result)
        {
            result = default;

            if (mat == null)
            {
                return false;
            }

            Texture tex = mat.GetTexture(propertyName);
            if (tex == null)
            {
                return false;
            }

            Vector2 scale = mat.GetTextureScale(propertyName);
            Vector2 offset = mat.GetTextureOffset(propertyName);

            result = new TexTransform(tex, scale, offset);
            return true;
        }

        public static void MaterialCorrection(SkinnedMeshRenderer renderer, Shader fallbackUrpShader)
        {
            if (renderer == null)
            {
                return;
            }

            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                return;
            }

            if (fallbackUrpShader == null)
            {
                Debug.LogWarning("MaterialCorrection: fallbackUrpShader is null, cannot swap shaders.");
                return;
            }

            bool anyChanged = false;

            for (int mi = 0; mi < materials.Length; mi++)
            {
                var mat = materials[mi];
                if (mat == null)
                {
                    continue;
                }

                var shader = mat.shader;
                if (shader == null)
                {
                    continue;
                }

                bool shaderBroken = !shader.isSupported || (!string.IsNullOrEmpty(shader.name) && shader.name.Contains("InternalErrorShader"));

                if (!shaderBroken)
                {
                    continue;
                }
                bool hasAlbedo = TryGetFirstTextureWithScaleAndOffset(mat, AlbedoProps, out TexTransform albedo, out string albedoProp);
                bool hasNormal = TryGetFirstTextureWithScaleAndOffset(mat, NormalProps, out TexTransform normal, out string normalProp);
                bool hasMetal = TryGetFirstTextureWithScaleAndOffset(mat, MetallicProps, out TexTransform metal, out string metalProp);
                bool hasOcc = TryGetFirstTextureWithScaleAndOffset(mat, OcclusionProps, out TexTransform occ, out string occProp);
                bool hasColor = TryGetFirstColor(mat, out Color baseColor, out string colorProp);
                var fixedMat = new Material(fallbackUrpShader)
                {
                    name = mat.name + " (Fixed)"
                };
                if (hasAlbedo)
                {
                    fixedMat.SetTexture("_BaseMap", albedo.texture);
                    fixedMat.SetTextureScale("_BaseMap", albedo.scale);
                    fixedMat.SetTextureOffset("_BaseMap", albedo.offset);
                }
                if (hasColor)
                {
                    fixedMat.SetColor("_BaseColor", baseColor);
                }
                if (hasNormal)
                {
                    fixedMat.EnableKeyword("_NORMALMAP");
                    fixedMat.SetTexture("_BumpMap", normal.texture);
                    fixedMat.SetTextureScale("_BumpMap", normal.scale);
                    fixedMat.SetTextureOffset("_BumpMap", normal.offset);
                    fixedMat.SetFloat("_BumpScale", 0.2f);
                }
                if (hasMetal)
                {
                    fixedMat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    fixedMat.SetTexture("_MetallicGlossMap", metal.texture);
                    fixedMat.SetTextureScale("_MetallicGlossMap", metal.scale);
                    fixedMat.SetTextureOffset("_MetallicGlossMap", metal.offset);
                }
                fixedMat.SetFloat("_Metallic", 0.2f);
                fixedMat.SetFloat("_Smoothness", 0.2f);
                if (hasOcc)
                {
                    fixedMat.SetTexture("_OcclusionMap", occ.texture);
                    fixedMat.SetTextureScale("_OcclusionMap", occ.scale);
                    fixedMat.SetTextureOffset("_OcclusionMap", occ.offset);

                    fixedMat.SetFloat("_OcclusionStrength", 0.2f);
                }
                materials[mi] = fixedMat;
                anyChanged = true;
            }
            if (anyChanged)
            {
                renderer.sharedMaterials = materials;
            }
        }
        public struct TexTransform
        {
            public Texture texture;
            public Vector2 scale;
            public Vector2 offset;
            public TexTransform(Texture tex, Vector2 scale, Vector2 offset)
            {
                this.texture = tex;
                this.scale = scale;
                this.offset = offset;
            }
        }
    }
}
