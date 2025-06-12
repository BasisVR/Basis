using Basis.Scripts.BasisSdk;
using HVR.Basis.Comms;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(FeatureNetworking))]
public class FeatureNetworkingEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        FindUpdates();
    }

    private void FindUpdates()
    {
        var that = (FeatureNetworking)target;
        
        var avatar = that.GetComponentInParent<BasisAvatar>(true);
        if (avatar == null) return;

        var anyChanged = false;
        foreach (var candidate in avatar.GetComponentsInChildren<Component>(true))
        {
            if (candidate is ICommsNetworkable)
            {
                if (that.TryAddPairingIfNotExists(candidate))
                {
                    anyChanged = true;
                }
            }
        }

        if (anyChanged)
        {
            EditorUtility.SetDirty(that);
        }
    }
}