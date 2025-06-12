using UnityEditor;
using UnityEngine;
using Basis.Scripts.BasisSdk.Players;
using System.Threading.Tasks;

public class BasisHeightEditor : Editor
{
    // Menu item for recalculating the player's eye height
    [MenuItem("Basis/Height/Recalculate Player Eye Height")]
    public static void RecalculatePlayerEyeHeight()
    {
        // Get the local player instance
        BasisLocalPlayer basisPlayer = BasisLocalPlayer.Instance;

        if (basisPlayer == null)
        {
            BasisDebug.LogError("No BasisLocalPlayer instance found.");
            return;
        }

        // Call the method from the BasisHeightDriver class
        BasisHeightDriver.SetPlayersEyeHeight(basisPlayer, BasisLocalHeightInformation.BasisSelectedHeightMode.EyeHeight);
        BasisDebug.Log("Player eye height recalculated successfully.");
    }

    // Menu item for capturing the player's height
    [MenuItem("Basis/Height/Capture Player Height")]
    public static void CapturePlayerHeight()
    {
        // Call the CapturePlayerHeight method from the BasisHeightDriver class
        BasisHeightDriver.CapturePlayerHeight();
        BasisDebug.Log("Player height captured successfully.");
    }

    // Menu item for loading or getting default player height
    [MenuItem("Basis/Height/Get Default or Load Player Height")]
    public static void GetDefaultOrLoadPlayerHeight()
    {
        float height = BasisHeightDriver.GetDefaultOrLoadPlayerHeight();
        BasisDebug.Log($"Loaded or default player height: {height}");
    }

    // Menu item for saving player height
    [MenuItem("Basis/Height/Save Player Height")]
    public static void SavePlayerHeight()
    {
        // Get the local player instance to retrieve the eye height
        BasisLocalPlayer basisPlayer = BasisLocalPlayer.Instance;

        if (basisPlayer == null)
        {
            BasisDebug.LogError("No BasisLocalPlayer instance found.");
            return;
        }

        // Save the current player height
        BasisHeightDriver.SaveHeight(basisPlayer.CurrentHeight.SelectedPlayerHeight);
        BasisDebug.Log($"Player height saved: {basisPlayer.CurrentHeight.SelectedPlayerHeight}");
    }
}
