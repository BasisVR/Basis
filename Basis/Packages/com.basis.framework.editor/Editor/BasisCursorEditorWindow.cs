#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using Basis.Editor.Localization;
using Basis.Scripts.Common; // BasisLocks

public class BasisCursorEditorWindow : EditorWindow
{
    private Vector2 cursorReqScroll;
    private Vector2 locksScroll;

    // Add more contexts here as you create them.
    private static readonly string[] LockContextsToShow =
    {
        BasisLocks.LookRotation,
        BasisLocks.Movement,
        BasisLocks.Crouching
    };

    [MenuItem("Basis/Settings/Cursor", false, 401)]
    public static void Open()
    {
        GetWindow<BasisCursorEditorWindow>(BasisEditorLocalization.Get("editor.cursorEditor.windowTitle"));
    }

    private void OnEnable()
    {
        BasisCursorManagement.OnCursorStateChange += RepaintOnCursorChange;
    }

    private void OnDisable()
    {
        BasisCursorManagement.OnCursorStateChange -= RepaintOnCursorChange;
    }

    private void RepaintOnCursorChange(CursorLockMode mode, bool visible) => Repaint();

    private void OnGUI()
    {
        BasisEditorUI.Header(BasisEditorLocalization.Get("editor.cursorEditor.header"),
            BasisEditorLocalization.Get("editor.cursorEditor.subtitle"));

        EditorGUILayout.Space();

        DrawRuntimeInfo();
        EditorGUILayout.Space();

        DrawCursorRequests();
        EditorGUILayout.Space();

        DrawBasisLocks();
        EditorGUILayout.Space();

        DrawActions();
    }

    private void DrawRuntimeInfo()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.cursorEditor.runtimeState"));

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.cursorEditor.lockState"),
                BasisCursorManagement.ActiveLockState().ToString());

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.cursorEditor.visible"),
                BasisCursorManagement.IsVisible().ToString());

            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.cursorEditor.ignoringRequestsVr"),
                Basis.Scripts.Device_Management.BasisDeviceManagement.IsCurrentModeVR().ToString());
        }
    }

    private void DrawCursorRequests()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.cursorEditor.unlockRequests"));

            IReadOnlyList<string> requests = BasisCursorManagement.CursorUnlockRequestsDebug;

            if (requests == null || requests.Count == 0)
            {
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.common.none"));
                return;
            }

            cursorReqScroll = EditorGUILayout.BeginScrollView(cursorReqScroll, GUILayout.MaxHeight(150));
            for (int i = 0; i < requests.Count; i++)
            {
                EditorGUILayout.LabelField("• " + requests[i]);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawBasisLocks()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.cursorEditor.locks"));

            locksScroll = EditorGUILayout.BeginScrollView(locksScroll, GUILayout.MaxHeight(260));

            for (int i = 0; i < LockContextsToShow.Length; i++)
            {
                string ctxName = LockContextsToShow[i];
                var ctx = BasisLocks.GetContext(ctxName);

                DrawLockContext(ctxName, ctx);
            }

            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawLockContext(string ctxName, BasisLocks.LockContext ctx)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            bool locked = ctx; // implicit bool operator uses Count > 0
            EditorGUILayout.LabelField(ctxName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.cursorEditor.locked"), locked.ToString());

            var holders = ctx.ToList(); // snapshot (thread-safe because your LockContext locks internally)

            if (holders.Count == 0)
            {
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.cursorEditor.holders"), BasisEditorLocalization.Get("editor.common.none"));
            }
            else
            {
                // Show as a compact line
                EditorGUILayout.LabelField(BasisEditorLocalization.Get("editor.cursorEditor.holders"), string.Join(", ", holders));

                // Optional: show as bullets too (uncomment if you prefer)
                // EditorGUILayout.Space(2);
                // foreach (var h in holders)
                //     EditorGUILayout.LabelField("• " + h);
            }
        }
    }

    private void DrawActions()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            BasisEditorUI.SectionTitle(BasisEditorLocalization.Get("editor.cursorEditor.actions"));

            if (GUILayout.Button(BasisEditorLocalization.Get("editor.cursorEditor.forceUnlock")))
            {
                BasisCursorManagement.OnReset();
            }

            EditorGUILayout.Space(4);

            // Optional: nuke common locks (careful—this is global!)
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(BasisEditorLocalization.Get("editor.cursorEditor.clearLookRotation")))
                {
                    BasisLocks.GetContext(BasisLocks.LookRotation).Clear();
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.cursorEditor.clearMovement")))
                {
                    BasisLocks.GetContext(BasisLocks.Movement).Clear();
                }

                if (GUILayout.Button(BasisEditorLocalization.Get("editor.cursorEditor.clearCrouching")))
                {
                    BasisLocks.GetContext(BasisLocks.Crouching).Clear();
                }
            }
        }
    }
}
#endif
