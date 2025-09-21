/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.Util;
using UnityEngine;

/// <summary>
/// This class manages the face expressions data provided per frame, and is responsible for stopping and
/// starting face tracking. Use this class to read face tracking data, accessible via <see cref="OVRFaceExpressions.this"/>
/// and <see cref="OVRFaceExpressions.GetWeight"/> to drive the blend shapes on a <see cref="SkinnedMeshRenderer"/>.
/// For more information, see [Face Tracking for Movement SDK for Unity](https://developer.oculus.com/documentation/unity/move-face-tracking/).
/// </summary>
/// <remarks>
/// Refer to the <see cref="OVRFaceExpressions.FaceExpression"/> enum for the list of face expressions that contain
/// weights that can be applied to blend shapes.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/move-face-tracking/")]
[Feature(Feature.FaceTracking)]
public class OVRFaceExpressions : MonoBehaviour, IReadOnlyCollection<float>, OVRFaceExpressions.WeightProvider
{
    /// <summary>
    /// The interface for the weight provider that <see cref="OVRFaceExpressions"/> uses to expose information
    /// about the face expressions weights available from face tracking.
    /// </summary>
    public interface WeightProvider
    {
        float GetWeight(FaceExpression expression);
    }

    /// <summary>
    /// This will be true if face tracking is enabled, otherwise false. This is returning the current face tracking
    /// enabled state from <see cref="OVRPlugin"/> - to enable/disable face tracking, please refer to
    /// <see cref="OVRPlugin.StartFaceTracking2"/> and <see cref="OVRPlugin.StopFaceTracking"/>.
    /// </summary>
    public bool FaceTrackingEnabled => OVRPlugin.faceTracking2Enabled;

    /// <summary>
    /// True if the facial expressions returned from the current face tracking data are valid, otherwise false. This
    /// is equivalent to checking if the <see cref="OVRPlugin.FaceState"/> is valid on this frame.
    /// </summary>
    /// <remarks>
    /// This value gets updated in every frame. You should check this
    /// value before querying for face expressions.
    /// </remarks>
    public bool ValidExpressions { get; private set; }

    /// <summary>
    /// True if the eye look-related blend shapes are valid, otherwise false.
    /// </summary>
    /// <remarks>
    /// This property affects the behavior of two sets of blend shapes.
    ///
    /// **EyesLook:**
    /// - <see cref="FaceExpression.EyesLookDownL"/>
    /// - <see cref="FaceExpression.EyesLookDownR"/>
    /// - <see cref="FaceExpression.EyesLookLeftL"/>
    /// - <see cref="FaceExpression.EyesLookLeftR"/>
    /// - <see cref="FaceExpression.EyesLookRightL"/>
    /// - <see cref="FaceExpression.EyesLookRightR"/>
    /// - <see cref="FaceExpression.EyesLookUpL"/>
    /// - <see cref="FaceExpression.EyesLookUpR"/>
    ///
    /// **EyesClosed:**
    /// - <see cref="FaceExpression.EyesClosedL"/>
    /// - <see cref="FaceExpression.EyesClosedR"/>
    ///
    /// **When <see cref="EyeFollowingBlendshapesValid"/> is `false`:**
    /// - The `EyesLook` blend shapes are set to zero.
    /// - The `EyesClosed` blend shapes range from 0..1, and represent the true state of the eyelids.
    ///
    /// **When <see cref="EyeFollowingBlendshapesValid"/> is `true`:**
    /// - The `EyesLook` blend shapes are valid.
    /// - The `EyesClosed` blend shapes are modified so that the sum of the `EyesClosedX` and `EyesLookDownX` blend shapes
    ///   range from 0..1. This helps avoid double deformation of the avatar's eye lids when they may be driven by both
    ///   the `EyesClosed` and `EyesLookDown` blend shapes. To recover the true `EyesClosed` values, add the
    ///   minimum of `EyesLookDownL` and `EyesLookDownR` blend shapes back using the following formula:<br />
    ///   `EyesClosedL` += min(`EyesLookDownL`, `EyesLookDownR`)<br />
    ///   `EyesClosedR` += min(`EyesLookDownL`, `EyesLookDownR`)
    /// </remarks>
    public bool EyeFollowingBlendshapesValid { get; private set; }

