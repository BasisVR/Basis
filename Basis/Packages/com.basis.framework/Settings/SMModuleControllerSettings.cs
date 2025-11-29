public class SMModuleControllerSettings : BasisSettingsBase
{
    public static float JoyStickDeadZone = 0.01f;
    public static float SnapTurnAngle = 45;
    public static bool HasInvertedMouse = false;
    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName)
        {
            case "joystickdeadzone":
                if (SliderReadOption(optionValue, out JoyStickDeadZone))
                {
                    BasisDebug.Log("JoyStick deadspace is set to " + JoyStickDeadZone);
                }
                break;
            case "snapturnangle":
                if (SliderReadOption(optionValue, out SnapTurnAngle))
                {
                    BasisDebug.Log("Snap Turn Angle is set to " + SnapTurnAngle);
                }
                break;
            case "invertmouse":
                if(optionValue == "true")
                {
                    HasInvertedMouse = true;
                }
                else
                {
                    if (optionValue == "false")
                    {
                        HasInvertedMouse = false;
                    }
                }
                break;
        }
    }
}
