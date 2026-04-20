using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

public class SMModuleCalibration : BasisSettingsBase
{
    public static BasisSelectedHeightMode HeightMode = BasisSelectedHeightMode.EyeHeight;
    public static BasisIKLockMode CurrentIKLockMode = BasisIKLockMode.LockHips;
    public static bool ApplyCustomScale = false;
    public static float SelectedScale = 1.6f;
    public static float SelectedEyeHeight = 1.61f;
    public static bool PitchCalibrationEnabled = false;

    /// <summary>
    /// Per-sphere calibration scale multipliers. Default is 1.0 for each role.
    /// </summary>
    public static readonly Dictionary<BasisBoneTrackedRole, float> SphereScaleMultipliers = new Dictionary<BasisBoneTrackedRole, float>();

    /// <summary>
    /// Returns the user-configured sphere scale multiplier for a given role (defaults to 1.0).
    /// </summary>
    public static float GetSphereScale(BasisBoneTrackedRole role)
    {
        if (SphereScaleMultipliers.TryGetValue(role, out float scale))
            return scale;
        return 1f;
    }

    // Cache last applied state so we only apply when it actually changes.
    private static bool _hasApplied;
    private static BasisSelectedHeightMode _lastHeightMode;
    private static float _lastSelectedScale;
    private static bool _lastApplyCustomScale;

    private static bool _dirty;

    // --- Canonical setting keys (from defaults) ---
    private static string K_IK_MODE => BasisSettingsDefaults.IKMode.BindingKey;                    // "ikmode"
    private static string K_IK_LOCK_MODE => BasisSettingsDefaults.IKLockMode.BindingKey;          // "iklockmode"
    private static string K_SELECTED_HEIGHT => BasisSettingsDefaults.SelectedHeight.BindingKey;    // "selectedheight"
    private static string K_CUSTOM_SCALE => BasisSettingsDefaults.CustomScale.BindingKey;         // "custom scale"
    private static string K_SELECTED_SCALE => BasisSettingsDefaults.SelectedScale.BindingKey;     // "selected scale"
    private static string K_REALWORLD_EYE_HEIGHT => BasisSettingsDefaults.realworldeyeheight.BindingKey; // "real world eye height"
    private static string K_PITCH_CALIBRATION => BasisSettingsDefaults.PitchCalibration.BindingKey;     // "pitchcalibration"

    // One Euro globals
    private static string K_FBIK_MINCUTOFF => BasisSettingsDefaults.FBIKMinCutoff.BindingKey;                 // "fbikmincutoff"
    private static string K_FBIK_BETA => BasisSettingsDefaults.FBIKBeta.BindingKey;                           // "fbikbeta"
    private static string K_FBIK_DERIV_CUTOFF => BasisSettingsDefaults.FBIKDerivativeCutoff.BindingKey;       // "fbikderivativecutoff"
    private static string K_FBIK_POS_SMOOTH_HZ => BasisSettingsDefaults.FBIKPositionSmoothingHz.BindingKey;   // "fbikpositionsmoothinghz"
    private static string K_FBIK_ROT_SMOOTH_HZ => BasisSettingsDefaults.FBIKRotationSmoothingHz.BindingKey;   // "fbikrotationsmoothinghz"
    private static string K_FBIK_SMOOTHING_STRENGTH => BasisSettingsDefaults.FBIKSmoothingStrength.BindingKey; // "fbiksmoothingstrength"

    // Hips
    private static string K_HIPS_SMOOTH_POS => BasisSettingsDefaults.FBIKHipsSmoothPos.BindingKey; // "fbikhipssmoothpos"
    private static string K_HIPS_SMOOTH_ROT => BasisSettingsDefaults.FBIKHipsSmoothRot.BindingKey; // "fbikhipssmoothrot"
    private static string K_HIPS_EURO_POS => BasisSettingsDefaults.FBIKHipsEuroPos.BindingKey;     // "fbikhipseuropos"
    private static string K_HIPS_EURO_ROT => BasisSettingsDefaults.FBIKHipsEuroRot.BindingKey;     // "fbikhipseurorot"