    private OVRPlugin.FaceState _currentFaceState;

    /// <summary>
    /// True if the visemes are valid, otherwise false.
    /// </summary>
    /// <remarks>
    /// This value gets updated in every frame. You should check this
    /// value before querying for visemes.
    /// If you query visemes when it's false,
    /// InvalidOperationException will be thrown.
    /// </remarks>
    public bool AreVisemesValid { get; private set; }

    private OVRPlugin.FaceVisemesState _currentFaceVisemesState;

    private const OVRPermissionsRequester.Permission FaceTrackingPermission =
        OVRPermissionsRequester.Permission.FaceTracking;
    private const OVRPermissionsRequester.Permission RecordAudioPermission =
        OVRPermissionsRequester.Permission.RecordAudio;

    private Action<string> _onPermissionGranted;
    private static int _trackingInstanceCount;

    private void Awake()
    {
        _onPermissionGranted = OnPermissionGranted;
    }

    private void OnEnable()
    {
        _trackingInstanceCount++;

        if (!StartFaceTracking())
        {
            enabled = false;
        }
    }

    private void OnPermissionGranted(string permissionId)
    {
        if (permissionId == OVRPermissionsRequester.GetPermissionId(FaceTrackingPermission) ||
            permissionId == OVRPermissionsRequester.GetPermissionId(RecordAudioPermission))
        {
            OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
            enabled = true;
        }
    }

    private OVRPlugin.FaceTrackingDataSource[] GetRequestedFaceTrackingDataSources()
    {
        var runtimeSettings = OVRRuntimeSettings.GetRuntimeSettings();
        if (runtimeSettings.RequestsAudioFaceTracking && runtimeSettings.RequestsVisualFaceTracking)
        {
            return new OVRPlugin.FaceTrackingDataSource[] { OVRPlugin.FaceTrackingDataSource.Visual, OVRPlugin.FaceTrackingDataSource.Audio };
        }
        else if (runtimeSettings.RequestsVisualFaceTracking)
        {
            return new OVRPlugin.FaceTrackingDataSource[] { OVRPlugin.FaceTrackingDataSource.Visual };
        }
        else if (runtimeSettings.RequestsAudioFaceTracking)
        {
            return new OVRPlugin.FaceTrackingDataSource[] { OVRPlugin.FaceTrackingDataSource.Audio };
        }
        else
        {
            return new OVRPlugin.FaceTrackingDataSource[] { };
        }
    }

    private bool StartFaceTracking()
    {
        if (!OVRPermissionsRequester.IsPermissionGranted(FaceTrackingPermission) &&
            !OVRPermissionsRequester.IsPermissionGranted(RecordAudioPermission))
        {
            OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
            OVRPermissionsRequester.PermissionGranted += _onPermissionGranted;
            return false;
        }

        if (!OVRPlugin.StartFaceTracking2(GetRequestedFaceTrackingDataSources()))
        {
            Debug.LogWarning($"[{nameof(OVRFaceExpressions)}] Failed to start face tracking.");
            return false;
        }

        OVRPlugin.SetFaceTrackingVisemesEnabled(OVRRuntimeSettings.GetRuntimeSettings().EnableFaceTrackingVisemesOutput);

        return true;
    }

    private void OnDisable()
    {
        if (--_trackingInstanceCount == 0)
        {
            OVRPlugin.StopFaceTracking2();
        }
    }

    private void OnDestroy()
    {
        OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
    }

    private void Update()
    {
        ValidExpressions =
            OVRPlugin.GetFaceState2(OVRPlugin.Step.Render, -1, ref _currentFaceState)
            && _currentFaceState.Status.IsValid;

        EyeFollowingBlendshapesValid = ValidExpressions && _currentFaceState.Status.IsEyeFollowingBlendshapesValid;

        AreVisemesValid =
            OVRPlugin.GetFaceVisemesState(OVRPlugin.Step.Render, ref _currentFaceVisemesState) == OVRPlugin.Result.Success
            && _currentFaceVisemesState.IsValid;
    }


