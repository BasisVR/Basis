using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Basis.Setup
{
    public class BasisSetupWindow : EditorWindow
    {
        private Vector2 _scroll;

        [MenuItem("Basis/Setup/Configuration Window", false, 1)]
        public static void ShowWindow()
        {
            BasisSetupWindow window = GetWindow<BasisSetupWindow>("Basis Setup");
            window.minSize = new Vector2(560, 420);
            window.Show();
        }

        private void OnEnable()
        {
            BasisSetupRegistry.Rebuild();
        }

        private void OnGUI()
        {
            DrawHeader();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Create Missing Assets", GUILayout.Height(24)))
                {
                    BasisSetupRunner.ApplyAll(BasisSetupMode.EnsureExists);
                }

                if (GUILayout.Button("Update All To Latest", GUILayout.Height(24)))
                {
                    BasisSetupRunner.UpdateAllMenu();
                }

                if (GUILayout.Button("Rescan", GUILayout.Width(80), GUILayout.Height(24)))
                {
                    BasisSetupRegistry.Rebuild();
                }
            }

            EditorGUILayout.Space(6);

            using (var scope = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scope.scrollPosition;
                string category = null;
                foreach (IBasisSetupModule module in BasisSetupRegistry.Modules)
                {
                    if (module.Category != category)
                    {
                        category = module.Category;
                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField(category, EditorStyles.boldLabel);
                    }

                    DrawModuleRow(module);
                }
            }
        }

        private void DrawModuleRow(IBasisSetupModule module)
        {
            BasisSetupStatus status = module.GetStatus();
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                DrawStatusChip(status);
                EditorGUILayout.LabelField(new GUIContent(module.DisplayName, module.Key), GUILayout.MinWidth(160));

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(status != BasisSetupStatus.Missing))
                {
                    if (GUILayout.Button("Create", GUILayout.Width(70)))
                    {
                        Run(module, BasisSetupMode.EnsureExists);
                    }
                }

                using (new EditorGUI.DisabledScope(status == BasisSetupStatus.NotApplicable))
                {
                    if (GUILayout.Button("Update", GUILayout.Width(70)))
                    {
                        Run(module, BasisSetupMode.Update);
                    }
                }
            }
        }

        private void Run(IBasisSetupModule module, BasisSetupMode mode)
        {
            BasisSetupReport report = BasisSetupRunner.ApplyOne(module, mode);
            ShowNotification(new GUIContent($"{module.DisplayName}: {report.Message}"));
            Repaint();
        }

        private static void DrawStatusChip(BasisSetupStatus status)
        {
            Color color;
            string text;
            switch (status)
            {
                case BasisSetupStatus.Missing:
                    color = new Color(0.8f, 0.2f, 0.2f, 0.25f);
                    text = "Missing";
                    break;
                case BasisSetupStatus.Outdated:
                    color = new Color(0.85f, 0.65f, 0.15f, 0.3f);
                    text = "Outdated";
                    break;
                case BasisSetupStatus.UpToDate:
                    color = new Color(0.2f, 0.6f, 0.2f, 0.25f);
                    text = "Up to date";
                    break;
                case BasisSetupStatus.Error:
                    color = new Color(0.8f, 0.1f, 0.1f, 0.4f);
                    text = "Error";
                    break;
                default:
                    color = new Color(0.5f, 0.5f, 0.5f, 0.2f);
                    text = "N/A";
                    break;
            }

            Rect rect = GUILayoutUtility.GetRect(new GUIContent(text), EditorStyles.miniButton, GUILayout.Width(80));
            EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, text, EditorStyles.miniBoldLabel);
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Basis Project Configuration", EditorStyles.largeLabel);
            EditorGUILayout.LabelField(
                "Generate the Assets-level files Unity requires, and update older copies in place.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);
        }
    }
}
