#if !BASIS_FRAMEWORK_EXISTS
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Scripts.BasisSdk.Players.Editor
{
    // Drives the live preview avatar's animator parameters from an inspector foldout.
    // Mounted by BasisAvatarSDKInspector when the SDK preview is the active driver
    // (framework absent). Generic over Animator.parameters — works for the locomotion
    // hashes (VelocityX/VelocityZ/IsCrouched/...) and any custom FX-layer params alike.
    internal static class BasisEditorPreviewParametersFoldout
    {
        public static VisualElement Create()
        {
            var root = new VisualElement();
            root.style.marginTop = 8;

            var imgui = new IMGUIContainer(Draw);
            root.Add(imgui);

            void OnStateChanged() => imgui.MarkDirtyRepaint();
            BasisEditorPreviewLocalPlayer.StateChanged += OnStateChanged;
            root.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                BasisEditorPreviewLocalPlayer.StateChanged -= OnStateChanged;
            });

            return root;
        }

        private static void Draw()
        {
            var avatar = BasisEditorPreviewLocalPlayer.ActiveAvatar;
            if (avatar == null || avatar.Animator == null || avatar.Animator.runtimeAnimatorController == null)
                return;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Preview Parameters", EditorStyles.boldLabel);

            if (GUILayout.Button("Stop Preview"))
            {
                BasisEditorPreviewLocalPlayer.DisposeActive();
                return;
            }

            Animator anim = avatar.Animator;
            foreach (AnimatorControllerParameter p in anim.parameters)
            {
                if (anim.IsParameterControlledByCurve(p.nameHash)) continue;

                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                    {
                        float v = anim.GetFloat(p.nameHash);
                        float nv = EditorGUILayout.Slider(p.name, v, -1f, 1f);
                        if (!Mathf.Approximately(v, nv)) anim.SetFloat(p.nameHash, nv);
                        break;
                    }
                    case AnimatorControllerParameterType.Int:
                    {
                        int v = anim.GetInteger(p.nameHash);
                        int nv = EditorGUILayout.IntField(p.name, v);
                        if (v != nv) anim.SetInteger(p.nameHash, nv);
                        break;
                    }
                    case AnimatorControllerParameterType.Bool:
                    {
                        bool v = anim.GetBool(p.nameHash);
                        bool nv = EditorGUILayout.Toggle(p.name, v);
                        if (v != nv) anim.SetBool(p.nameHash, nv);
                        break;
                    }
                    case AnimatorControllerParameterType.Trigger:
                    {
                        if (GUILayout.Button(p.name)) anim.SetTrigger(p.nameHash);
                        break;
                    }
                }
            }
        }
    }
}
#endif
