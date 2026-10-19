#if UNITY_EDITOR
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
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
        private BasisOsc.InspectorState _cachedState;
        private int _cachedStateKey;
        private EntityId _cachedTargetEntityId;
        private bool _hasCachedState;
        private bool _cachedPlayModeState;

        public override void OnInspectorGUI()
        {
            BasisOsc osc = (BasisOsc)target;
            RefreshInspectorState(osc);
            BasisOsc.InspectorState state = _cachedState;
            if (state == null)
            {
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("sdk.osc.inspector.stateUnavailable"), MessageType.Warning);
                DrawScriptField(osc);
                return;
            }

            DrawScriptField(osc);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.osc.inspector.stateTitle"), EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle(BasisEditorLocalization.Get("sdk.osc.inspector.activeAndEnabled"), state.IsActiveAndEnabled);
                EditorGUILayout.Toggle(BasisEditorLocalization.Get("sdk.osc.inspector.receiveAll"), state.ReceiveAll);
                EditorGUILayout.Toggle(BasisEditorLocalization.Get("sdk.osc.inspector.canPublish"), state.CanPublish);
                EditorGUILayout.TextField(BasisEditorLocalization.Get("sdk.osc.inspector.entityId"), state.EntityId ?? string.Empty);
                EditorGUILayout.TextField(BasisEditorLocalization.Get("sdk.osc.inspector.scope"), state.ScopeName ?? BasisEditorLocalization.Get("editor.common.none"));
                EditorGUILayout.TextField(BasisEditorLocalization.Get("sdk.osc.inspector.publishPrefix"), state.PublishPrefix ?? BasisEditorLocalization.Get("sdk.osc.inspector.publishPrefixUnavailable"));
                EditorGUILayout.TextField(BasisEditorLocalization.Get("sdk.osc.inspector.defaultSubscribePrefix"), state.DefaultSubscriptionPrefix ?? string.Empty);
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.onMessageListeners"), state.OnMessageListenerCount);
            }

            if (!state.HasScope)
            {
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("sdk.osc.inspector.noScope"), MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("sdk.osc.inspector.subscriptionCounts"), EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.exactTotal"), state.ExactSubscriptions.Length);
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.exactMessageCallbacks"), state.ExactCallbackCount);
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.exactValueCallbacks"), state.ExactValueCallbackCount);
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.prefixTotal"), state.PrefixSubscriptions.Length);
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.prefixMessageCallbacks"), state.PrefixCallbackCount);
                EditorGUILayout.IntField(BasisEditorLocalization.Get("sdk.osc.inspector.prefixValueCallbacks"), state.PrefixValueCallbackCount);
            }

            EditorGUILayout.Space();
            _showExactSubscriptions = EditorGUILayout.BeginFoldoutHeaderGroup(_showExactSubscriptions, BasisEditorLocalization.Get("sdk.osc.inspector.exactSubscriptions", state.ExactSubscriptions.Length));
            if (_showExactSubscriptions)
            {
                DrawStringList(state.ExactSubscriptions, BasisEditorLocalization.Get("sdk.osc.inspector.exactSubscriptionsEmpty"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _showExactRegistrations = EditorGUILayout.BeginFoldoutHeaderGroup(_showExactRegistrations, BasisEditorLocalization.Get("sdk.osc.inspector.exactSources", state.ExactRegistrationLines.Length));
            if (_showExactRegistrations)
            {
                DrawStringList(state.ExactRegistrationLines, BasisEditorLocalization.Get("sdk.osc.inspector.exactSourcesEmpty"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _showPrefixSubscriptions = EditorGUILayout.BeginFoldoutHeaderGroup(_showPrefixSubscriptions, BasisEditorLocalization.Get("sdk.osc.inspector.prefixSubscriptions", state.PrefixSubscriptions.Length));
            if (_showPrefixSubscriptions)
            {
                DrawStringList(state.PrefixSubscriptions, BasisEditorLocalization.Get("sdk.osc.inspector.prefixSubscriptionsEmpty"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            _showPrefixRegistrations = EditorGUILayout.BeginFoldoutHeaderGroup(_showPrefixRegistrations, BasisEditorLocalization.Get("sdk.osc.inspector.prefixSources", state.PrefixRegistrationLines.Length));
            if (_showPrefixRegistrations)
            {
                DrawStringList(state.PrefixRegistrationLines, BasisEditorLocalization.Get("sdk.osc.inspector.prefixSourcesEmpty"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            if (!Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(BasisEditorLocalization.Get("sdk.osc.inspector.playModeHint"), MessageType.None);
            }
        }

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

        private void RefreshInspectorState(BasisOsc osc)
        {
            if (osc == null)
            {
                _cachedState = null;
                _hasCachedState = false;
                return;
            }

            bool isPlaying = Application.isPlaying;
            int stateKey = osc.GetInspectorCacheKey();
            EntityId targetEntityId = osc.GetEntityId();
            if (_hasCachedState && _cachedState != null && _cachedStateKey == stateKey && _cachedPlayModeState == isPlaying && _cachedTargetEntityId.Equals(targetEntityId))
            {
                return;
            }

            _cachedState = osc.GetInspectorState();
            _cachedStateKey = stateKey;
            _cachedTargetEntityId = targetEntityId;
            _cachedPlayModeState = isPlaying;
            _hasCachedState = true;
        }

        private static void DrawScriptField(BasisOsc osc)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                MonoScript script = MonoScript.FromMonoBehaviour(osc);
                EditorGUILayout.ObjectField(BasisEditorLocalization.Get("sdk.cilbox.scan.card.script"), script, typeof(MonoScript), false);
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
                int valueCount = values.Length;
                for (int i = 0; i < valueCount; i++)
                {
                    EditorGUILayout.TextField(values[i] ?? string.Empty);
                }
            }
        }
    }
}
#endif
