using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [CustomEditor(typeof(BlendshapeActuationDefinitionFile))]
    public class BlendshapeActuationDefinitionFileEditor : Editor
    {
        private string _addressPrefix = "FT/";
        private string _address = "";
        private string _blendshape = "";
        private float _inStart;
        private float _inEnd = 1f;

        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField(((BlendshapeActuationDefinitionFile)target).comment ?? "", EditorStyles.wordWrappedLabel);
            DrawDefaultInspector();

            EditorGUILayout.BeginHorizontal();
            _addressPrefix = EditorGUILayout.TextField(_addressPrefix, GUILayout.Width(50)).Trim();
            _address = EditorGUILayout.TextField(_address).Trim();
            _inStart = EditorGUILayout.FloatField(_inStart, GUILayout.Width(30));
            EditorGUILayout.LabelField("->", GUILayout.Width(30));
            _inEnd = EditorGUILayout.FloatField(_inEnd, GUILayout.Width(30));
            EditorGUILayout.LabelField("=>", GUILayout.Width(30));
            _blendshape = EditorGUILayout.TextField(_blendshape).Trim();
            EditorGUILayout.EndHorizontal();

            var address = _addressPrefix + _address;
            EditorGUI.BeginDisabledGroup(!address.Contains("Left") && !address.Contains("Right"));
            if (GUILayout.Button("Add (auto Left/Right)"))
            {
                var blendShape = !string.IsNullOrWhiteSpace(_blendshape) ? _blendshape : address.Substring(address.LastIndexOf("/", StringComparison.Ordinal) + 1);
                AddLeftRight(address, blendShape);
            }
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Add (this only)"))
            {
                var blendShape = !string.IsNullOrWhiteSpace(_blendshape) ? _blendshape : address.Substring(address.LastIndexOf("/", StringComparison.Ordinal) + 1);
                Add(address, blendShape);
            }
        }

        private void AddLeftRight(string address, string blendshape)
        {
            var def = (BlendshapeActuationDefinitionFile)target;
            Undo.RecordObject(def, "Add new entry");
            var isLeft = address.Contains("Left") && blendshape.Contains("Left");
            var isRight = address.Contains("Right") && blendshape.Contains("Right");
            if (isLeft ^ isRight)
            {
                def.definitions = def.definitions.Concat(new[]
                {
                    new BlendshapeActuationDefinition
                    {
                        address = address,
                        inStart = _inStart,
                        inEnd = _inEnd,
                        outStart = 0f,
                        outEnd = 1f,
                        curve = AnimationCurve.Linear(0, 0, 1, 1),
                        blendshapes = new [] { blendshape }
                    },
                    new BlendshapeActuationDefinition
                    {
                        address = (isLeft ? address.Replace("Left", "Right") : address.Replace("Right", "Left")),
                        inStart = _inStart,
                        inEnd = _inEnd,
                        outStart = 0f,
                        outEnd = 1f,
                        curve = AnimationCurve.Linear(0, 0, 1, 1),
                        blendshapes = new []{ isLeft ? blendshape.Replace("Left", "Right") : blendshape.Replace("Right", "Left") }
                    }

                }).ToArray();
            }
        }

        private void Add(string address, string blendshape)
        {
            var def = (BlendshapeActuationDefinitionFile)target;
            Undo.RecordObject(def, "Add new entry");
            def.definitions = def.definitions.Concat(new[]
            {
                new BlendshapeActuationDefinition
                {
                    address = address,
                    inStart = _inStart,
                    inEnd = _inEnd,
                    outStart = 0f,
                    outEnd = 1f,
                    curve = AnimationCurve.Linear(0, 0, 1, 1),
                    blendshapes = new [] { blendshape }
                }
            }).ToArray();
        }
    }
}