    /// <summary>
    /// This will return the weight of the specified <see cref="FaceExpression"/> present in the expression weights array.
    /// </summary>
    /// <returns>Returns weight of the specified <see cref="FaceExpression"/>,
    /// which will be within the range of 0.0f to 1.0f inclusive.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ValidExpressions"/> is false.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="expression"/> value is not in range.
    /// </exception>
    public float this[FaceExpression expression]
    {
        get
        {
            CheckValidity();

            if (expression < 0 || expression >= FaceExpression.Max)
            {
                throw new ArgumentOutOfRangeException(nameof(expression),
                    expression,
                    $"Value must be between 0 to {(int)FaceExpression.Max}");
            }

            return _currentFaceState.ExpressionWeights[(int)expression];
        }
    }

    /// <summary>
    /// Returns the weight of the specified <see cref="FaceExpression"/> by accessing the expression weights array
    /// present through <see cref="this"/>.
    /// </summary>
    /// <param name="expression">The specified <see cref="FaceExpression"/> to get the weight for.</param>
    /// <returns>The weight of the specified <see cref="FaceExpression"/>.</returns>
    public float GetWeight(FaceExpression expression) => this[expression];

    /// <summary>
    /// This method will try to get the weight of the specified <see cref="FaceExpression"/> if it's
    /// valid. This can be used if it isn't certain that the specified <see cref="FaceExpression"/>
    /// is a valid expression, or if the facial expressions on this frame are valid.
    /// </summary>
    /// <param name="expression" cref="FaceExpression">The expression to get the weight of.</param>
    /// <param name="weight">The output argument that will contain the expression weight or 0.0 if it's not valid.</param>
    /// <returns>Returns true if the expression weight is valid, false otherwise.</returns>
    public bool TryGetFaceExpressionWeight(FaceExpression expression, out float weight)
    {
        if (!ValidExpressions || expression < 0 || expression >= FaceExpression.Max)
        {
            weight = 0;
            return false;
        }

        weight = _currentFaceState.ExpressionWeights[(int)expression];
        return true;
    }

    /// <summary>
    /// This will return the weight of the given viseme.
    /// </summary>
    /// <returns>Returns weight of viseme ranged between 0.0 to 1.0.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="OVRFaceExpressions.AreVisemesValid"/> is false.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="viseme"/> value is not in range.
    /// </exception>
    public float GetViseme(FaceViseme viseme)
    {
        CheckVisemesValidity();

        if (viseme < 0 || viseme >= FaceViseme.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(viseme),
                viseme,
                $"Value must be between 0 to {(int)FaceViseme.Count}");
        }

