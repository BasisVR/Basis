using UnityEngine;

public partial class BundledContentHolder : MonoBehaviour
{
    public ContentPoliceSelector AvatarScriptSelector;
    public ContentPoliceSelector SystemScriptSelector;
    public ContentPoliceSelector PropScriptSelector;

    public BasisLoadableBundle DefaultScene;
    public BasisLoadableBundle DefaultAvatar;
    public static BundledContentHolder Instance;
    public bool UseAddressablesToLoadScene = false;
    public bool UseSceneProvidedHere = false;
    public bool GetSelector(Selector Selector, out ContentPoliceSelector PoliceCheck)
    {
        switch (Selector)
        {
            case Selector.Avatar:
                PoliceCheck = AvatarScriptSelector;
                return true;
            case Selector.System:
                PoliceCheck = SystemScriptSelector;
                return true;
            case Selector.Prop:
                PoliceCheck = PropScriptSelector;
                return true;
            default:
                PoliceCheck = null;
                BasisDebug.LogError("Missing Selector");
                return false;
        }
    }
    public void Awake()
    {
        Instance = this;
    }
}
