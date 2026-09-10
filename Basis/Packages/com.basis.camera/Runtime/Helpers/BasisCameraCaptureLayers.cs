using UnityEngine;
public static class BasisCameraCaptureLayers
{
    private static readonly string[] Managed = { "OverlayUI" };
    private static readonly string[] SubjectLayerNames = { "LocalPlayerAvatar", "RemotePlayerAvatar", "Player", "Interactable" };
    public static int Marker => LayerMask.NameToLayer("OverlayUI");
    public static bool IsUserTogglable(int layer)
    {
        if (layer < 0 || layer > 31) return false;
        string name = LayerMask.LayerToName(layer);
        if (string.IsNullOrEmpty(name)) return false;
        for (int Index = 0; Index < Managed.Length; Index++)
        {
            if (name == Managed[Index]) return false;
        }
        return true;
    }
    public static int SubjectMask(int worldMask)
    {
        int subject = 0;
        for (int Index = 0; Index < SubjectLayerNames.Length; Index++)
        {
            int layer = LayerMask.NameToLayer(SubjectLayerNames[Index]);
            if (layer >= 0) subject |= 1 << layer;
        }

        int result = worldMask & subject;
        return result == 0 ? subject : result;
    }
    public static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        Transform transform = root.transform;
        for (int Index = 0; Index < transform.childCount; Index++)
        {
            SetLayerRecursively(transform.GetChild(Index).gameObject, layer);
        }
    }
}
