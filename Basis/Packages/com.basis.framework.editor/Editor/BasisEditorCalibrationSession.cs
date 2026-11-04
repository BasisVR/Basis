using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.UI;
using UnityEditor;

/// <summary>
/// Editor version of the in-game calibrate flow (CalibrationProvider): start puts the avatar into a
/// T-pose and holds it there with the lock-in visualizer and trackers showing, so you can move the
/// simulated trackers and see which bone each one will bind to. Nothing is bound until Confirm.
/// Cancel drops the T-pose without calibrating.
/// </summary>
[InitializeOnLoad]
public static class BasisEditorCalibrationSession
{
    public static bool Active { get; private set; }

    public static bool CanStart => EditorApplication.isPlaying && BasisLocalPlayer.Instance != null && BasisLocalPlayer.Instance.LocalAvatarDriver != null;

    static BasisEditorCalibrationSession()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Active = false;
        };
        EditorApplication.update += Watch;
    }

    public static void Start()
    {
        if (!CanStart) return;
        BasisLocalPlayer player = BasisLocalPlayer.Instance;

        BasisUINeedsVisibleTrackers.Add(player);
        player.LocalAvatarDriver.PutAvatarIntoTPose();
        BasisCalibrationLockInVisualizer.Begin();
        Active = true;
    }

    public static void Confirm()
    {
        if (!Active) return;
        Active = false;

        BasisCalibrationLockInVisualizer.End();
        // FullBodyCalibration restores the animator itself once it has read the T-pose.
        BasisAvatarIKStageCalibration.FullBodyCalibration();
        if (BasisLocalPlayer.Instance != null) BasisUINeedsVisibleTrackers.Remove(BasisLocalPlayer.Instance);
        BasisCalibrationQualityReport.Capture();
    }

    public static void Cancel()
    {
        if (!Active) return;
        Active = false;

        BasisCalibrationLockInVisualizer.End();
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (player == null) return;

        if (BasisLocalAvatarDriver.CurrentlyTposing)
        {
            player.LocalAvatarDriver.ResetAvatarAnimator();
            player.LocalRigDriver.RigLayerActive = true;
        }
        BasisUINeedsVisibleTrackers.Remove(player);
    }

    // Something else ended the T-pose (in-game calibrate, avatar reload, player gone): drop the session
    // so the editor buttons don't show a hold that isn't there.
    private static void Watch()
    {
        if (!Active) return;
        if (!EditorApplication.isPlaying || BasisLocalPlayer.Instance == null || !BasisLocalAvatarDriver.CurrentlyTposing)
        {
            Active = false;
            BasisCalibrationLockInVisualizer.End();
            if (BasisLocalPlayer.Instance != null) BasisUINeedsVisibleTrackers.Remove(BasisLocalPlayer.Instance);
        }
    }
}
