using System;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class BasisCameraButtonDescriptor
{
    public string id;
    public BasisCameraButtonAction action;
    public Button button;
    public Sprite icon;

    [NonSerialized] public Image statusIndicator;
}
