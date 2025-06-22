using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
[CustomEditor(typeof(BasisJiggleBonesComponent))]
public class BasisJiggleBonesComponentInspector : Editor
{
    public VisualElement rootElement;
    public BasisJiggleBonesComponent BonesComponent;
    public AvatarSDKJiggleBonesView AvatarSDKJiggleBonesView = new AvatarSDKJiggleBonesView();
    public override VisualElement CreateInspectorGUI()
    {
        rootElement = new VisualElement();
        BonesComponent = (BasisJiggleBonesComponent)target;
        AvatarSDKJiggleBonesView.Initialize(this);
        return rootElement;
    }
}