    // Head
    private static string K_HEAD_SMOOTH_POS => BasisSettingsDefaults.FBIKHeadSmoothPos.BindingKey;
    private static string K_HEAD_SMOOTH_ROT => BasisSettingsDefaults.FBIKHeadSmoothRot.BindingKey;
    private static string K_HEAD_EURO_POS => BasisSettingsDefaults.FBIKHeadEuroPos.BindingKey;
    private static string K_HEAD_EURO_ROT => BasisSettingsDefaults.FBIKHeadEuroRot.BindingKey;

    // Left Foot
    private static string K_LF_SMOOTH_POS => BasisSettingsDefaults.FBIKLeftFootSmoothPos.BindingKey;
    private static string K_LF_SMOOTH_ROT => BasisSettingsDefaults.FBIKLeftFootSmoothRot.BindingKey;
    private static string K_LF_EURO_POS => BasisSettingsDefaults.FBIKLeftFootEuroPos.BindingKey;
    private static string K_LF_EURO_ROT => BasisSettingsDefaults.FBIKLeftFootEuroRot.BindingKey;

    // Right Foot
    private static string K_RF_SMOOTH_POS => BasisSettingsDefaults.FBIKRightFootSmoothPos.BindingKey;
    private static string K_RF_SMOOTH_ROT => BasisSettingsDefaults.FBIKRightFootSmoothRot.BindingKey;
    private static string K_RF_EURO_POS => BasisSettingsDefaults.FBIKRightFootEuroPos.BindingKey;
    private static string K_RF_EURO_ROT => BasisSettingsDefaults.FBIKRightFootEuroRot.BindingKey;

    // Chest
    private static string K_CHEST_SMOOTH_POS => BasisSettingsDefaults.FBIKChestSmoothPos.BindingKey;
    private static string K_CHEST_SMOOTH_ROT => BasisSettingsDefaults.FBIKChestSmoothRot.BindingKey;
    private static string K_CHEST_EURO_POS => BasisSettingsDefaults.FBIKChestEuroPos.BindingKey;
    private static string K_CHEST_EURO_ROT => BasisSettingsDefaults.FBIKChestEuroRot.BindingKey;

    // Left Lower Leg
    private static string K_LLL_SMOOTH_POS => BasisSettingsDefaults.FBIKLeftLowerLegSmoothPos.BindingKey;
    private static string K_LLL_SMOOTH_ROT => BasisSettingsDefaults.FBIKLeftLowerLegSmoothRot.BindingKey;
    private static string K_LLL_EURO_POS => BasisSettingsDefaults.FBIKLeftLowerLegEuroPos.BindingKey;
    private static string K_LLL_EURO_ROT => BasisSettingsDefaults.FBIKLeftLowerLegEuroRot.BindingKey;

    // Right Lower Leg
    private static string K_RLL_SMOOTH_POS => BasisSettingsDefaults.FBIKRightLowerLegSmoothPos.BindingKey;
    private static string K_RLL_SMOOTH_ROT => BasisSettingsDefaults.FBIKRightLowerLegSmoothRot.BindingKey;
    private static string K_RLL_EURO_POS => BasisSettingsDefaults.FBIKRightLowerLegEuroPos.BindingKey;
    private static string K_RLL_EURO_ROT => BasisSettingsDefaults.FBIKRightLowerLegEuroRot.BindingKey;

    // Left Hand
    private static string K_LH_SMOOTH_POS => BasisSettingsDefaults.FBIKLeftHandSmoothPos.BindingKey;
    private static string K_LH_SMOOTH_ROT => BasisSettingsDefaults.FBIKLeftHandSmoothRot.BindingKey;
    private static string K_LH_EURO_POS => BasisSettingsDefaults.FBIKLeftHandEuroPos.BindingKey;
    private static string K_LH_EURO_ROT => BasisSettingsDefaults.FBIKLeftHandEuroRot.BindingKey;

    // Right Hand
    private static string K_RH_SMOOTH_POS => BasisSettingsDefaults.FBIKRightHandSmoothPos.BindingKey;
    private static string K_RH_SMOOTH_ROT => BasisSettingsDefaults.FBIKRightHandSmoothRot.BindingKey;
    private static string K_RH_EURO_POS => BasisSettingsDefaults.FBIKRightHandEuroPos.BindingKey;
    private static string K_RH_EURO_ROT => BasisSettingsDefaults.FBIKRightHandEuroRot.BindingKey;

