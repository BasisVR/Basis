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
            avatar.AnimatorHumanScale = Vector3.one / avatar.Animator.humanScale;
            float3 bottom = avatar.Animator.transform.position;
            Vector2 previousAvatarEyePosition = avatar.AvatarEyePosition;
            Vector2 previousAvatarMouthPosition = avatar.AvatarMouthPosition;
            UpdateAvatarPosition(Color.green, ref avatar.AvatarEyePosition, inspector.AvatarEyePositionState, avatar.transform.rotation, bottom, previousAvatarEyePosition, BasisSDKConstants.avatarEyePositionField, inspector.uiElementsRoot, avatar);
            UpdateAvatarPosition(Color.blue, ref avatar.AvatarMouthPosition, inspector.AvatarMouthPositionState, avatar.transform.rotation, bottom, previousAvatarMouthPosition, BasisSDKConstants.avatarMouthPositionField, inspector.uiElementsRoot, avatar);
        }

        private static void UpdateAvatarPosition(
            Color GizmoColor,
            ref Vector2 avatarPosition,
            bool positionState,
            Quaternion rotation,
            Vector3 bottom,
            Vector2 previousPosition,
            string positionField,
            VisualElement uiElementsRoot,
            BasisAvatar avatar)
        {
            if (!positionState)
                return;

#if UNITY_EDITOR
            Handles.color = GizmoColor;
#endif

            // Convert the 2D "avatarPosition" (y,z) into a local-space Vector3 (x=0).
            Vector3 localAvatarPos = BasisHelpers.AvatarPositionConversion(avatarPosition);

            // ✅ Convert from LOCAL → WORLD using origin + rotation
            Vector3 worldSpaceAvatarPosition = BasisHelpers.ConvertFromLocalSpace(localAvatarPos, bottom, rotation);

            // Let your gizmo handler move the point in world space (already rotated correctly)
            BasisHelpersGizmo.PositionHandler(ref worldSpaceAvatarPosition, rotation);

#if UNITY_EDITOR
            Handles.DrawWireDisc(worldSpaceAvatarPosition, Vector3.forward, 0.01f);
#endif

            // ✅ Convert back WORLD → LOCAL using inverse rotation
            Vector3 newLocalPos = BasisHelpers.ConvertToLocalSpace(worldSpaceAvatarPosition, bottom, rotation);

            // Back to your 2D representation
            avatarPosition = BasisHelpers.AvatarPositionConversion(newLocalPos);

            if (avatarPosition != previousPosition)
            {
                BasisHelpersGizmo.SetValueVector2Field(uiElementsRoot, positionField, avatarPosition);
#if UNITY_EDITOR
                EditorUtility.SetDirty(avatar);
#endif
            }
        }
    }
}
