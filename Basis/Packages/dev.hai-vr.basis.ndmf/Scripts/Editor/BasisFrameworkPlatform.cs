using System;
using System.Linq;
using Basis.Scripts.BasisSdk;
using nadena.dev.ndmf.platform;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.NDMF
{
    /// The class is called BasisFrameworkPlatform to follow NDMF's naming convention (INDMFPlatformProvider). Yes, we know Basis is not a platform.
    [NDMFPlatformProvider]
    public class BasisFrameworkPlatform : INDMFPlatformProvider
    {
        public static readonly INDMFPlatformProvider Instance = new BasisFrameworkPlatform();

        private static readonly string[] OurVisemesIndexedByMovement = {
            CommonAvatarInfo.Viseme_Silence, CommonAvatarInfo.Viseme_PP, CommonAvatarInfo.Viseme_FF, CommonAvatarInfo.Viseme_TH,
            CommonAvatarInfo.Viseme_DD, CommonAvatarInfo.Viseme_kk, CommonAvatarInfo.Viseme_CH, CommonAvatarInfo.Viseme_SS,
            CommonAvatarInfo.Viseme_nn, CommonAvatarInfo.Viseme_RR, CommonAvatarInfo.Viseme_aa, CommonAvatarInfo.Viseme_E,
            CommonAvatarInfo.Viseme_ih, CommonAvatarInfo.Viseme_oh, CommonAvatarInfo.Viseme_ou
        };

        public string QualifiedName => "org.basisvr.basis-framework";
        public string DisplayName => "Basis Framework";
        public Texture2D? Icon => null;
        public Type AvatarRootComponentType => typeof(BasisAvatar);
        public bool HasNativeConfigData => true;

        public BuildUIElement? CreateBuildUI()
        {
            return new BasisFrameworkBuildUI();
        }

        public CommonAvatarInfo ExtractCommonAvatarInfo(GameObject avatarRoot)
        {
            var basisAvatar = avatarRoot.GetComponent<BasisAvatar>();

            var cai = new CommonAvatarInfo();
            // The following is based on nadena.dev.ndmf.vrchat.VRChatPlatform
            cai.EyePosition = avatarRoot.transform.InverseTransformVector(new Vector3(0, basisAvatar.AvatarEyePosition.x, basisAvatar.AvatarEyePosition.y));
            if (basisAvatar.FaceVisemeMesh != null && basisAvatar.FaceVisemeMesh.sharedMesh != null)
            {
                cai.VisemeRenderer = basisAvatar.FaceVisemeMesh;

                var sharedMesh = basisAvatar.FaceVisemeMesh.sharedMesh;
                for (var movement = 0; movement < OurVisemesIndexedByMovement.Length; movement++)
                {
                    var ourViseme = OurVisemesIndexedByMovement[movement];
                    if (TryGet(sharedMesh, basisAvatar.FaceVisemeMovement[movement], out var blendShape)) cai.VisemeBlendshapes[ourViseme] = blendShape;
                }
            }

            return cai;
        }

        public void InitFromCommonAvatarInfo(GameObject avatarRoot, CommonAvatarInfo cai)
        {
            if (!avatarRoot.TryGetComponent<BasisAvatar>(out var basisAvatar))
            {
                basisAvatar = avatarRoot.AddComponent<BasisAvatar>();
                // The following is based on nadena.dev.ndmf.vrchat.VRChatPlatform:
                // Initialize array SerializeFields with empty array instances
                EditorUtility.CopySerialized(basisAvatar, basisAvatar);
            }

            if (cai.EyePosition != null)
            {
                var transformVector = avatarRoot.transform.TransformVector(cai.EyePosition.Value);
                basisAvatar.AvatarEyePosition = new Vector2(transformVector.y, transformVector.z);
            }

            if (cai.VisemeRenderer != null && cai.VisemeRenderer.sharedMesh != null)
            {
                basisAvatar.FaceVisemeMesh = cai.VisemeRenderer;

                var sharedMesh = cai.VisemeRenderer.sharedMesh;
                var blendShapeNames = Enumerable.Range(0, basisAvatar.FaceVisemeMesh.sharedMesh.blendShapeCount)
                    .Select(i => sharedMesh.GetBlendShapeName(i))
                    .ToList();
                for (var visemeMovementIndex = 0; visemeMovementIndex < OurVisemesIndexedByMovement.Length; visemeMovementIndex++)
                {
                    var caiViseme = OurVisemesIndexedByMovement[visemeMovementIndex];
                    if (cai.VisemeBlendshapes.TryGetValue(caiViseme, out var caiBlendShapeName))
                    {
                        var blendShapeIndex = blendShapeNames.IndexOf(caiBlendShapeName);
                        if (blendShapeIndex >= 0)
                        {
                            basisAvatar.FaceVisemeMovement[visemeMovementIndex] = blendShapeIndex;
                        }
                    }
                }
            }
        }

        public bool CanInitFromCommonAvatarInfo(GameObject avatarRoot, CommonAvatarInfo info)
        {
            return true;
        }

        private bool TryGet(Mesh mesh, int blendShapeIndex, out string name)
        {
            if (blendShapeIndex < 0 || blendShapeIndex >= mesh.blendShapeCount)
            {
                name = null;
                return false;
            }

            name = mesh.GetBlendShapeName(blendShapeIndex);
            return true;
        }
    }

    public class BasisFrameworkBuildUI : BuildUIElement
    {
    }
}