    // Left Lower Arm
    private static string K_LLA_SMOOTH_POS => BasisSettingsDefaults.FBIKLeftLowerArmSmoothPos.BindingKey;
    private static string K_LLA_SMOOTH_ROT => BasisSettingsDefaults.FBIKLeftLowerArmSmoothRot.BindingKey;
    private static string K_LLA_EURO_POS => BasisSettingsDefaults.FBIKLeftLowerArmEuroPos.BindingKey;
    private static string K_LLA_EURO_ROT => BasisSettingsDefaults.FBIKLeftLowerArmEuroRot.BindingKey;

    // Right Lower Arm
    private static string K_RLA_SMOOTH_POS => BasisSettingsDefaults.FBIKRightLowerArmSmoothPos.BindingKey;
    private static string K_RLA_SMOOTH_ROT => BasisSettingsDefaults.FBIKRightLowerArmSmoothRot.BindingKey;
    private static string K_RLA_EURO_POS => BasisSettingsDefaults.FBIKRightLowerArmEuroPos.BindingKey;
    private static string K_RLA_EURO_ROT => BasisSettingsDefaults.FBIKRightLowerArmEuroRot.BindingKey;

    // Left Toe
    private static string K_LT_SMOOTH_POS => BasisSettingsDefaults.FBIKLeftToeSmoothPos.BindingKey;
    private static string K_LT_SMOOTH_ROT => BasisSettingsDefaults.FBIKLeftToeSmoothRot.BindingKey;
    private static string K_LT_EURO_POS => BasisSettingsDefaults.FBIKLeftToeEuroPos.BindingKey;
    private static string K_LT_EURO_ROT => BasisSettingsDefaults.FBIKLeftToeEuroRot.BindingKey;

    // Right Toe
    private static string K_RT_SMOOTH_POS => BasisSettingsDefaults.FBIKRightToeSmoothPos.BindingKey;
    private static string K_RT_SMOOTH_ROT => BasisSettingsDefaults.FBIKRightToeSmoothRot.BindingKey;
    private static string K_RT_EURO_POS => BasisSettingsDefaults.FBIKRightToeEuroPos.BindingKey;
    private static string K_RT_EURO_ROT => BasisSettingsDefaults.FBIKRightToeEuroRot.BindingKey;

    // Left Shoulder
    private static string K_LS_SMOOTH_POS => BasisSettingsDefaults.FBIKLeftShoulderSmoothPos.BindingKey;
    private static string K_LS_SMOOTH_ROT => BasisSettingsDefaults.FBIKLeftShoulderSmoothRot.BindingKey;
    private static string K_LS_EURO_POS => BasisSettingsDefaults.FBIKLeftShoulderEuroPos.BindingKey;
    private static string K_LS_EURO_ROT => BasisSettingsDefaults.FBIKLeftShoulderEuroRot.BindingKey;

    // Right Shoulder
    private static string K_RS_SMOOTH_POS => BasisSettingsDefaults.FBIKRightShoulderSmoothPos.BindingKey;
    private static string K_RS_SMOOTH_ROT => BasisSettingsDefaults.FBIKRightShoulderSmoothRot.BindingKey;
    private static string K_RS_EURO_POS => BasisSettingsDefaults.FBIKRightShoulderEuroPos.BindingKey;
    private static string K_RS_EURO_ROT => BasisSettingsDefaults.FBIKRightShoulderEuroRot.BindingKey;