        return _currentFaceVisemesState.Visemes[(int)viseme];
    }

    /// <summary>
    /// This method tries to gets the weight of the given viseme if it's available.
    /// </summary>
    /// <param name="viseme" cref="FaceViseme">The viseme to get the weight of.</param>
    /// <param name="weight">The output argument that will contain the viseme weight or 0.0 if it's not available.</param>
    /// <returns>Returns true if the viseme weight is valid, false otherwise.</returns>
    public bool TryGetFaceViseme(FaceViseme viseme, out float weight)
    {
        if (!AreVisemesValid || viseme < 0 || viseme >= FaceViseme.Count)
        {
            weight = 0;
            return false;
        }

        weight = _currentFaceVisemesState.Visemes[(int)viseme];
        return true;
    }

    /// <summary>
    /// Copies visemes to a pre-allocated array.
    /// </summary>
    /// <param name="array">Pre-allocated destination array for visemes</param>
    /// <param name="startIndex">Starting index in the destination array</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="array"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when there is not enough capacity to copy weights to <paramref name="array"/> at <paramref name="startIndex"/> index.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="startIndex"/> value is out of <paramref name="array"/> bounds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="OVRFaceExpressions.AreVisemesValid"/> is false.
    /// </exception>
    public void CopyVisemesTo(float[] array, int startIndex = 0)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        if (startIndex < 0 || startIndex >= array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                startIndex,
                $"Value must be between 0 to {array.Length - 1}");
        }

        if (array.Length - startIndex < (int)FaceViseme.Count)
        {
            throw new ArgumentException(
                $"Capacity is too small - required {(int)FaceViseme.Count}, available {array.Length - startIndex}.",
                nameof(array));
        }

        CheckVisemesValidity();
        for (int i = 0; i < (int)FaceViseme.Count; i++)
        {
            array[i + startIndex] = _currentFaceVisemesState.Visemes[i];
        }
    }

    /// <summary>
    /// The face part type used for getting the face tracking confidence weight in <see cref="TryGetWeightConfidence"/>.
    /// </summary>
    public enum FaceRegionConfidence
    {
        /// <summary>
        /// Represents the lower part of the face. It includes the mouth, chin and a portion of the nose and cheek.
        /// </summary>
        Lower = OVRPlugin.FaceRegionConfidence.Lower,

        /// <summary>
        /// Represents the upper part of the face. It includes the eyes, eyebrows and a portion of the nose and cheek.
        /// </summary>
        Upper = OVRPlugin.FaceRegionConfidence.Upper,

        /// <summary>
        /// Used to determine the size of the <see cref="FaceRegionConfidence"/> enum.
        /// </summary>
        Max = OVRPlugin.FaceRegionConfidence.Max
    }

    /// <summary>
    /// This method tries to get the confidence weight of the given face part if it's available. This can be used
    /// if it isn't certain that the facial expressions on this frame are valid.
    /// </summary>
    /// <param name="region" cref="FaceRegionConfidence">The part of the face to get the confidence weight of.</param>
    /// <param name="weightConfidence">The output argument that will contain the weight confidence or 0.0 if it's not valid.</param>
    /// <returns>Returns true if the weight confidence is valid, false otherwise.</returns>
    public bool TryGetWeightConfidence(FaceRegionConfidence region, out float weightConfidence)
    {
        if (!ValidExpressions || region < 0 || region >= FaceRegionConfidence.Max)
        {
            weightConfidence = 0;
            return false;
        }

        weightConfidence = _currentFaceState.ExpressionWeightConfidences[(int)region];
        return true;
    }

    /// <summary>
    /// The source type that the face tracking data is currently based off of. This is part of the data contained
    /// in <see cref="OVRPlugin.FaceState"/>.
    /// </summary>
    public enum FaceTrackingDataSource
    {
        /// <summary>
        /// Represents visual based face tracking. This is the case if the face tracking data came from
        /// visual based face tracking.
        /// </summary>
        Visual = OVRPlugin.FaceTrackingDataSource.Visual,

        /// <summary>
        /// Represents audio based face tracking. if the face tracking data came from audio based face tracking.
        /// </summary>
        Audio = OVRPlugin.FaceTrackingDataSource.Audio,

        /// <summary>
        /// Used to determine the size of the <see cref="FaceTrackingDataSource"/> enum.
        /// </summary>
        [InspectorName(null)]
        Count = OVRPlugin.FaceTrackingDataSource.Count,
    }

    /// <summary>
    /// This method tries to get the data source that was used for the current frame for face tracking data. This
    /// can be used if it isn't certain that the facial expressions on this frame are valid.
    /// </summary>
    /// <param name="dataSource" cref="FaceTrackingDataSource">The output argument that will contain the tracking
    /// data source.</param>
    /// <returns>Returns true if the face tracking data source is valid, false otherwise.</returns>
    public bool TryGetFaceTrackingDataSource(out FaceTrackingDataSource dataSource)
    {
        dataSource = (FaceTrackingDataSource)_currentFaceState.DataSource;
        return ValidExpressions;
    }

    internal void CheckValidity()
    {
        if (!ValidExpressions)
        {
            throw new InvalidOperationException(
                $"Face expressions are not valid at this time. Use {nameof(ValidExpressions)} to check for validity.");
        }
    }

    internal void CheckVisemesValidity()
    {
        if (!AreVisemesValid)
        {
            throw new InvalidOperationException(
                $"Face visemes are not valid at this time. Use {nameof(AreVisemesValid)} to check for validity.");
        }
    }

    /// <summary>
    /// Copies expression weights to a pre-allocated array.
    /// </summary>
    /// <param name="array">Pre-allocated destination array for expression weights</param>
    /// <param name="startIndex">Starting index in the destination array</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="array"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when there is not enough capacity to copy weights to <paramref name="array"/> at <paramref name="startIndex"/> index.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="startIndex"/> value is out of <paramref name="array"/> bounds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="OVRFaceExpressions.ValidExpressions"/> is false.
    /// </exception>
    public void CopyTo(float[] array, int startIndex = 0)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        if (startIndex < 0 || startIndex >= array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                startIndex,
                $"Value must be between 0 to {array.Length - 1}");
        }

        if (array.Length - startIndex < (int)FaceExpression.Max)
        {
            throw new ArgumentException(
                $"Capacity is too small - required {(int)FaceExpression.Max}, available {array.Length - startIndex}.",
                nameof(array));
        }

        CheckValidity();
        for (int i = 0; i < (int)FaceExpression.Max; i++)
        {
            array[i + startIndex] = _currentFaceState.ExpressionWeights[i];
        }
    }

    /// <summary>
    /// Allocates a float array and copies expression weights to it.
    /// </summary>
    public float[] ToArray()
    {
        var array = new float[(int)OVRFaceExpressions.FaceExpression.Max];
        this.CopyTo(array);
        return array;
    }

    /// <summary>
    /// List of face expressions, based off of the Facial Action Coding System (FACS).
    /// </summary>
    public enum FaceExpression
    {
        [InspectorName("None")]
        Invalid = OVRPlugin.FaceExpression2.Invalid,
        BrowLowererL = OVRPlugin.FaceExpression2.Brow_Lowerer_L,
        BrowLowererR = OVRPlugin.FaceExpression2.Brow_Lowerer_R,
        CheekPuffL = OVRPlugin.FaceExpression2.Cheek_Puff_L,
        CheekPuffR = OVRPlugin.FaceExpression2.Cheek_Puff_R,
        CheekRaiserL = OVRPlugin.FaceExpression2.Cheek_Raiser_L,
        CheekRaiserR = OVRPlugin.FaceExpression2.Cheek_Raiser_R,
        CheekSuckL = OVRPlugin.FaceExpression2.Cheek_Suck_L,
        CheekSuckR = OVRPlugin.FaceExpression2.Cheek_Suck_R,
        ChinRaiserB = OVRPlugin.FaceExpression2.Chin_Raiser_B,
        ChinRaiserT = OVRPlugin.FaceExpression2.Chin_Raiser_T,
        DimplerL = OVRPlugin.FaceExpression2.Dimpler_L,
        DimplerR = OVRPlugin.FaceExpression2.Dimpler_R,
        EyesClosedL = OVRPlugin.FaceExpression2.Eyes_Closed_L,
        EyesClosedR = OVRPlugin.FaceExpression2.Eyes_Closed_R,
        EyesLookDownL = OVRPlugin.FaceExpression2.Eyes_Look_Down_L,
        EyesLookDownR = OVRPlugin.FaceExpression2.Eyes_Look_Down_R,
        EyesLookLeftL = OVRPlugin.FaceExpression2.Eyes_Look_Left_L,
        EyesLookLeftR = OVRPlugin.FaceExpression2.Eyes_Look_Left_R,
        EyesLookRightL = OVRPlugin.FaceExpression2.Eyes_Look_Right_L,
        EyesLookRightR = OVRPlugin.FaceExpression2.Eyes_Look_Right_R,
        EyesLookUpL = OVRPlugin.FaceExpression2.Eyes_Look_Up_L,
        EyesLookUpR = OVRPlugin.FaceExpression2.Eyes_Look_Up_R,
        InnerBrowRaiserL = OVRPlugin.FaceExpression2.Inner_Brow_Raiser_L,
        InnerBrowRaiserR = OVRPlugin.FaceExpression2.Inner_Brow_Raiser_R,
        JawDrop = OVRPlugin.FaceExpression2.Jaw_Drop,
        JawSidewaysLeft = OVRPlugin.FaceExpression2.Jaw_Sideways_Left,
        JawSidewaysRight = OVRPlugin.FaceExpression2.Jaw_Sideways_Right,
        JawThrust = OVRPlugin.FaceExpression2.Jaw_Thrust,
        LidTightenerL = OVRPlugin.FaceExpression2.Lid_Tightener_L,
        LidTightenerR = OVRPlugin.FaceExpression2.Lid_Tightener_R,
        LipCornerDepressorL = OVRPlugin.FaceExpression2.Lip_Corner_Depressor_L,
        LipCornerDepressorR = OVRPlugin.FaceExpression2.Lip_Corner_Depressor_R,
        LipCornerPullerL = OVRPlugin.FaceExpression2.Lip_Corner_Puller_L,
        LipCornerPullerR = OVRPlugin.FaceExpression2.Lip_Corner_Puller_R,
        LipFunnelerLB = OVRPlugin.FaceExpression2.Lip_Funneler_LB,
        LipFunnelerLT = OVRPlugin.FaceExpression2.Lip_Funneler_LT,
        LipFunnelerRB = OVRPlugin.FaceExpression2.Lip_Funneler_RB,
        LipFunnelerRT = OVRPlugin.FaceExpression2.Lip_Funneler_RT,
        LipPressorL = OVRPlugin.FaceExpression2.Lip_Pressor_L,
        LipPressorR = OVRPlugin.FaceExpression2.Lip_Pressor_R,
        LipPuckerL = OVRPlugin.FaceExpression2.Lip_Pucker_L,
        LipPuckerR = OVRPlugin.FaceExpression2.Lip_Pucker_R,
        LipStretcherL = OVRPlugin.FaceExpression2.Lip_Stretcher_L,
        LipStretcherR = OVRPlugin.FaceExpression2.Lip_Stretcher_R,
        LipSuckLB = OVRPlugin.FaceExpression2.Lip_Suck_LB,
        LipSuckLT = OVRPlugin.FaceExpression2.Lip_Suck_LT,
        LipSuckRB = OVRPlugin.FaceExpression2.Lip_Suck_RB,
        LipSuckRT = OVRPlugin.FaceExpression2.Lip_Suck_RT,
        LipTightenerL = OVRPlugin.FaceExpression2.Lip_Tightener_L,
        LipTightenerR = OVRPlugin.FaceExpression2.Lip_Tightener_R,
        LipsToward = OVRPlugin.FaceExpression2.Lips_Toward,
        LowerLipDepressorL = OVRPlugin.FaceExpression2.Lower_Lip_Depressor_L,
        LowerLipDepressorR = OVRPlugin.FaceExpression2.Lower_Lip_Depressor_R,
        MouthLeft = OVRPlugin.FaceExpression2.Mouth_Left,
        MouthRight = OVRPlugin.FaceExpression2.Mouth_Right,
        NoseWrinklerL = OVRPlugin.FaceExpression2.Nose_Wrinkler_L,
        NoseWrinklerR = OVRPlugin.FaceExpression2.Nose_Wrinkler_R,
        OuterBrowRaiserL = OVRPlugin.FaceExpression2.Outer_Brow_Raiser_L,
        OuterBrowRaiserR = OVRPlugin.FaceExpression2.Outer_Brow_Raiser_R,
        UpperLidRaiserL = OVRPlugin.FaceExpression2.Upper_Lid_Raiser_L,
        UpperLidRaiserR = OVRPlugin.FaceExpression2.Upper_Lid_Raiser_R,
        UpperLipRaiserL = OVRPlugin.FaceExpression2.Upper_Lip_Raiser_L,
        UpperLipRaiserR = OVRPlugin.FaceExpression2.Upper_Lip_Raiser_R,
        TongueTipInterdental = OVRPlugin.FaceExpression2.Tongue_Tip_Interdental,
        TongueTipAlveolar = OVRPlugin.FaceExpression2.Tongue_Tip_Alveolar,
        TongueFrontDorsalPalate = OVRPlugin.FaceExpression2.Tongue_Front_Dorsal_Palate,
        TongueMidDorsalPalate = OVRPlugin.FaceExpression2.Tongue_Mid_Dorsal_Palate,
        TongueBackDorsalVelar = OVRPlugin.FaceExpression2.Tongue_Back_Dorsal_Velar,
        TongueOut = OVRPlugin.FaceExpression2.Tongue_Out,
        TongueRetreat = OVRPlugin.FaceExpression2.Tongue_Retreat,
        [InspectorName(null)]
        Max = OVRPlugin.FaceExpression2.Max,
    }

    #region Face expressions enumerator

    /// <summary>
    /// Gets the face expressions enumerator, used for enumerating over <see cref="OVRFaceExpressions"/>
    /// as a collection to read data in this collection of facial expressions by accessing <see cref="this"/>.
    /// </summary>
    /// <returns></returns>
    public FaceExpressionsEnumerator GetEnumerator() =>
        new FaceExpressionsEnumerator(_currentFaceState.ExpressionWeights);

    IEnumerator<float> IEnumerable<float>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// The number of items in <see cref="this"/> collection (facial expression weights).
    /// </summary>
    public int Count => _currentFaceState.ExpressionWeights?.Length ?? 0;

    /// <summary>
    /// The implementation of IEnumerator for face expressions weights, used for enumerating directly over
    /// <see cref="OVRFaceExpressions"/>. This is used when reading this data as a collection of facial expressions
    /// by accessing <see cref="this"/>.
    /// </summary>
    public struct FaceExpressionsEnumerator : IEnumerator<float>
    {
        private float[] _faceExpressions;

        private int _index;

        private int _count;

        internal FaceExpressionsEnumerator(float[] array)
        {
            _faceExpressions = array;
            _index = -1;
            _count = _faceExpressions?.Length ?? 0;
        }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns>Returns true if the enumerator was successfully advanced to the next element in this collection,
        /// false otherwise.</returns>
        public bool MoveNext() => ++_index < _count;

        /// <summary>
        /// Gets the element of this collection at the current position of the enumerator.
        /// </summary>
        public float Current => _faceExpressions[_index];

        object IEnumerator.Current => Current;

        /// <summary>
        /// Sets the enumerator to its initial position, which is before the first element in the collection.
        /// </summary>
        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }

    #endregion

    /// <summary>
    /// List of face visemes.
    /// </summary>
    public enum FaceViseme
    {
        [InspectorName("None")]
        Invalid = OVRPlugin.FaceViseme.Invalid,

        /// <summary>The viseme representing silence.</summary>
        SIL = OVRPlugin.FaceViseme.SIL,

        /// <summary>The viseme representing p, b, and m.</summary>
        PP = OVRPlugin.FaceViseme.PP,

        /// <summary>The viseme representing f and v.</summary>
        FF = OVRPlugin.FaceViseme.FF,

        /// <summary>The viseme representing th.</summary>
        TH = OVRPlugin.FaceViseme.TH,

        /// <summary>The viseme representing t and d.</summary>
        DD = OVRPlugin.FaceViseme.DD,

        /// <summary>The viseme representing k and g.</summary>
        KK = OVRPlugin.FaceViseme.KK,

        /// <summary>The viseme representing tS, dZ, and S.</summary>
        CH = OVRPlugin.FaceViseme.CH,

        /// <summary>The viseme representing s and z.</summary>
        SS = OVRPlugin.FaceViseme.SS,

        /// <summary>The viseme representing n and l.</summary>
        NN = OVRPlugin.FaceViseme.NN,

        /// <summary>The viseme representing r.</summary>
        RR = OVRPlugin.FaceViseme.RR,

        /// <summary>The viseme representing a:.</summary>
        AA = OVRPlugin.FaceViseme.AA,

        /// <summary>The viseme representing e.</summary>
        E = OVRPlugin.FaceViseme.E,

        /// <summary>The viseme representing ih.</summary>
        IH = OVRPlugin.FaceViseme.IH,

        /// <summary>The viseme representing oh.</summary>
        OH = OVRPlugin.FaceViseme.OH,

        /// <summary>The viseme representing ou.</summary>
        OU = OVRPlugin.FaceViseme.OU,

        [InspectorName(null)]
        Count = OVRPlugin.FaceViseme.Count,
    }
}
