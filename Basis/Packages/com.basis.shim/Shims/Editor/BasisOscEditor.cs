#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Basis.Shims.Editor
{
    [CustomEditor(typeof(BasisOsc))]
    internal class BasisOscEditor : UnityEditor.Editor
    {
        private bool _showExactSubscriptions = true;
        private bool _showExactRegistrations = true;
        private bool _showPrefixSubscriptions = true;
        private bool _showPrefixRegistrations = true;

        public override void OnInspectorGUI()
        {
            BasisOsc osc = (BasisOsc)target;
            BasisOsc.InspectorState state = osc.GetInspectorState();

            DrawScriptField(osc);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("OSC State", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Active And Enabled", state.IsActiveAndEnabled);
                EditorGUILayout.Toggle("Receive All", state.ReceiveAll);
                EditorGUILayout.Toggle("Can Publish", state.CanPublish);
                EditorGUILayout.TextField("Entity Id", state.EntityId ?? string.Empty);
                EditorGUILayout.TextField("Scope", state.ScopeName ?? "None");
                EditorGUILayout.TextField("Publish Prefix", state.PublishPrefix ?? "Unavailable");
                EditorGUILayout.TextField("Default Subscribe Prefix", state.DefaultSubscriptionPrefix ?? string.Empty);
                EditorGUILayout.IntField("OnMessage Listeners", state.OnMessageListenerCount);
            }

            if (!state.HasScope)
            {
                EditorGUILayout.HelpBox(
                    "No OSC scope was resolved from a BasisAvatar, BasisProp, or BasisScene ancestor. Relative addresses will default to /avatar/parameters until the object is under a recognized scope.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Subscription Counts", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField("Exact Total", state.ExactSubscriptions.Length);
                EditorGUILayout.IntField("Exact Passive", state.PassiveExactCount);
                EditorGUILayout.IntField("Exact Message Callback Addresses", state.ExactCallbackCount);
                EditorGUILayout.IntField("Exact Value Callback Addresses", state.ExactValueCallbackCount);
                EditorGUILayout.IntField("Prefix Total", state.PrefixSubscriptions.Length);
                EditorGUILayout.IntField("Prefix Passive", state.PassivePrefixCount);
                EditorGUILayout.IntField("Prefix Message Callback Addresses", state.PrefixCallbackCount);
                EditorGUILayout.IntField("Prefix Value Callback Addresses", state.PrefixValueCallbackCount);
            }

            EditorGUILayout.Space();
            _showExactSubscriptions = EditorGUILayout.BeginFoldoutHeaderGroup(_showExactSubscriptions, $"Exact Subscriptions ({state.ExactSubscriptions.Length})");
            if (_showExactSubscriptions)
            {
                DrawStringList(state.ExactSubscriptions, "No exact subscriptions registered.");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _showExactRegistrations = EditorGUILayout.BeginFoldoutHeaderGroup(_showExactRegistrations, $"Exact Registration Sources ({state.ExactRegistrationLines.Length})");
            if (_showExactRegistrations)
            {
                DrawStringList(state.ExactRegistrationLines, "No exact registration sources tracked.");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _showPrefixSubscriptions = EditorGUILayout.BeginFoldoutHeaderGroup(_showPrefixSubscriptions, $"Prefix Subscriptions ({state.PrefixSubscriptions.Length})");
            if (_showPrefixSubscriptions)
            {
                DrawStringList(state.PrefixSubscriptions, "No prefix subscriptions registered.");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _showPrefixRegistrations = EditorGUILayout.BeginFoldoutHeaderGroup(_showPrefixRegistrations, $"Prefix Registration Sources ({state.PrefixRegistrationLines.Length})");
            if (_showPrefixRegistrations)
            {
                DrawStringList(state.PrefixRegistrationLines, "No prefix registration sources tracked.");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            if (!Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Subscription lists are most useful in play mode after runtime subscriptions have been registered.", MessageType.None);
            }
        }

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

        private static void DrawScriptField(BasisOsc osc)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                MonoScript script = MonoScript.FromMonoBehaviour(osc);
                EditorGUILayout.ObjectField("Script", script, typeof(MonoScript), false);
            }
        }

        private static void DrawStringList(string[] values, string emptyMessage)
        {
            if (values == null || values.Length == 0)
            {
                EditorGUILayout.HelpBox(emptyMessage, MessageType.None);
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                for (int i = 0; i < values.Length; i++)
                {
                    EditorGUILayout.TextField(values[i] ?? string.Empty);
                }
            }
        }
    }
}
#endif