    // IK Collider & Tuning keys
    private static string K_FBIK_COLLISIONS_ENABLED => BasisSettingsDefaults.FBIKCollisionsEnabled.BindingKey;
    private static string K_FBIK_PROTECT_ELBOW => BasisSettingsDefaults.FBIKProtectElbow.BindingKey;
    private static string K_FBIK_USE_HAND_CAPSULE => BasisSettingsDefaults.FBIKUseHandCapsule.BindingKey;
    private static string K_FBIK_CHEST_RADIUS => BasisSettingsDefaults.FBIKChestRadius.BindingKey;
    private static string K_FBIK_COLLISION_SKIN => BasisSettingsDefaults.FBIKCollisionSkin.BindingKey;
    private static string K_FBIK_HAND_RADIUS => BasisSettingsDefaults.FBIKHandRadius.BindingKey;
    private static string K_FBIK_HAND_SKIN => BasisSettingsDefaults.FBIKHandSkin.BindingKey;
    private static string K_FBIK_SHOULDER_SOLVE => BasisSettingsDefaults.FBIKShoulderSolveEnabled.BindingKey;
    private static string K_FBIK_SHOULDER_ELEVATION => BasisSettingsDefaults.FBIKShoulderElevation.BindingKey;
    private static string K_FBIK_SHOULDER_PROTRACTION => BasisSettingsDefaults.FBIKShoulderProtraction.BindingKey;
    private static string K_FBIK_MAX_BEND_DEG => BasisSettingsDefaults.FBIKMaxBendDeg.BindingKey;
    private static string K_FBIK_STRUGGLE_START => BasisSettingsDefaults.FBIKStruggleStart.BindingKey;
    private static string K_FBIK_STRUGGLE_END => BasisSettingsDefaults.FBIKStruggleEnd.BindingKey;
    private static string K_FBIK_MAX_CHEST_DELTA => BasisSettingsDefaults.FBIKMaxChestDelta.BindingKey;
    private static string K_FBIK_MAX_HIP_DELTA => BasisSettingsDefaults.FBIKMaxHipDelta.BindingKey;

    // Calibration sphere scale keys
    private static string K_CALIB_HIPS => BasisSettingsDefaults.CalibSphereScaleHips.BindingKey;
    private static string K_CALIB_CHEST => BasisSettingsDefaults.CalibSphereScaleChest.BindingKey;
    private static string K_CALIB_LF => BasisSettingsDefaults.CalibSphereScaleLeftFoot.BindingKey;
    private static string K_CALIB_RF => BasisSettingsDefaults.CalibSphereScaleRightFoot.BindingKey;
    private static string K_CALIB_LLL => BasisSettingsDefaults.CalibSphereScaleLeftLowerLeg.BindingKey;
    private static string K_CALIB_RLL => BasisSettingsDefaults.CalibSphereScaleRightLowerLeg.BindingKey;
    private static string K_CALIB_LLA => BasisSettingsDefaults.CalibSphereScaleLeftLowerArm.BindingKey;
    private static string K_CALIB_RLA => BasisSettingsDefaults.CalibSphereScaleRightLowerArm.BindingKey;
    private static string K_CALIB_LH => BasisSettingsDefaults.CalibSphereScaleLeftHand.BindingKey;
    private static string K_CALIB_RH => BasisSettingsDefaults.CalibSphereScaleRightHand.BindingKey;
    private static string K_CALIB_LT => BasisSettingsDefaults.CalibSphereScaleLeftToes.BindingKey;
    private static string K_CALIB_RT => BasisSettingsDefaults.CalibSphereScaleRightToes.BindingKey;
    private static string K_CALIB_LS => BasisSettingsDefaults.CalibSphereScaleLeftShoulder.BindingKey;
    private static string K_CALIB_RS => BasisSettingsDefaults.CalibSphereScaleRightShoulder.BindingKey;

    private static readonly Dictionary<string, BasisBoneTrackedRole> _calibKeyToRole = new Dictionary<string, BasisBoneTrackedRole>();

    private static void EnsureCalibKeyMap()
    {
        if (_calibKeyToRole.Count > 0) return;
        _calibKeyToRole[K_CALIB_HIPS] = BasisBoneTrackedRole.Hips;
        _calibKeyToRole[K_CALIB_CHEST] = BasisBoneTrackedRole.Chest;
        _calibKeyToRole[K_CALIB_LF] = BasisBoneTrackedRole.LeftFoot;
        _calibKeyToRole[K_CALIB_RF] = BasisBoneTrackedRole.RightFoot;
        _calibKeyToRole[K_CALIB_LLL] = BasisBoneTrackedRole.LeftLowerLeg;
        _calibKeyToRole[K_CALIB_RLL] = BasisBoneTrackedRole.RightLowerLeg;
        _calibKeyToRole[K_CALIB_LLA] = BasisBoneTrackedRole.LeftLowerArm;
        _calibKeyToRole[K_CALIB_RLA] = BasisBoneTrackedRole.RightLowerArm;
        _calibKeyToRole[K_CALIB_LH] = BasisBoneTrackedRole.LeftHand;
        _calibKeyToRole[K_CALIB_RH] = BasisBoneTrackedRole.RightHand;
        _calibKeyToRole[K_CALIB_LT] = BasisBoneTrackedRole.LeftToes;
        _calibKeyToRole[K_CALIB_RT] = BasisBoneTrackedRole.RightToes;
        _calibKeyToRole[K_CALIB_LS] = BasisBoneTrackedRole.LeftShoulder;
        _calibKeyToRole[K_CALIB_RS] = BasisBoneTrackedRole.RightShoulder;
    }

