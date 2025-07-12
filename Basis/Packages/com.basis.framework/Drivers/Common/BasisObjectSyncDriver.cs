using Unity.Mathematics;
using UnityEngine;
using System.Collections.Generic;
public static class BasisObjectSyncDriver
{
    public static List<BasisObjectSyncNetworking> OwnedObjectSyncs = new List<BasisObjectSyncNetworking>();
    public static List<BasisObjectSyncNetworking> RemoteOwnedObjectSyncs = new List<BasisObjectSyncNetworking>();
    public static float targetMiliseconds = 0.1f; // Target update frequency in Hz (10 times per second)
    private static double _lastUpdateTime; // Last update timestamp
    /// <summary>
    /// only syncs when we are the owner
    /// </summary>
    public static void TransmitOwnedPickups()
    {
        double timeAsDouble = Time.timeAsDouble;
        if (timeAsDouble - _lastUpdateTime >= targetMiliseconds)
        {
            _lastUpdateTime = timeAsDouble;
            int count = OwnedObjectSyncs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                BasisObjectSyncNetworking Object = OwnedObjectSyncs[Index];
                Object.SendNetworkSync();
            }
        }
        float Delta = Time.deltaTime;
        int remotecount = RemoteOwnedObjectSyncs.Count;
        for (int Index = 0; Index < remotecount; Index++)
        {
            BasisObjectSyncNetworking Object = RemoteOwnedObjectSyncs[Index];
            if (Object.HasRemoteIndex)
            {
                float lerp = Object.BTU.LerpMultipliers * Delta;

                if (lerp <= 0f)
                {
                    return;
                }
                if (Object.BTU.SyncScales)
                {
                    Object.transform.localScale = math.lerp(Object.transform.localScale, Object.BTU.TargetScales, lerp);
                }
                if (Object.BTU.SyncDestination)
                {
                    Object.transform.GetLocalPositionAndRotation(out Vector3 LocalPosition, out Quaternion LocalRotation);

                    float3 Position = math.lerp(LocalPosition, Object.BTU.TargetPositions, lerp);
                    quaternion rotation = math.slerp(LocalRotation, Object.BTU.TargetRotations, lerp);

                    Object.transform.SetLocalPositionAndRotation(Position, rotation);
                }
            }
        }
    }
    /// <summary>
    /// Adds a new object to the owned object sync list.
    /// </summary>
    public static void AddLocalOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null && !OwnedObjectSyncs.Contains(obj))
        {
            OwnedObjectSyncs.Add(obj);
        }
    }

    /// <summary>
    /// Removes an object from the owned object sync list.
    /// </summary>
    public static void RemoveLocalOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null)
        {
            OwnedObjectSyncs.Remove(obj);
        }
    }
    /// <summary>
    /// Adds a new object to the owned object sync list.
    /// </summary>
    public static void AddRemoteOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null && !OwnedObjectSyncs.Contains(obj))
        {
            RemoteOwnedObjectSyncs.Add(obj);
        }
    }

    /// <summary>
    /// Removes an object from the owned object sync list.
    /// </summary>
    public static void RemoveRemoteOwner(BasisObjectSyncNetworking obj)
    {
        if (obj != null)
        {
            RemoteOwnedObjectSyncs.Remove(obj);
        }
    }
    public struct BasisTranslationUpdate
    {
        public float3 TargetPositions;
        public quaternion TargetRotations;
        public float3 TargetScales;

        public float LerpMultipliers;

        public bool SyncDestination;
        public bool SyncScales;
    }
}
