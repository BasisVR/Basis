using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using Basis.Scripts.BasisSdk;
using HVR.Vixxy;
using HVR.Vixxy.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HVR.Basis.Comms.Editor
{
    [CustomEditor(typeof(HVRAvatarComms))]
    [NoAutoStaticsCleanup]
    public class HVRAvatarCommsEditor : UnityEditor.Editor
    {
        private const string HVRNetworkingPrefabGuid = "630d3429b35a4c844b56751eb1d77d90";

        private static bool _editorOnlyTypeAttempted;
        private static Type _editorOnlyTypeNullable;

        private List<Renderer> _renderers = new();
        private List<Renderer> _renderersNotInControl = new();
        private List<string> _addresses = new();
        private List<HVRVixxyControl> _controls = new();
        private List<Component> _components = new();
        private List<Object> _assets = new();
        private readonly Dictionary<Transform, List<HVRVixxyControl>> _transformToControlsDict = new();

        private bool displayAvatarInfo;

        private void OnEnable()
        {
            var avatar = HVRCommsUtil.GetAvatar((HVRAvatarComms)target);
            if (avatar != null)
            {
                EnsureEditorOnlyResolved();

                var applicableTransforms = HVR_EditorHelpers.CollectAllNonEditorOnlyTransforms(avatar).ToHashSet();
                var renderers = applicableTransforms
                    .SelectMany(transform => transform.GetComponents<Renderer>())
                    .ToList();
                _controls = applicableTransforms
                    .SelectMany(transform => transform.GetComponents<HVRVixxyControl>())
                    .OrderByDescending(that => that.activations.Length)
                    .ThenByDescending(that => that.subjects.Sum(subject => subject.properties.Count))
                    .ToList();
                _components = applicableTransforms
                    .SelectMany(transform => transform.GetComponents<Component>())
                    .Where(that => that != null && that is not Transform)
                    .Where(that => that is not HVRVixxyControl and not HVRNetworkingCarrier and not HVRAvatarComms and not HVRVixxyMenuItem)
                    .Where(that => _editorOnlyTypeNullable == null || !_editorOnlyTypeNullable.IsAssignableFrom(that.GetType()))
                    .OrderBy(that => that.GetType().Name)
                    .ThenBy(that => that.name)
                    .ToList();
                foreach (var control in _controls)
                {
                    // FIXME: This doesn't describe the situation where a Control toggles the SMR but the GameObject never gets toggled.
                    var transformsAffectedByThisControl = control.activations
                        .Where(activation => activation.component != null
                                             && activation.component.GetType() == typeof(Transform)
                                             && applicableTransforms.Contains(activation.component.transform))
                        .Select(activation => activation.component.transform)
                        .SelectMany(transform => transform.GetComponentsInChildren<Transform>(true)
                            .Where(t => applicableTransforms.Contains(t)))
                        .Concat(control.activations
                            .Where(activation => activation.component != null
                                                 && typeof(Renderer).IsAssignableFrom(activation.component.GetType())
                                                 && applicableTransforms.Contains(activation.component.transform))
                            .Select(activation => activation.component.transform)
                        )
                        .ToHashSet();

                    foreach (var t in transformsAffectedByThisControl)
                    {
                        if (!_transformToControlsDict.ContainsKey(t))
                        {
                            _transformToControlsDict[t] = new List<HVRVixxyControl>();
                        }

                        _transformToControlsDict[t].Add(control);
                    }
                }

                (_renderers, _renderersNotInControl) = renderers.Partition(renderer => _transformToControlsDict.ContainsKey(renderer.transform));

                _addresses = applicableTransforms.SelectMany(transform => transform.GetComponents<HVRVixxyControl>())
                    .Select(control => control.address.TryResolvePath(out var result) ? result : null)
                    .Where(address => address != null)
                    .Distinct()
                    .OrderBy(address => address)
                    .ToList();

                _assets = _controls.SelectMany(control => control.ListAssets()).Distinct().ToList();
            }
        }

        private static void EnsureEditorOnlyResolved()
        {
            if (_editorOnlyTypeAttempted) return;
            _editorOnlyTypeAttempted = true;

            _editorOnlyTypeNullable = HVR_EditorHelpers.FindEditorOnlyTypeOrNull();
        }

        public override void OnInspectorGUI()
        {
            var my = (HVRAvatarComms)target;

            EditorGUILayout.HelpBox("This prefab was added automatically because your avatar contains a component that depends on the HVR Avatar Communication module.", MessageType.Info);
            if (Application.isPlaying)
            {
                DisplayRuntimeData(my);
            }
            else
            {
                var changed = false;
                displayAvatarInfo = HaiEFExternals_LilGUI.LilFoldout("Avatar Info", "", displayAvatarInfo, ref changed);
                if (displayAvatarInfo)
                {
                    EditorGUILayout.LabelField("Renderers", EditorStyles.boldLabel);
                    foreach (var renderer in _renderers.Concat(_renderersNotInControl))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(new GUIContent(""), renderer, typeof(Renderer), true);
                        EditorGUILayout.Toggle(GUIContent.none, renderer.gameObject.activeInHierarchy, GUILayout.Width(20));
                        EditorGUILayout.Toggle(GUIContent.none, renderer.enabled, GUILayout.Width(20));
                        EditorGUILayout.LabelField(HVR_EditorHelpers.ArrowRightSymbol, GUILayout.Width(20));
                        if (_transformToControlsDict.TryGetValue(renderer.transform, out var controls))
                        {
                            foreach (var control in controls)
                            {
                                EditorGUILayout.ObjectField(new GUIContent(""), control, typeof(HVRVixxyControl), true, GUILayout.Width(200));
                            }
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.Separator();

                    EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);
                    foreach (var control in _controls)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(new GUIContent(""), control, typeof(HVRVixxyControl), true);
                        EditorGUILayout.LabelField($"Toggles {control.activations.Length} objects, changes {control.subjects.Sum(subject => subject.properties.Count)} properties", GUILayout.Width(300));
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.Separator();

                    EditorGUILayout.LabelField("Assets inside controls", EditorStyles.boldLabel);
                    foreach (var asset in _assets)
                    {
                        EditorGUILayout.ObjectField(new GUIContent(""), asset, typeof(Object), true);
                    }
                    EditorGUILayout.Separator();

                    EditorGUILayout.LabelField("Components", EditorStyles.boldLabel);
                    foreach (var component in _components)
                    {
                        EditorGUILayout.ObjectField(new GUIContent(""), component, component.GetType(), true);
                    }
                    EditorGUILayout.Separator();

                    EditorGUILayout.LabelField("Addresses", EditorStyles.boldLabel);
                    foreach (var address in _addresses)
                    {
                        EditorGUILayout.TextField(address);
                    }
                    EditorGUILayout.Separator();
                }
            }
        }

        private void DisplayRuntimeData(HVRAvatarComms my)
        {
            AcquisitionServiceEditor.DisplayVariableStore(my.VariableStore);

            Repaint();
        }

        public static void EnsureAvatarHasPrefab(Transform myTransform)
        {
            var avi = myTransform.GetComponentInParent<BasisAvatar>(true);
            if (avi == null) return;

            var comms = avi.GetComponentInChildren<HVRAvatarComms>(true);
            var carrier = avi.GetComponentInChildren<HVRNetworkingCarrier>(true);
            if (comms == null || carrier == null)
            {
                if (GUID.TryParse(HVRNetworkingPrefabGuid, out var guid))
                {
                    var instance = PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetByGUID<GameObject>(guid), avi.transform);
                    EditorUtility.SetDirty(instance);
                }
            }
        }
    }
}