    public override void ValidSettingsChange(string matchedSettingName, string optionValue)
    {
        string key = matchedSettingName;

        switch (key)
        {
            case var s when s == K_IK_MODE:
                {
                    var old = HeightMode;

                    switch (optionValue)
                    {
                        case "eye height":
                            BasisDebug.Log($"Height Mode Set To {optionValue}");
                            HeightMode = BasisSelectedHeightMode.EyeHeight;
                            break;

                        case "arm distance":
                            BasisDebug.Log($"Height Mode Set To {optionValue}");
                            HeightMode = BasisDeviceManagement.IsUserInDesktop() ? BasisSelectedHeightMode.EyeHeight : BasisSelectedHeightMode.ArmSpan;
                            break;
                    }

                    if (HeightMode != old) _dirty = true;
                    break;
                }

            case var s when s == K_IK_LOCK_MODE:
                {
                    switch (optionValue)
                    {
                        case "lock hips":
                            CurrentIKLockMode = BasisIKLockMode.LockHips;
                            break;
                        case "lock head":
                            CurrentIKLockMode = BasisIKLockMode.LockHead;
                            break;
                        case "lock both":
                            CurrentIKLockMode = BasisIKLockMode.LockBoth;
                            break;
                    }
                    ApplyIKLockMode();
                    BasisDebug.Log($"IK Lock Mode Set To {CurrentIKLockMode}");
                    break;
                }

            case var s when s == K_SELECTED_HEIGHT:
                // (Your original code intentionally did nothing here)
                break;

            case var s when s == K_CUSTOM_SCALE:
                {
                    var old = ApplyCustomScale;
                    if (bool.TryParse(optionValue, out var parsed) && parsed != old)
                    {
                        ApplyCustomScale = parsed;
                        _dirty = true;
                    }
                    break;
                }

            case var s when s == K_SELECTED_SCALE:
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

            case var s when s == K_REALWORLD_EYE_HEIGHT:
                {
                    var old = SelectedEyeHeight;
                    if (SliderReadOption(optionValue, out var current))
                    {
                        if (!Mathf.Approximately(old, current))
                        {
                            SelectedEyeHeight = current;
                            _dirty = true;
                        }
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Selected Scale", BasisDebug.LogTag.Device);
                    }
                    break;
                }

            case var s when s == K_PITCH_CALIBRATION:
                if (bool.TryParse(optionValue, out var pitchVal))
                {
                    PitchCalibrationEnabled = pitchVal;
                }
                break;

            // ---------- GLOBAL ONE EURO PARAMS ----------
            case var s when s == K_FBIK_MINCUTOFF:
                if (SliderReadOption(optionValue, out var f0)) BasisLocalRigDriver.MinCutoff = f0;
                break;

            case var s when s == K_FBIK_BETA:
                if (SliderReadOption(optionValue, out var f1)) BasisLocalRigDriver.Beta = f1;
                break;

            case var s when s == K_FBIK_DERIV_CUTOFF:
                if (SliderReadOption(optionValue, out var f2)) BasisLocalRigDriver.DerivativeCutoff = f2;
                break;

            case var s when s == K_FBIK_POS_SMOOTH_HZ:
                if (SliderReadOption(optionValue, out var f3)) BasisLocalRigDriver.PositionSmoothingHz = f3;
                break;

            case var s when s == K_FBIK_ROT_SMOOTH_HZ:
                if (SliderReadOption(optionValue, out var f4)) BasisLocalRigDriver.RotationSmoothingHz = f4;
                break;

            case var s when s == K_FBIK_SMOOTHING_STRENGTH:
                if (SliderReadOption(optionValue, out var f5)) BasisLocalRigDriver.SmoothingStrength = Mathf.Max(1f, f5);
                break;

            // ---------- HIPS ----------
            case var s when s == K_HIPS_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var b0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_Hips] = b0;
                break;

            case var s when s == K_HIPS_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var b1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_Hips] = b1;
                break;

