using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Basis.Scripts.BasisSdk.Players.Editor
{
    // Drives the live preview avatar's animator parameters from an inspector foldout.
    // Mounted into BasisAvatarSDKInspector via PreviewFoldoutFactory delegate registered
    // by BasisAvatarEditorPreviewBootstrap at editor load. Generic over Animator.parameters
    // — works for the locomotion hashes (VelocityX/VelocityZ/IsCrouched/...) and any
    // custom FX-layer params alike.
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

        private static int _selectedMicIndex;

        private static void Draw()
        {
            var avatar = BasisEditorPreviewLocalPlayer.ActiveAvatar;
            if (avatar == null || avatar.Animator == null || avatar.Animator.runtimeAnimatorController == null)
                return;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Preview Parameters", EditorStyles.boldLabel);

            if (GUILayout.Button("Stop Preview"))
            {
                // Exiting play mode is the cleanup. Unity reverts the play-mode scene,
                // which destroys the preview root + clone; OnPlayModeStateChanged clears statics.
                EditorApplication.ExitPlaymode();
                return;
            }

            DrawMicSection();

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

        private static void DrawMicSection()
        {
            var mic = BasisEditorPreviewLocalPlayer.ActiveSimMic;
            if (mic == null) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Microphone", EditorStyles.boldLabel);

            string[] devices = Microphone.devices;
            if (devices.Length == 0)
            {
                EditorGUILayout.HelpBox("No microphones detected.", MessageType.Info);
                return;
            }

            // Keep the dropdown selection in sync with whatever the mic is currently using
            if (mic.IsCapturing && !string.IsNullOrEmpty(mic.DeviceName))
            {
                int idx = System.Array.IndexOf(devices, mic.DeviceName);
                if (idx >= 0) _selectedMicIndex = idx;
            }
            _selectedMicIndex = Mathf.Clamp(_selectedMicIndex, 0, devices.Length - 1);

            using (new EditorGUI.DisabledScope(mic.IsCapturing))
            {
                _selectedMicIndex = EditorGUILayout.Popup("Device", _selectedMicIndex, devices);
            }

            if (mic.IsCapturing)
            {
                if (GUILayout.Button("Stop Mic")) mic.StopCapture();
            }
            else
            {
                if (GUILayout.Button("Start Mic"))
                {
                    if (!mic.StartCapture(devices[_selectedMicIndex]))
                        Debug.LogWarning("[Basis SDK Preview] Failed to start mic capture.");
                }
            }
        }
    }
}
