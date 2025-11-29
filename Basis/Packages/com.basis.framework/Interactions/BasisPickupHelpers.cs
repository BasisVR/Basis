using Basis.Scripts.BasisSdk.Interactions;
using Unity.Mathematics;
using UnityEngine;
public class BasisPickupHelpers
{
    public static float GetDistanceBetweenHands(BasisInputSources Inputs)
    {
        float3 Left = Inputs.leftHand.Source.UnscaledDeviceCoord.position;
        float3 Right = Inputs.rightHand.Source.UnscaledDeviceCoord.position;
        float Distance = math.distance(Left, Right);
        return Distance;
    }

    public static float GetNormalizedDistanceBetweenHands(BasisInputSources BasisInputSources,float min = 0.02f, float max = 0.5f)
    {
        var distance = GetDistanceBetweenHands(BasisInputSources);
        return Mathf.InverseLerp(min, max, distance);
    }
}