            case var s when s == K_HIPS_EURO_POS:
                if (bool.TryParse(optionValue, out var b2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_Hips] = b2;
                break;

            case var s when s == K_HIPS_EURO_ROT:
                if (bool.TryParse(optionValue, out var b3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_Hips] = b3;
                break;

            // ---------- HEAD ----------
            case var s when s == K_HEAD_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var bh0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_Head] = bh0;
                break;

            case var s when s == K_HEAD_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var bh1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_Head] = bh1;
                break;

            case var s when s == K_HEAD_EURO_POS:
                if (bool.TryParse(optionValue, out var bh2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_Head] = bh2;
                break;

            case var s when s == K_HEAD_EURO_ROT:
                if (bool.TryParse(optionValue, out var bh3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_Head] = bh3;
                break;

            // ---------- LEFT FOOT ----------
            case var s when s == K_LF_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var blf0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftFoot] = blf0;
                break;

            case var s when s == K_LF_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var blf1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftFoot] = blf1;
                break;

            case var s when s == K_LF_EURO_POS:
                if (bool.TryParse(optionValue, out var blf2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftFoot] = blf2;
                break;

            case var s when s == K_LF_EURO_ROT:
                if (bool.TryParse(optionValue, out var blf3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftFoot] = blf3;
                break;

            // ---------- RIGHT FOOT ----------
            case var s when s == K_RF_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var brf0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightFoot] = brf0;
                break;

            case var s when s == K_RF_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var brf1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightFoot] = brf1;
                break;

            case var s when s == K_RF_EURO_POS:
                if (bool.TryParse(optionValue, out var brf2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightFoot] = brf2;
                break;

            case var s when s == K_RF_EURO_ROT:
                if (bool.TryParse(optionValue, out var brf3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightFoot] = brf3;
                break;

            // ---------- CHEST ----------
            case var s when s == K_CHEST_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var bc0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_Chest] = bc0;
                break;

            case var s when s == K_CHEST_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var bc1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_Chest] = bc1;
                break;

            case var s when s == K_CHEST_EURO_POS:
                if (bool.TryParse(optionValue, out var bc2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_Chest] = bc2;
                break;

            case var s when s == K_CHEST_EURO_ROT:
                if (bool.TryParse(optionValue, out var bc3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_Chest] = bc3;
                break;

            // ---------- LEFT LOWER LEG ----------
            case var s when s == K_LLL_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var blll0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftLowerLeg] = blll0;
                break;

            case var s when s == K_LLL_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var blll1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftLowerLeg] = blll1;
                break;

            case var s when s == K_LLL_EURO_POS:
                if (bool.TryParse(optionValue, out var blll2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftLowerLeg] = blll2;
                break;

            case var s when s == K_LLL_EURO_ROT:
                if (bool.TryParse(optionValue, out var blll3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftLowerLeg] = blll3;
                break;

            // ---------- RIGHT LOWER LEG ----------
            case var s when s == K_RLL_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var brll0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightLowerLeg] = brll0;
                break;

            case var s when s == K_RLL_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var brll1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightLowerLeg] = brll1;
                break;

            case var s when s == K_RLL_EURO_POS:
                if (bool.TryParse(optionValue, out var brll2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightLowerLeg] = brll2;
                break;

            case var s when s == K_RLL_EURO_ROT:
                if (bool.TryParse(optionValue, out var brll3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightLowerLeg] = brll3;
                break;

            // ---------- LEFT HAND ----------
            case var s when s == K_LH_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var blh0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftHand] = blh0;
                break;

            case var s when s == K_LH_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var blh1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftHand] = blh1;
                break;

            case var s when s == K_LH_EURO_POS:
                if (bool.TryParse(optionValue, out var blh2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftHand] = blh2;
                break;

            case var s when s == K_LH_EURO_ROT:
                if (bool.TryParse(optionValue, out var blh3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftHand] = blh3;
                break;

            // ---------- RIGHT HAND ----------
            case var s when s == K_RH_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var brh0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightHand] = brh0;
                break;

            case var s when s == K_RH_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var brh1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightHand] = brh1;
                break;

