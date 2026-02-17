using Basis.BasisUI;

public class SMModuleControllerSettings : BasisSettingsBase
{
    public static float JoyStickDeadZone = 0.01f;
    public static float SnapTurnAngle = 45;
    public static bool HasInvertedMouse = false;

    public static float baseXDeadzone = 0.08f;
    public static float extraXDeadzoneAtFullY = 0.35f;
    public static float yDeadzone = 0.10f;
    public static float wingExponent = 1.6f;

    public static float MouseSensitivty = 1;
    public static bool UsingSnapTurnAngle = false;

    // --- Canonical setting keys (from defaults) ---
    private static string K_JOYSTICK_DEADZONE => BasisSettingsDefaults.ControllerDeadZone.BindingKey;      // "joystickdeadzone"
    private static string K_SNAPTURN_ANGLE => BasisSettingsDefaults.SnapTurnAngle.BindingKey;             // "snapturnangle"
    private static string K_BASEX_DEADZONE => BasisSettingsDefaults.Basexdeadzone.BindingKey;             // "basexdeadzone"
    private static string K_EXTRAX_DEADZONE_AT_FULLY => BasisSettingsDefaults.Extraxdeadzoneatfully.BindingKey; // "extraxdeadzoneatfully"
    private static string K_Y_DEADZONE => BasisSettingsDefaults.Ydeadzone.BindingKey;                     // "ydeadzone"
    private static string K_WING_EXPONENT => BasisSettingsDefaults.Wingexponent.BindingKey;               // "wingexponent"
    private static string K_INVERT_MOUSE => BasisSettingsDefaults.InvertMouse.BindingKey;                // "invertmouse"
    private static string K_USE_SNAPTURN => BasisSettingsDefaults.usesnapturn.BindingKey;                // "usesnapturn"
    private static string K_MOUSE_SENSITIVITY => BasisSettingsDefaults.mousesensitivty.BindingKey;       // "mousesensitivty"

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName)
        {
            case var s when s == K_JOYSTICK_DEADZONE:
                if (SliderReadOption(optionValue, out JoyStickDeadZone))
                {
                    // BasisDebug.Log($"JoyStick deadspace is set to {JoyStickDeadZone}");
                }
                break;

            case var s when s == K_SNAPTURN_ANGLE:
                if (SliderReadOption(optionValue, out SnapTurnAngle))
                {
                    // BasisDebug.Log($"Snap Turn Angle is set to {SnapTurnAngle}");
                }
                break;

            case var s when s == K_BASEX_DEADZONE:
                if (SliderReadOption(optionValue, out baseXDeadzone))
                {
                    // BasisDebug.Log($"baseXDeadzone deadspace is set to {baseXDeadzone}");
                }
                break;

            case var s when s == K_EXTRAX_DEADZONE_AT_FULLY:
                if (SliderReadOption(optionValue, out extraXDeadzoneAtFullY))
                {
                    // BasisDebug.Log($"extraXDeadzoneAtFullY deadspace is set to {extraXDeadzoneAtFullY}");
                }
                break;

            case var s when s == K_Y_DEADZONE:
                if (SliderReadOption(optionValue, out yDeadzone))
                {
                    // BasisDebug.Log($"yDeadzone deadspace is set to {yDeadzone}");
                }
                break;

            case var s when s == K_WING_EXPONENT:
                if (SliderReadOption(optionValue, out wingExponent))
                {
                    // BasisDebug.Log($"wingExponent deadspace is set to {wingExponent}");
                }
                break;

            case var s when s == K_INVERT_MOUSE:
                if (optionValue == "true") HasInvertedMouse = true;
                else if (optionValue == "false") HasInvertedMouse = false;
                break;

            case var s when s == K_USE_SNAPTURN:
                if (optionValue == "true") UsingSnapTurnAngle = true;
                else if (optionValue == "false") UsingSnapTurnAngle = false;
                break;

            case var s when s == K_MOUSE_SENSITIVITY:
                if (SliderReadOption(optionValue, out MouseSensitivty))
                {
                }
                break;
        }
    }

    public override void ChangedSettings()
    {
    }
}
