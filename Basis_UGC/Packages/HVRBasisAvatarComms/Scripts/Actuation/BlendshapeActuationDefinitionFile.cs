using System;
using HVR.Basis.Comms;
using UnityEngine;

[CreateAssetMenu(fileName = "BlendshapeActuationAsset", menuName = "HVR.Basis/Comms/Blendshape Actuation Definition FIle")]
public class BlendshapeActuationDefinitionFile : ScriptableObject
{
    [TextArea] public string comment; 
    public BlendshapeActuationDefinition[] definitions = Array.Empty<BlendshapeActuationDefinition>();
}