using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [CustomEditor(typeof(StreamedAvatarFeatureAssist))]
    public class StreamedAvatarFeatureAssistEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (GUILayout.Button("Submit random"))
            {
                SubmitOne();
            }
            if (GUILayout.Button("Submit random * 10"))
            {
                for (var i = 0; i < 10; i++)
                {
                    SubmitOne();
                }
            }
        }

        private void SubmitOne()
        {
            var assist = ((StreamedAvatarFeatureAssist)target);
            foreach (var feature in assist.features)
            {
                feature.QueueEvent(new StreamedAvatarFeaturePayload
                {
                    DeltaTime = assist.deltaTime,
                    FloatValues = Enumerable.Range(0, feature.valueArraySize)
                        .Select(_ => Random.Range(0f, 1f))
                        .ToArray()
                });
            }
        }
    }
}