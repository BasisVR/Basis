using Basis.Scripts.Device_Management.Devices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;

public abstract class BasisInputController : BasisInput
{
    [Header("Final Data normally just modified by EyeHeight/AvatarEyeHeight)")]
    public float3 HandFinalPosition;
    public quaternion HandFinalRotation;
}
