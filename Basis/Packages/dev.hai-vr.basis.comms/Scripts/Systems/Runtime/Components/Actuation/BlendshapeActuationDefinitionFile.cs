using System;
using HVR.Basis.Comms;
using UnityEngine;

[CreateAssetMenu(fileName = "BlendshapeActuationAsset", menuName = "HVR.Basis/Comms/Blendshape Actuation Definition FIle")]
public class BlendshapeActuationDefinitionFile : ScriptableObject
{
    [TextArea] public string comment;

    public BlendshapeActuationDefinition[] definitions = Array.Empty<BlendshapeActuationDefinition>();
    public AddressOverride[] addressOverrides = Array.Empty<AddressOverride>();

    public int internalVersion = 1;

    private void Reset()
    {
        internalVersion = 2;
    }
}
