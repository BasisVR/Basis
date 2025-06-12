using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Helpers.Editor;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Scripts.Editor
{
    public class BasisAvatarGizmoEditor : MonoBehaviour
    {
        public static void UpdateGizmos(BasisAvatarSDKInspector inspector, BasisAvatar avatar)
        {
            if (inspector == null)
            {
                Debug.LogError("Inspector was null!");
                return;
            }
            if (avatar.Animator == null)
            {
                avatar.TryGetComponent(out avatar.Animator);
            }
            if (avatar == null || avatar.Animator == null)
            {
                Debug.LogError("Avatar or its Animator was null!");
                return;
            }

            float3 bottom = avatar.Animator.transform.position;
            Vector2 previousAvatarEyePosition = avatar.AvatarEyePosition;
            Vector2 previousAvatarMouthPosition = avatar.AvatarMouthPosition;
            UpdateAvatarPosition(Color.green, ref avatar.AvatarEyePosition, inspector.AvatarEyePositionState, avatar.transform.rotation, bottom, previousAvatarEyePosition, BasisSDKConstants.avatarEyePositionField, inspector.uiElementsRoot, avatar);
            UpdateAvatarPosition(Color.blue, ref avatar.AvatarMouthPosition, inspector.AvatarMouthPositionState, avatar.transform.rotation, bottom, previousAvatarMouthPosition, BasisSDKConstants.avatarMouthPositionField, inspector.uiElementsRoot, avatar);
        }

        private static void UpdateAvatarPosition(Color GizmoColor,ref Vector2 avatarPosition, bool positionState, Quaternion rotation, Vector3 bottom, Vector2 previousPosition, string positionField, VisualElement uiElementsRoot, BasisAvatar avatar)
        {
            if (!positionState)
            {
                return;
            }
            Handles.color = GizmoColor;
            Vector3 ConvertedToVector3 = BasisHelpers.AvatarPositionConversion(avatarPosition);

            Vector3 worldSpaceAvatarPosition = BasisHelpers.ConvertFromLocalSpace(ConvertedToVector3, bottom);
            BasisHelpersGizmo.PositionHandler(ref worldSpaceAvatarPosition, rotation);
            Handles.DrawWireDisc(worldSpaceAvatarPosition, Vector3.forward, 0.01f);
            Vector3 convertedPosition = BasisHelpers.ConvertToLocalSpace(worldSpaceAvatarPosition, bottom);
            avatarPosition = BasisHelpers.AvatarPositionConversion(convertedPosition);


            if (avatarPosition != previousPosition)
            {
                BasisHelpersGizmo.SetValueVector2Field(uiElementsRoot, positionField, avatarPosition);
                EditorUtility.SetDirty(avatar);
            }
        }
    }
}
