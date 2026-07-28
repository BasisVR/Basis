using Basis.Scripts.Drivers;
using UnityEditor;
using UnityEngine;

namespace Basis.IK.Debugging
{
    /// <summary>
    /// Menu hooks for the runtime knee-swivel recorder. Enter Play, Start, then spend a few seconds in the pose
    /// where the bad knee actually misbehaves -- stand, shift weight, take a step, sit if that is where it shows
    /// -- and Stop + Dump. The console prints a LEFT vs RIGHT table and names the asymmetry; the CSV is there for
    /// the frame the number goes bad.
    ///
    /// Use it when one knee is wrong and the other is not: the solve path is mirror-symmetric, so that pattern
    /// means the two legs are being handed different data, and this says which field differs.
    /// </summary>
    public static class BasisLegSwivelDebugMenu
    {
        [MenuItem("Basis/Debug/IK/Leg Swivel Debug - Start (record)")]
        static void StartRec()
        {
            if (!Application.isPlaying) { Debug.LogWarning("[LegSwivelDebug] enter Play mode first."); return; }
            BasisLegSwivelDebug.Start();
            Debug.Log("[LegSwivelDebug] recording -- reproduce the bad knee, then 'Stop + Dump CSV'.");
        }

        [MenuItem("Basis/Debug/IK/Leg Swivel Debug - Stop + Dump CSV")]
        static void StopRec() => BasisLegSwivelDebug.StopAndDump();

        [MenuItem("Basis/Debug/IK/Leg Swivel Debug - Start (record)", true)]
        static bool V1() => !BasisLegSwivelDebug.Enabled;

        [MenuItem("Basis/Debug/IK/Leg Swivel Debug - Stop + Dump CSV", true)]
        static bool V2() => BasisLegSwivelDebug.Enabled;
    }
}