            case var s when s == K_RH_EURO_POS:
                if (bool.TryParse(optionValue, out var brh2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightHand] = brh2;
                break;

            case var s when s == K_RH_EURO_ROT:
                if (bool.TryParse(optionValue, out var brh3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightHand] = brh3;
                break;

            // ---------- LEFT LOWER ARM ----------
            case var s when s == K_LLA_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var blla0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftLowerArm] = blla0;
                break;

            case var s when s == K_LLA_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var blla1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftLowerArm] = blla1;
                break;

            case var s when s == K_LLA_EURO_POS:
                if (bool.TryParse(optionValue, out var blla2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftLowerArm] = blla2;
                break;

            case var s when s == K_LLA_EURO_ROT:
                if (bool.TryParse(optionValue, out var blla3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftLowerArm] = blla3;
                break;

            // ---------- RIGHT LOWER ARM ----------
            case var s when s == K_RLA_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var brla0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightLowerArm] = brla0;
                break;

            case var s when s == K_RLA_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var brla1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightLowerArm] = brla1;
                break;

            case var s when s == K_RLA_EURO_POS:
                if (bool.TryParse(optionValue, out var brla2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightLowerArm] = brla2;
                break;

            case var s when s == K_RLA_EURO_ROT:
                if (bool.TryParse(optionValue, out var brla3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightLowerArm] = brla3;
                break;

            // ---------- LEFT TOE ----------
            case var s when s == K_LT_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var blt0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftToe] = blt0;
                break;

            case var s when s == K_LT_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var blt1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftToe] = blt1;
                break;

            case var s when s == K_LT_EURO_POS:
                if (bool.TryParse(optionValue, out var blt2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftToe] = blt2;
                break;

            case var s when s == K_LT_EURO_ROT:
                if (bool.TryParse(optionValue, out var blt3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftToe] = blt3;
                break;

            // ---------- RIGHT TOE ----------
            case var s when s == K_RT_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var brt0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightToe] = brt0;
                break;

            case var s when s == K_RT_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var brt1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightToe] = brt1;
                break;

            case var s when s == K_RT_EURO_POS:
                if (bool.TryParse(optionValue, out var brt2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightToe] = brt2;
                break;

            case var s when s == K_RT_EURO_ROT:
                if (bool.TryParse(optionValue, out var brt3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightToe] = brt3;
                break;

            // ---------- LEFT SHOULDER ----------
            case var s when s == K_LS_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var bls0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_LeftShoulder] = bls0;
                break;

            case var s when s == K_LS_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var bls1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_LeftShoulder] = bls1;
                break;

