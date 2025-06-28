using UnityEngine;
using UnityEditor;
using Basis.Scripts.Networking.Receivers;
using Basis.Scripts.Networking;
[CustomEditor(typeof(BasisNetworkManagement))]
public class BasisNetworkManagementEditor : Editor
{
    private SerializedProperty ipProp;
    private SerializedProperty portProp;
    private SerializedProperty passwordProp;

    private bool receiverArrayFoldout = true;
    private int selectedReceiverIndex = 0;
    private bool nativeArrayFoldout = false; // Foldout for NativeArray details
    private bool debugFoldout = false; // Foldout for debugging data
    private void OnEnable()
    {
        ipProp = serializedObject.FindProperty("Ip");
        portProp = serializedObject.FindProperty("Port");
        passwordProp = serializedObject.FindProperty("Password");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Reference to the target script
        BasisNetworkManagement networkManager = (BasisNetworkManagement)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Network Statistics", EditorStyles.boldLabel);

        // Editable fields using SerializedProperties
        EditorGUILayout.PropertyField(ipProp, new GUIContent("IP Address"));
        portProp.intValue = EditorGUILayout.IntField("Port", portProp.intValue);
        passwordProp.stringValue = EditorGUILayout.PasswordField("Password", passwordProp.stringValue);

        EditorGUILayout.LabelField("Total Players:", BasisNetworkManagement.Players.Count.ToString());
        EditorGUILayout.LabelField("Remote Players:", BasisNetworkManagement.RemotePlayers.Count.ToString());
        EditorGUILayout.LabelField("Joining Players:", BasisNetworkManagement.JoiningPlayers.Count.ToString());
        EditorGUILayout.LabelField("Receiver Count:", BasisNetworkManagement.ReceiverCount.ToString());

        if (BasisNetworkManagement.Instance == null)
        {
            serializedObject.ApplyModifiedProperties();
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transmitter Details", EditorStyles.boldLabel);
        if (BasisNetworkManagement.Instance.Transmitter != null)
        {
            EditorGUILayout.LabelField("Has Events:", BasisNetworkManagement.Instance.Transmitter.HasEvents.ToString());
            EditorGUILayout.LabelField("Timer:", BasisNetworkManagement.Instance.Transmitter.timer.ToString("F3"));
            EditorGUILayout.LabelField("Interval:", BasisNetworkManagement.Instance.Transmitter.interval.ToString("F3"));
            EditorGUILayout.LabelField("Smallest Distance to Another Player:", BasisNetworkManagement.Instance.Transmitter.SmallestDistanceToAnotherPlayer.ToString("F3"));

            EditorGUILayout.Space();
            nativeArrayFoldout = EditorGUILayout.Foldout(nativeArrayFoldout, "Native Arrays", true);
            if (nativeArrayFoldout)
            {
                EditorGUILayout.LabelField("Target Positions:", BasisNetworkManagement.Instance.Transmitter.targetPositions.IsCreated ? "Created" : "Not Created");
                EditorGUILayout.LabelField("Distances:", BasisNetworkManagement.Instance.Transmitter.distances.IsCreated ? "Created" : "Not Created");
                EditorGUILayout.LabelField("Distance Results:", BasisNetworkManagement.Instance.Transmitter.DistanceResults.IsCreated ? "Created" : "Not Created");
                EditorGUILayout.LabelField("Hearing Results:", BasisNetworkManagement.Instance.Transmitter.HearingResults.IsCreated ? "Created" : "Not Created");
                EditorGUILayout.LabelField("Avatar Results:", BasisNetworkManagement.Instance.Transmitter.AvatarResults.IsCreated ? "Created" : "Not Created");
            }

            EditorGUILayout.Space();
            debugFoldout = EditorGUILayout.Foldout(debugFoldout, "Debugging", true);
            if (debugFoldout)
            {
                EditorGUILayout.LabelField("Microphone Range Index:", BasisNetworkManagement.Instance.Transmitter.MicrophoneRangeIndex != null ? BasisNetworkManagement.Instance.Transmitter.MicrophoneRangeIndex.Length.ToString() : "NULL");
                EditorGUILayout.LabelField("Hearing Index:", BasisNetworkManagement.Instance.Transmitter.HearingIndex != null ? BasisNetworkManagement.Instance.Transmitter.HearingIndex.Length.ToString() : "NULL");
                EditorGUILayout.LabelField("Avatar Index:", BasisNetworkManagement.Instance.Transmitter.AvatarIndex != null ? BasisNetworkManagement.Instance.Transmitter.AvatarIndex.Length.ToString() : "NULL");
            }
        }

        EditorGUILayout.Space();
        receiverArrayFoldout = EditorGUILayout.Foldout(receiverArrayFoldout, "Receiver Array Data", true);
        if (receiverArrayFoldout)
        {
            if (BasisNetworkManagement.ReceiversSnapshot != null && BasisNetworkManagement.ReceiversSnapshot.Length > 0)
            {
                EditorGUILayout.LabelField($"Receiver Array Length: {BasisNetworkManagement.ReceiversSnapshot.Length}");
                selectedReceiverIndex = EditorGUILayout.IntSlider("Select Receiver", selectedReceiverIndex, 0, BasisNetworkManagement.ReceiversSnapshot.Length - 1);

                var receiver = BasisNetworkManagement.ReceiversSnapshot[selectedReceiverIndex];
                if (receiver != null)
                {
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField($"Receiver {selectedReceiverIndex + 1}", EditorStyles.boldLabel);
                    DisplayReceiverProperties(receiver);
                    EditorGUILayout.EndVertical();
                }
                else
                {
                    EditorGUILayout.LabelField($"Receiver {selectedReceiverIndex + 1}: NULL");
                }
            }
            else
            {
                EditorGUILayout.LabelField("Receiver Array is empty or null.");
            }
        }

        serializedObject.ApplyModifiedProperties();

        if (GUI.changed)
        {
            EditorUtility.SetDirty(networkManager);
        }
    }

    private void DisplayReceiverProperties(object receiver)
    {
        // Placeholder: implement this to show receiver details properly
        EditorGUILayout.LabelField("Receiver Data: (custom display not implemented)");
    }
    private void DisplayReceiverProperties(BasisNetworkReceiver receiver)
    {
        // Use reflection to dynamically display fields
        var fields = receiver.GetType().GetFields();
        foreach (var field in fields)
        {
            var fieldValue = field.GetValue(receiver);
            EditorGUILayout.LabelField($"{field.Name}:", fieldValue != null ? fieldValue.ToString() : "null");
        }

        // Use reflection to dynamically display properties
        var properties = receiver.GetType().GetProperties();
        foreach (var property in properties)
        {
            if (property.CanRead)
            {
                var propertyValue = property.GetValue(receiver, null);
                EditorGUILayout.LabelField($"{property.Name}:", propertyValue != null ? propertyValue.ToString() : "null");
            }
        }
    }
}
