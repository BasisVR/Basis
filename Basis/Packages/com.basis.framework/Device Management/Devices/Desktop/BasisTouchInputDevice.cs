using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Desktop;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;

public class BasisTouchInputDevice : BasisInput
{
    public BasisDesktopEye Input;
    public Finger Finger;
    public int Index;
    public void Initalize(string uniqueID, string unUniqueDeviceID, string subSystems, bool ForceAssignTrackedRole, BasisBoneTrackedRole basisBoneTrackedRole, bool hasRayCastOverrideSupport = false)
    {
        InitalizeTracking( uniqueID,  unUniqueDeviceID,  subSystems,  ForceAssignTrackedRole,  basisBoneTrackedRole, hasRayCastOverrideSupport);
        BasisPointRaycaster.UseWorldPosition = false;
    }
    public override void LateDoPollData()
    {
        if (HasRaycaster)
        {
            if (Finger != null)
            {
                BasisPointRaycaster.ScreenPoint = Finger.screenPosition;

                if (Finger.isActive)
                {
                    CurrentInputState.Trigger = 1;
                }
                else
                {
                    CurrentInputState.Trigger = 0;
                }
            }
            else
            {
                CurrentInputState.Trigger = 0;
            }
            UpdateInputEvents(false,true);
        }
    }
    public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
    {

    }

    public override void PlaySoundEffect(string SoundEffectName, float Volume)
    {

    }

    public override void ShowTrackedVisual()
    {

    }
}
