using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Basis.Scripts.Editor
{
    public static class BasisAutomaticSetupAvatarEditor
    {
        // Offset to estimate the mouth position relative to the head
        public static Vector3 mouthOffset = new Vector3(0f, 0.025f, 0.15f);
        public static void TryToAutomatic(BasisAvatarSDKInspector Inspector)
        {
            BasisAvatar avatar = Inspector.Avatar;
            if (avatar != null)
            {
                if (TryFindOrCheckAvatar(avatar))
                {
                    if (CheckAnimator(avatar))
                    {
                        if (TryFindNeckAndHead(avatar, out Transform Neck, out Transform Head))
                        {
                            TrySetAvatarEyePosition(avatar);
                            TrySetAvatarMouthPosition(avatar, Head);
                        }
                    }
                }
                else
                {
                    Debug.LogError("Animator component not found on GameObject " + avatar.gameObject.name);
                }
                UpdateAvatarRenders(avatar);

                EditorUtility.SetDirty(avatar);
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.LogError("Avatar instance is null.");
            }
        }
        private static bool CheckAnimator(BasisAvatar avatar)
        {
            if (!avatar.Animator.isHuman)
            {
                Debug.LogError("Animator is not human.");
                return false;
            }
            if (!avatar.Animator.hasTransformHierarchy)
            {
                Debug.LogError("Animator doesn't have a transform hierarchy.");
                return false;
            }
            if (avatar.Animator.avatar == null)
            {
                Debug.LogError("Animator avatar is null.");
                return false;
            }
            return true;
        }
        private static bool TryFindOrCheckAvatar(BasisAvatar avatar)
        {
            if (avatar.Animator == null)
            {
                Debug.Log("Animator component not found on GameObject Attempting Load" + avatar.gameObject);

                if (avatar.gameObject.TryGetComponent(out avatar.Animator))
                {
                    return true;
                }
                Animator Anim = avatar.gameObject.GetComponentInChildren<Animator>();
                if (Anim != null)
                {
                    avatar.Animator = Anim;
                    return true;
                }
            }
            else
            {
                return true;
            }
            return false;
        }

        private static bool TryFindNeckAndHead(BasisAvatar avatar, out Transform Neck, out Transform Head)
        {
            Head = null;
            if (!BasisHelpers.TryGetTransformBone(avatar.Animator, HumanBodyBones.Neck, out Neck))
            {
                Debug.LogError("Missing Neck in Animator " + avatar.Animator);
                return false;
            }
            if (!BasisHelpers.TryGetTransformBone(avatar.Animator, HumanBodyBones.Head, out Head))
            {
                Debug.LogError("Missing Head in Animator " + avatar.Animator);
                return false;
            }
            return true;
        }

        private static void TrySetAvatarEyePosition(BasisAvatar avatar)
        {
            if (avatar.AvatarEyePosition != Vector2.zero)
            {
                return;
            }
            if (BasisHelpers.TryGetVector3Bone(avatar.Animator, HumanBodyBones.LeftEye, out Vector3 LeftEye) && BasisHelpers.TryGetVector3Bone(avatar.Animator, HumanBodyBones.RightEye, out Vector3 RightEye))
            {
                Vector3 EyePosition = Vector3.Lerp(LeftEye, RightEye, 0.5f);

                float3 Bottom = avatar.Animator.transform.position;
                Vector3 Space = BasisHelpers.ConvertToLocalSpace(EyePosition, Bottom);
                avatar.AvatarEyePosition = BasisHelpers.AvatarPositionConversion(Space);
                EditorUtility.SetDirty(avatar);
            }
        }
        private static void TrySetAvatarMouthPosition(BasisAvatar avatar, Transform Head)
        {
            if (avatar.AvatarMouthPosition != Vector2.zero)
            {
                return;
            }

            if (BasisHelpers.TryGetVector3Bone(avatar.Animator, HumanBodyBones.LeftFoot, out Vector3 LeftFoot) && BasisHelpers.TryGetVector3Bone(avatar.Animator, HumanBodyBones.RightFoot, out Vector3 RightFoot))
            {
                Vector3 Bottom = Vector3.Lerp(LeftFoot, RightFoot, 0.5f);
                Vector3 estimatedMouthPosition = Head.position + Head.TransformDirection(BasisHelpers.ScaleVector(mouthOffset));//height

                Vector3 Space = BasisHelpers.ConvertToLocalSpace(estimatedMouthPosition, Bottom);
                avatar.AvatarMouthPosition = BasisHelpers.AvatarPositionConversion(Space);
                EditorUtility.SetDirty(avatar);
            }
        }
        private static void UpdateAvatarRenders(BasisAvatar avatar)
        {
            avatar.Renders = avatar.GetComponentsInChildren<Renderer>(true);
        }
    }
}