            case var s when s == K_LS_EURO_POS:
                if (bool.TryParse(optionValue, out var bls2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_LeftShoulder] = bls2;
                break;

            case var s when s == K_LS_EURO_ROT:
                if (bool.TryParse(optionValue, out var bls3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_LeftShoulder] = bls3;
                break;

            // ---------- RIGHT SHOULDER ----------
            case var s when s == K_RS_SMOOTH_POS:
                if (bool.TryParse(optionValue, out var brs0)) BasisLocalRigDriver.SmoothPos[BasisLocalRigDriver.S_RightShoulder] = brs0;
                break;

            case var s when s == K_RS_SMOOTH_ROT:
                if (bool.TryParse(optionValue, out var brs1)) BasisLocalRigDriver.SmoothRot[BasisLocalRigDriver.S_RightShoulder] = brs1;
                break;

            case var s when s == K_RS_EURO_POS:
                if (bool.TryParse(optionValue, out var brs2)) BasisLocalRigDriver.EuroPos[BasisLocalRigDriver.S_RightShoulder] = brs2;
                break;

            case var s when s == K_RS_EURO_ROT:
                if (bool.TryParse(optionValue, out var brs3)) BasisLocalRigDriver.EuroRot[BasisLocalRigDriver.S_RightShoulder] = brs3;
                break;

            // ---------- IK COLLIDER & TUNING ----------
            case var s when s == K_FBIK_COLLISIONS_ENABLED:
                if (bool.TryParse(optionValue, out var colEn)) ApplyIKDataBool((ref BasisFullBodyData d) => d.CollisionsEnabled = colEn);
                break;

            case var s when s == K_FBIK_PROTECT_ELBOW:
                if (bool.TryParse(optionValue, out var peVal)) ApplyIKDataBool((ref BasisFullBodyData d) => d.ProtectElbow = peVal);
                break;

            case var s when s == K_FBIK_USE_HAND_CAPSULE:
                if (bool.TryParse(optionValue, out var hcVal)) ApplyIKDataBool((ref BasisFullBodyData d) => d.UseHandCapsule = hcVal);
                break;

            case var s when s == K_FBIK_CHEST_RADIUS:
                if (SliderReadOption(optionValue, out var crVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.ChestRadius = crVal);
                break;

            case var s when s == K_FBIK_COLLISION_SKIN:
                if (SliderReadOption(optionValue, out var csVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.CollisionSkin = csVal);
                break;

            case var s when s == K_FBIK_HAND_RADIUS:
                if (SliderReadOption(optionValue, out var hrVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.HandRadius = hrVal);
                break;

            case var s when s == K_FBIK_HAND_SKIN:
                if (SliderReadOption(optionValue, out var hsVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.HandSkin = hsVal);
                break;

            case var s when s == K_FBIK_SHOULDER_SOLVE:
                if (bool.TryParse(optionValue, out var ssVal)) ApplyIKDataBool((ref BasisFullBodyData d) => d.ShoulderSolveEnabled = ssVal);
                break;

            case var s when s == K_FBIK_SHOULDER_ELEVATION:
                if (SliderReadOption(optionValue, out var seVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.ShoulderElevationFactor = seVal);
                break;

            case var s when s == K_FBIK_SHOULDER_PROTRACTION:
                if (SliderReadOption(optionValue, out var spVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.ShoulderProtractionFactor = spVal);
                break;

            case var s when s == K_FBIK_MAX_BEND_DEG:
                if (SliderReadOption(optionValue, out var mbVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.MaxBendDeg = mbVal);
                break;

            case var s when s == K_FBIK_STRUGGLE_START:
                if (SliderReadOption(optionValue, out var ssStartVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.StruggleStart = ssStartVal);
                break;

            case var s when s == K_FBIK_STRUGGLE_END:
                if (SliderReadOption(optionValue, out var ssEndVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.StruggleEnd = ssEndVal);
                break;

            case var s when s == K_FBIK_MAX_CHEST_DELTA:
                if (SliderReadOption(optionValue, out var mcdVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.MaxChestDelta = mcdVal);
                break;

            case var s when s == K_FBIK_MAX_HIP_DELTA:
                if (SliderReadOption(optionValue, out var mhdVal)) ApplyIKDataFloat((ref BasisFullBodyData d) => d.MaxHipDelta = mhdVal);
                break;

            // ---------- CALIBRATION SPHERE SCALE ----------
            default:
                EnsureCalibKeyMap();
                if (_calibKeyToRole.TryGetValue(matchedSettingName, out var calibRole))
                {
                    if (SliderReadOption(optionValue, out var calibScale))
                    {
                        SphereScaleMultipliers[calibRole] = Mathf.Clamp(calibScale, 0.1f, 5f);
                    }
                }
                break;
        }
        BasisLocalPlayer.Instance.LocalRigDriver.UpdateEuroSettings();
    }

    public override void ChangedSettings()
    {
        if (!_dirty && _hasApplied)
            return;

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

        if (ApplyCustomScale || (!ApplyCustomScale && _lastApplyCustomScale == true))
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

    private static void ApplyIKLockMode()
    {
        if (BasisLocalPlayer.Instance == null || BasisLocalPlayer.Instance.LocalRigDriver == null)
            return;

        var constraint = BasisLocalPlayer.Instance.LocalRigDriver.BasisFullIKConstraint;
        if (constraint == null)
            return;

        var data = constraint.data;
        data.IKLockMode = (float)CurrentIKLockMode;
        constraint.data = data;
    }

    private delegate void IKDataAction(ref BasisFullBodyData data);

    private static void ApplyIKDataBool(IKDataAction action)
    {
        if (BasisLocalPlayer.Instance == null || BasisLocalPlayer.Instance.LocalRigDriver == null)
            return;

        var constraint = BasisLocalPlayer.Instance.LocalRigDriver.BasisFullIKConstraint;
        if (constraint == null)
            return;

        var data = constraint.data;
        action(ref data);
        constraint.data = data;
    }

    private static void ApplyIKDataFloat(IKDataAction action)
    {
        ApplyIKDataBool(action);
    }
}
