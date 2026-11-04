using UnityEditor;
using UnityEngine.UIElements;

#pragma warning disable 618
[CustomEditor(typeof(BasisObjectSyncNetworking))]
#pragma warning restore 618
[CanEditMultipleObjects]
public class BasisObjectSyncNetworkingInspector : BasisPickupSyncNetworkingInspector
{
    public override VisualElement CreateInspectorGUI()
    {
        VisualElement root = base.CreateInspectorGUI();
        VisualElement banner = BasisDeprecatedComponentUpgrader.Banner(targets);
        if (banner != null) root.Insert(0, banner);
        return root;
    }
}
