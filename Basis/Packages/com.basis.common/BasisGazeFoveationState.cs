using Unity.Scripting.LifecycleManagement;
using UnityEngine;

/// <summary>
/// The gaze foveation values for the frame being recorded. The VRS build pass writes them at the start of
/// the local player's camera and the reset pass clears them at its end, so passes that bind their own
/// parameters (the ray traced GI dispatch) can foveate without reading shader globals. Focal w is the gaze
/// weight; 0 means full quality everywhere.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisGazeFoveationState
{
    public static Vector4 Focal;
    public static Vector4 Bands;
    public static Vector4 Effects;

    public static void Clear()
    {
        Focal = Vector4.zero;
    }
}
