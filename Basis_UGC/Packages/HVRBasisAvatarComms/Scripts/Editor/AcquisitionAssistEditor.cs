using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [CustomEditor(typeof(AcquisitionAssist))]
    public class AcquisitionAssistEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var assist = (AcquisitionAssist)target;
            if (assist.definitionFile)
            {
                var groupings = assist.definitionFile.definitions.GroupBy(definition => definition.address, definition => definition);
                foreach (var definitions in groupings)
                {
                    var address = definitions.First().address;
                    assist.memory.TryGetValue(address, out var value);
                    var allInEnds = definitions.Select(definition => definition.inStart).Concat(definitions.Select(definition => definition.inEnd)).ToArray();
                    var min = allInEnds.Min();
                    var max = allInEnds.Max();
                    var newValue = EditorGUILayout.Slider(address, value, min, max);
                    if (value != newValue)
                    {
                        assist.memory[address] = newValue;
                        assist.acquisitionService.Submit(address, newValue);
                    }
                }
            }
            foreach (var def in assist.toggles)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button($"{def} = false"))
                {
                    assist.acquisitionService.Submit(def, 0f);
                }
                if (GUILayout.Button($"{def} = true"))
                {
                    assist.acquisitionService.Submit(def, 1f);
                }
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}