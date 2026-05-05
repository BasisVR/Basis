using HVR.Basis.Comms.Editor;
using UnityEditor;
using UnityEngine;

namespace HVR.Vixxy.Editor
{
    [CustomEditor(typeof(HVRVixxyMenuItem))]
    public class HVRVixxyMenuItemEditor : UnityEditor.Editor
    {
        private VMenuItem _menuItem;

        public static bool _userViewFoldout = true;
        public static bool _creatorViewFoldout;

        internal const string CreatorView = "Creator View";

        private void OnEnable()
        {
            _menuItem = new VMenuItem(this);
        }

        public override void OnInspectorGUI()
        {
            var my = (HVRVixxyMenuItem)target;
            HVRAvatarCommsEditor.EnsureAvatarHasPrefab(my.transform);

            var isPlaying = Application.isPlaying;
            if (isPlaying)
            {
                EditorGUILayout.HelpBox(HVRVixxyLocalizationPhrase.MsgCannotEditInPlayMode, MessageType.Warning);
            }

            var anyChanged = false;
            _userViewFoldout = HaiEFCommon.LilFoldout(HVRVixxyLocalizationPhrase.UserViewLabel, "", _userViewFoldout, ref anyChanged);
            if (_userViewFoldout)
            {
                if (_menuItem.LayoutMenu()) return;
            }
            _creatorViewFoldout = HaiEFCommon.LilFoldout(CreatorView, "", _creatorViewFoldout, ref anyChanged);
            if (_creatorViewFoldout)
            {
                if (_menuItem.LayoutCreatorView()) return;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
