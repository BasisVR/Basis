using BattlePhaze.SettingsManager;
using UnityEngine;
public class SMModuleControllerSettings : SettingsManagerOption
{
    public static float JoyStickDeadZone = 0.01f;
    public static float SnapTurnAngle = 45;
    public override void ReceiveOption(SettingsMenuInput Option, SettingsManager Manager)
    {
        if (NameReturn(0, Option))
        {
            if (SliderReadOption(Option, Manager, out JoyStickDeadZone))
            {
                BasisDebug.Log("JoyStick deadspace is set to " + JoyStickDeadZone);
            }
        }
        if (NameReturn(1, Option))
        {
            if (SliderReadOption(Option, Manager, out SnapTurnAngle))
            {
                BasisDebug.Log("Snap Turn Angle is set to " + SnapTurnAngle);
            }
        }
    }
}