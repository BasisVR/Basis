using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

public class SMModuleCalibration : BasisSettingsBase
{
    public static BasisSelectedHeightMode HeightMode = BasisSelectedHeightMode.EyeHeight;
    public static bool ApplyCustomScale = false;
    public static float SelectedScale = 1.6f;
    public static float SelectedEyeHeight = 1.61f;

    // Cache last applied state so we only apply when it actually changes.
    private static bool _hasApplied;
    private static BasisSelectedHeightMode _lastHeightMode;
    private static float _lastSelectedScale;
    private static bool _lastApplyCustomScale;

    private static bool _dirty;

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        switch (matchedSettingName.ToLower())
        {
            case "ik mode":
                {
                    var old = HeightMode;

                    // Note: your optionValue strings must match exactly; consider trimming.
                    switch (optionValue)
                    {
                        case "Eye Height":
                            HeightMode = BasisSelectedHeightMode.EyeHeight;
                            break;
                        case "Arm Distance":
                            if (BasisDeviceManagement.IsUserInDesktop())
                            {
                                HeightMode = BasisSelectedHeightMode.EyeHeight;
                            }
                            else
                            {
                                HeightMode = BasisSelectedHeightMode.ArmSpan;
                            }
                            break;
                    }

                    if (HeightMode != old) _dirty = true;
                    break;
                }

            case "selectedheight":
                {
                    break;
                }

            case "custom scale":
                {
                    var old = ApplyCustomScale;
                    if (bool.TryParse(optionValue, out var parsed) && parsed != old)
                    {
                        ApplyCustomScale = parsed;
                        _dirty = true;
                    }
                    break;
                }

            case "selected scale":
                {
                    var old = SelectedScale;
                    if (SliderReadOption(optionValue, out var parsed))
                    {
                        if (!Mathf.Approximately(old, parsed))
                        {
                            SelectedScale = parsed;
                            _dirty = true;
                        }
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Selected Scale", BasisDebug.LogTag.Device);
                    }
                    break;
                }
            case "real world eye height":
                {
                    var old = SelectedEyeHeight;
                    if (SliderReadOption(optionValue, out var CurrentSelectedEyeHeight))
                    {
                        if (!Mathf.Approximately(old, CurrentSelectedEyeHeight))
                        {
                            SelectedEyeHeight = CurrentSelectedEyeHeight;
                            _dirty = true;
                        }
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Selected Scale", BasisDebug.LogTag.Device);
                    }
                }
                break;
            // ---------- GLOBAL ONE EURO PARAMS ----------
            case "fbikmincutoff":
                {
                    if (SliderReadOption(optionValue, out var f))
                    {
                        BasisLocalRigDriver.MinCutoff = f;
                    }

                    break;
                }
            case "fbikbeta":
                {
                    if (SliderReadOption(optionValue, out var f))
                        BasisLocalRigDriver.Beta = f;
                    break;
                }
            case "fbikderivativecutoff":
                {
                    if (SliderReadOption(optionValue, out var f))
                        BasisLocalRigDriver.DerivativeCutoff = f;
                    break;
                }
            case "fbikpositionsmoothinghz":
                {
                    if (SliderReadOption(optionValue, out var f))
                        BasisLocalRigDriver.PositionSmoothingHz = f;
                    break;
                }
            case "fbikrotationsmoothinghz":
                {
                    if (SliderReadOption(optionValue, out var f))
                        BasisLocalRigDriver.RotationSmoothingHz = f;
                    break;
                }

            // ---------- HIPS ----------
            case "fbikhipssmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_Hips] = parsed;
                    break;
                }
            case "fbikhipssmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_Hips] = parsed;
                    break;
                }
            case "fbikhipseuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_Hips] = parsed;
                    break;
                }
            case "fbikhipseurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_Hips] = parsed;
                    break;
                }

            // ---------- HEAD ----------
            case "fbikheadsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_Head] = parsed;
                    break;
                }
            case "fbikheadsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_Head] = parsed;
                    break;
                }
            case "fbikheadeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_Head] = parsed;
                    break;
                }
            case "fbikheadeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_Head] = parsed;
                    break;
                }

            // ---------- LEFT FOOT ----------
            case "fbikleftfootsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftFoot] = parsed;
                    break;
                }
            case "fbikleftfootsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftFoot] = parsed;
                    break;
                }
            case "fbikleftfooteuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftFoot] = parsed;
                    break;
                }
            case "fbikleftfooteurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftFoot] = parsed;
                    break;
                }

            // ---------- RIGHT FOOT ----------
            case "fbikrightfootsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightFoot] = parsed;
                    break;
                }
            case "fbikrightfootsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightFoot] = parsed;
                    break;
                }
            case "fbikrightfooteuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightFoot] = parsed;
                    break;
                }
            case "fbikrightfooteurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightFoot] = parsed;
                    break;
                }

            // ---------- CHEST ----------
            case "fbikchestsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_Chest] = parsed;
                    break;
                }
            case "fbikchestsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_Chest] = parsed;
                    break;
                }
            case "fbikchesteuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_Chest] = parsed;
                    break;
                }
            case "fbikchesteurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_Chest] = parsed;
                    break;
                }

            // ---------- LEFT LOWER LEG ----------
            case "fbikleftlowerlegsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftLowerLeg] = parsed;
                    break;
                }
            case "fbikleftlowerlegsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftLowerLeg] = parsed;
                    break;
                }
            case "fbikleftlowerlegeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftLowerLeg] = parsed;
                    break;
                }
            case "fbikleftlowerlegeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftLowerLeg] = parsed;
                    break;
                }

            // ---------- RIGHT LOWER LEG ----------
            case "fbikrightlowerlegsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightLowerLeg] = parsed;
                    break;
                }
            case "fbikrightlowerlegsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightLowerLeg] = parsed;
                    break;
                }
            case "fbikrightlowerlegeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightLowerLeg] = parsed;
                    break;
                }
            case "fbikrightlowerlegeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightLowerLeg] = parsed;
                    break;
                }

            // ---------- LEFT HAND ----------
            case "fbiklefthandsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftHand] = parsed;
                    break;
                }
            case "fbiklefthandsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftHand] = parsed;
                    break;
                }
            case "fbiklefthandeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftHand] = parsed;
                    break;
                }
            case "fbiklefthandeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftHand] = parsed;
                    break;
                }

            // ---------- RIGHT HAND ----------
            case "fbikrighthandsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightHand] = parsed;
                    break;
                }
            case "fbikrighthandsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightHand] = parsed;
                    break;
                }
            case "fbikrighthandeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightHand] = parsed;
                    break;
                }
            case "fbikrighthandeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightHand] = parsed;
                    break;
                }

            // ---------- LEFT LOWER ARM ----------
            case "fbikleftlowerarmsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftLowerArm] = parsed;
                    break;
                }
            case "fbikleftlowerarmsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftLowerArm] = parsed;
                    break;
                }
            case "fbikleftlowerarmeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftLowerArm] = parsed;
                    break;
                }
            case "fbikleftlowerarmeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftLowerArm] = parsed;
                    break;
                }

            // ---------- RIGHT LOWER ARM ----------
            case "fbikrightlowerarmsmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightLowerArm] = parsed;
                    break;
                }
            case "fbikrightlowerarmsmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightLowerArm] = parsed;
                    break;
                }
            case "fbikrightlowerarmeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightLowerArm] = parsed;
                    break;
                }
            case "fbikrightlowerarmeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightLowerArm] = parsed;
                    break;
                }

            // ---------- LEFT TOE ----------
            case "fbiklefttoesmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftToe] = parsed;
                    break;
                }
            case "fbiklefttoesmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftToe] = parsed;
                    break;
                }
            case "fbiklefttoeeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftToe] = parsed;
                    break;
                }
            case "fbiklefttoeeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftToe] = parsed;
                    break;
                }

            // ---------- RIGHT TOE ----------
            case "fbikrighttoesmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightToe] = parsed;
                    break;
                }
            case "fbikrighttoesmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightToe] = parsed;
                    break;
                }
            case "fbikrighttoeeuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightToe] = parsed;
                    break;
                }
            case "fbikrighttoeeurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightToe] = parsed;
                    break;
                }

            // ---------- LEFT SHOULDER ----------
            case "fbikleftshouldersmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftShoulder] = parsed;
                    break;
                }
            case "fbikleftshouldersmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftShoulder] = parsed;
                    break;
                }
            case "fbikleftshouldereuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftShoulder] = parsed;
                    break;
                }
            case "fbikleftshouldereurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftShoulder] = parsed;
                    break;
                }

            // ---------- RIGHT SHOULDER ----------
            case "fbikrightshouldersmoothpos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightShoulder] = parsed;
                    break;
                }
            case "fbikrightshouldersmoothrot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightShoulder] = parsed;
                    break;
                }
            case "fbikrightshouldereuropos":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightShoulder] = parsed;
                    break;
                }
            case "fbikrightshouldereurorot":
                {
                    if (bool.TryParse(optionValue, out var parsed))
                        BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightShoulder] = parsed;
                    break;
                }
            default:
           //     BasisDebug.LogError($"UnImplemented Settings Name! {matchedSettingName}", BasisDebug.LogTag.Device);
                break;
        }
    }

    public override void ChangedSettings()
    {
        // If UI calls ChangedSettings a lot, this prevents reapplying.
        if (!_dirty && _hasApplied)
            return;

        // Compare against last applied values, so even if _dirty gets missed,
        // we still won't spam applies.
        bool sameAsLast =
            _hasApplied &&
            _lastHeightMode == HeightMode &&
            Mathf.Approximately(_lastSelectedScale, SelectedScale) &&
            _lastApplyCustomScale == ApplyCustomScale;

        if (sameAsLast)
        {
            _dirty = false;
            return;
        }
        if (ApplyCustomScale || ApplyCustomScale == false && _lastApplyCustomScale  == true)
        {
            BasisHeightDriver.ApplyScaleAndHeight();
        }
        _hasApplied = true;
        _lastHeightMode = HeightMode;
        _lastSelectedScale = SelectedScale;
        _lastApplyCustomScale = ApplyCustomScale;

        _dirty = false;

        BasisDebug.Log(
            $"Applied height settings. HeightMode {HeightMode} " +
            $"SelectedScale {SelectedScale}, ApplyCustomScale {ApplyCustomScale}"
        );
    }
}
