#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
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

    [MenuItem("Basis/Cursor")]
    public static void Open()
    {
        GetWindow<BasisCursorEditorWindow>("Basis Cursor");
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
        EditorGUILayout.LabelField("Basis Cursor Management", EditorStyles.boldLabel);
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
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Cursor Lock State",
                BasisCursorManagement.ActiveLockState().ToString());

            EditorGUILayout.LabelField("Cursor Visible",
                BasisCursorManagement.IsVisible().ToString());

            EditorGUILayout.LabelField("Ignoring Requests (VR)",
                Basis.Scripts.Device_Management.BasisDeviceManagement.IsCurrentModeVR().ToString());
        }
    }

    private void DrawCursorRequests()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Active Cursor Unlock Requests", EditorStyles.boldLabel);

            IReadOnlyList<string> requests = BasisCursorManagement.CursorUnlockRequestsDebug;

            if (requests == null || requests.Count == 0)
            {
                EditorGUILayout.LabelField("None");
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
            EditorGUILayout.LabelField("Basis Locks", EditorStyles.boldLabel);

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
            EditorGUILayout.LabelField("Locked", locked.ToString());

            var holders = ctx.ToList(); // snapshot (thread-safe because your LockContext locks internally)

            if (holders.Count == 0)
            {
                EditorGUILayout.LabelField("Holders", "None");
            }
            else
            {
                // Show as a compact line
                EditorGUILayout.LabelField("Holders", string.Join(", ", holders));

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
            EditorGUILayout.LabelField("Editor Actions", EditorStyles.boldLabel);

            if (GUILayout.Button("Force Unlock & Clear Cursor Requests"))
            {
                BasisCursorManagement.OnReset();
            }

            EditorGUILayout.Space(4);

            // Optional: nuke common locks (careful—this is global!)
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Clear LookRotation Lock List"))
                {
                    BasisLocks.GetContext(BasisLocks.LookRotation).Clear();
                }

                if (GUILayout.Button("Clear Movement Lock List"))
                {
                    BasisLocks.GetContext(BasisLocks.Movement).Clear();
                }

                if (GUILayout.Button("Clear Crouching Lock List"))
                {
                    BasisLocks.GetContext(BasisLocks.Crouching).Clear();
                }
            }
        }
    }
}
#endif
