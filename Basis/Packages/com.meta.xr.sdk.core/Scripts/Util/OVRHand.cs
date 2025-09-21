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
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// The OVRHand class provides hand related data which can be used by other classes such as the <see cref="OVRSkeleton"/>,
/// the <see cref="OVRMesh"/> or the <see cref="OVRSkeletonRenderer"/>.
/// For example, it can detect whether a given finger is currently pinching, the pinch’s strength, and the confidence
/// level of a finger pose.
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-handtracking/")]
[Feature(Feature.Hands)]
public class OVRHand : MonoBehaviour,
    OVRInputModule.InputSource,
    OVRSkeleton.IOVRSkeletonDataProvider,
    OVRSkeletonRenderer.IOVRSkeletonRendererDataProvider,
    OVRMesh.IOVRMeshDataProvider,
    OVRMeshRenderer.IOVRMeshRendererDataProvider
{

    /// <summary>
    /// This enum dictates if a hand is a left or right hand. It's used in many scenarios such as choosing which hand
    /// mesh to return to <see cref="OVRMesh"/>, which skeleton to return, etc.
    /// </summary>
    public enum Hand
    {
        None = OVRPlugin.Hand.None,
        HandLeft = OVRPlugin.Hand.HandLeft,
        HandRight = OVRPlugin.Hand.HandRight,
    }

    /// <summary>
    /// This enum is used for clarifying which finger you are currently working with or need data on.
    /// For example, you can pass "HandFinger.Ring" to <see cref="GetFingerIsPinching(HandFinger)"/> to check if the
    /// ring finger is pinching.
    /// </summary>
    public enum HandFinger
    {
        Thumb = OVRPlugin.HandFinger.Thumb,
        Index = OVRPlugin.HandFinger.Index,
        Middle = OVRPlugin.HandFinger.Middle,
        Ring = OVRPlugin.HandFinger.Ring,
        Pinky = OVRPlugin.HandFinger.Pinky,
        Max = OVRPlugin.HandFinger.Max,
    }

    /// <summary>
    /// This enum refers to the level of confidence of a pose. For an example of how this can be used, see method
    /// <see cref="GetFingerConfidence(HandFinger)"/>
    /// </summary>
    public enum TrackingConfidence
    {
        Low = OVRPlugin.TrackingConfidence.Low,
        High = OVRPlugin.TrackingConfidence.High
    }

    [SerializeField]
    internal Hand HandType = Hand.None;

    [SerializeField]
    private Transform _pointerPoseRoot = null;

    /// <summary>
    /// Determines if the controller should be hidden based on held state.
    /// </summary>
    public OVRInput.InputDeviceShowState m_showState = OVRInput.InputDeviceShowState.ControllerNotInHand;

    /// <summary>
    /// An optional component for provind shell like ray functionality - highlighting where you're selecting in the UI and responding to pinches / button presses.
    /// </summary>
    public OVRRayHelper RayHelper;

    private GameObject _pointerPoseGO;
    private OVRPlugin.HandState _handState = new OVRPlugin.HandState();

    // Track index pinching for UI interactions.
    private bool _wasIndexPinching = false;
    private bool _wasReleased = false;

    /// <summary>
    /// Enumerates the types of microgestures that can be recognized by the OVR hand tracking system.
    /// Each microgesture corresponds to a specific hand movement or gesture recognized by the system.
    /// </summary>
    public enum MicrogestureType
    {
        NoGesture = OVRPlugin.MicrogestureType.NoGesture,
        SwipeLeft = OVRPlugin.MicrogestureType.SwipeLeft,
        SwipeRight = OVRPlugin.MicrogestureType.SwipeRight,
        SwipeForward = OVRPlugin.MicrogestureType.SwipeForward,
        SwipeBackward = OVRPlugin.MicrogestureType.SwipeBackward,
        ThumbTap = OVRPlugin.MicrogestureType.ThumbTap,
        Invalid = OVRPlugin.MicrogestureType.Invalid,
    }


    private OVRPlugin.HandTrackingState _handTrackingState = new OVRPlugin.HandTrackingState();
    private bool _handTrackingStateValid;

    private static OVRHandSkeletonVersion GlobalHandSkeletonVersion =>
        OVRRuntimeSettings.Instance.HandSkeletonVersion;

    /// <summary>
    /// True when the data received from this hand is valid. Data can be invalid for different reasons. For
    /// example, the <see cref="OVRPlugin"/> might not have finished initializing.
    /// </summary>
    public bool IsDataValid { get; private set; }

    /// <summary>
    /// True when there is high confidence on the data being provided.
    /// </summary>
    public bool IsDataHighConfidence { get; private set; }

    /// <summary>
    /// True when the hand is being tracked. If this is false, the <see cref="OVRPlugin"/> might not have finished initializing,
    /// or hand tracking was lost.
    /// </summary>
    public bool IsTracked { get; private set; }

    /// <summary>
    /// True when a system gesture is in progress. A system gesture is a reserved gesture that allows users to transition
    /// to the Meta Quest universal menu. This behavior occurs when users place their dominant hand up with the palm facing
    /// the user and pinch with their index finger.
    /// </summary>
    public bool IsSystemGestureInProgress { get; private set; }

    /// <summary>
    /// True when the <see cref="PointerPose"/> is valid. The <see cref="PointerPose"/> may or may not be valid, depending
    /// on the user’s hand position, tracking status, and other factors.
    /// </summary>
    public bool IsPointerPoseValid { get; private set; }

    /// <summary>
    /// The pointer pose determines the direction that the user is pointing in. It indicates the starting point
    /// and position of the pointing ray in the tracking space. It's useful for things like UI interactions where
    /// the user pinches to click on something.
    /// </summary>
    public Transform PointerPose
    {
        get
        {
            if (_pointerPoseGO == null)
            {
                InitializePointerPose();
            }
            return _pointerPoseGO.transform;
        }
    }
    public float HandScale { get; private set; }

    /// <summary>
    /// The level of confidence of the data being provided about this hand.
    /// </summary>
    public TrackingConfidence HandConfidence { get; private set; }

    /// <summary>
    /// True when this hand is the same type (left/right) as the user's dominant hand.
    /// </summary>
    public bool IsDominantHand { get; private set; }

    private void InitializePointerPose()
    {
        _pointerPoseGO = new GameObject($"{HandType} {nameof(PointerPose)}");
        DontDestroyOnLoad(_pointerPoseGO);
        _pointerPoseGO.hideFlags = HideFlags.HideAndDontSave;
        if (_pointerPoseRoot != null)
        {
            PointerPose.SetParent(_pointerPoseRoot, false);
        }
    }

    private void Awake()
    {
        if (_pointerPoseGO == null)
        {
            InitializePointerPose();
        }

        if (RayHelper != null)
        {
            RayHelper.transform.SetParent(PointerPose, false);
        }

        GetHandState(OVRPlugin.Step.Render);
    }

    private void Update()
    {
        GetHandState(OVRPlugin.Step.Render);

        bool newPinching = GetFingerIsPinching(HandFinger.Index);
        _wasReleased = !newPinching && _wasIndexPinching;
        _wasIndexPinching = newPinching;

        if (RayHelper && !IsActive() && RayHelper.isActiveAndEnabled)
        {
            RayHelper.gameObject.SetActive(false);
        }
    }

    private void FixedUpdate()
    {
        if (OVRPlugin.nativeXrApi != OVRPlugin.XrApi.OpenXR)
        {
            GetHandState(OVRPlugin.Step.Physics);
        }

        if (RayHelper != null)
        {
            RayHelper.gameObject.SetActive(IsDataValid);
        }
    }

    private void OnDestroy()
    {
        if (_pointerPoseGO != null)
        {
            Destroy(_pointerPoseGO);
        }
    }

    private void GetHandState(OVRPlugin.Step step)
    {
        if (OVRPlugin.GetHandState(step, (OVRPlugin.Hand)HandType, ref _handState))
        {
            IsTracked = (_handState.Status & OVRPlugin.HandStatus.HandTracked) != 0;
            IsSystemGestureInProgress = (_handState.Status & OVRPlugin.HandStatus.SystemGestureInProgress) != 0;
            IsPointerPoseValid = (_handState.Status & OVRPlugin.HandStatus.InputStateValid) != 0;
            IsDominantHand = (_handState.Status & OVRPlugin.HandStatus.DominantHand) != 0;
            PointerPose.localPosition = _handState.PointerPose.Position.FromFlippedZVector3f();
            PointerPose.localRotation = _handState.PointerPose.Orientation.FromFlippedZQuatf();
            HandScale = _handState.HandScale;
            HandConfidence = (TrackingConfidence)_handState.HandConfidence;

            IsDataValid = true;
            IsDataHighConfidence = IsTracked && HandConfidence == TrackingConfidence.High;
            _handTrackingStateValid =
                OVRPlugin.GetHandTrackingState(step, (OVRPlugin.Hand)HandType, ref _handTrackingState);

            // Hands cannot be doing pointer poses or system gestures when they are holding controllers
            //OVRInput.Hand inputHandType = (HandType == Hand.)
            OVRInput.ControllerInHandState controllerInHandState =
                OVRInput.GetControllerIsInHandState((OVRInput.Hand)HandType);
            if (controllerInHandState == OVRInput.ControllerInHandState.ControllerInHand)
            {
                // This hand is holding a controller
                IsSystemGestureInProgress = false;
                IsPointerPoseValid = false;
            }

            switch (m_showState)
            {
                case OVRInput.InputDeviceShowState.Always:
                    // intentionally blank
                    break;
                case OVRInput.InputDeviceShowState.ControllerInHandOrNoHand:
                    if (controllerInHandState == OVRInput.ControllerInHandState.ControllerNotInHand)
                    {
                        IsDataValid = false;
                    }
                    break;
                case OVRInput.InputDeviceShowState.ControllerInHand:
                    if (controllerInHandState != OVRInput.ControllerInHandState.ControllerInHand)
                    {
                        IsDataValid = false;
                    }
                    break;
                case OVRInput.InputDeviceShowState.ControllerNotInHand:
                    if (controllerInHandState != OVRInput.ControllerInHandState.ControllerNotInHand)
                    {
                        IsDataValid = false;
                    }
                    break;
                case OVRInput.InputDeviceShowState.NoHand:
                    if (controllerInHandState != OVRInput.ControllerInHandState.NoHand)
                    {
                        IsDataValid = false;
                    }
                    break;
            }

            if (OVRPlugin.HandSkeletonVersion != GlobalHandSkeletonVersion)
            {
                // The realtime hand data is not following the global skeleton version
                IsDataValid = false;
            }
        }
        else
        {
            IsTracked = false;
            IsSystemGestureInProgress = false;
            IsPointerPoseValid = false;
            PointerPose.localPosition = Vector3.zero;
            PointerPose.localRotation = Quaternion.identity;
            HandScale = 1.0f;
            HandConfidence = TrackingConfidence.Low;

            IsDataValid = false;
            IsDataHighConfidence = false;
            _handTrackingStateValid = false;
        }
    }

    /// <summary>
    /// Returns true if the given <see cref="HandFinger"/> is currently pinching.
    /// If the hand data overall is not valid, it will return false.
    /// This method only returns true or false, if you want more information on how much the user has pinched, try
    /// <see cref="GetFingerPinchStrength(HandFinger)"/>.
    /// </summary>
    /// <param name="finger">The finger you want to test, for example Middle, Index, Pinky, etc.</param>
    public bool GetFingerIsPinching(HandFinger finger)
    {
        return IsDataValid && (((int)_handState.Pinches & (1 << (int)finger)) != 0);
    }

    /// <summary>
    /// Returns the strength of the user's pinch. The value it returns ranges from 0 to 1, where 0 indicates no pinch
    /// and 1 is a full pinch with the finger touching the thumb.
    /// Based on the values returned, you can provide feedback to the user by changing the color of the fingertip,
    /// adding an audible pop when fingers have fully pinched, or integrate physics interactions based on the pinch status.
    /// </summary>
    /// <param name="finger">The finger you want to test, for example Middle, Index, Pinky, etc.</param>
    public float GetFingerPinchStrength(HandFinger finger)
    {
        if (IsDataValid
            && _handState.PinchStrength != null
            && _handState.PinchStrength.Length == (int)OVRPlugin.HandFinger.Max)
        {
            return _handState.PinchStrength[(int)finger];
        }

        return 0.0f;
    }

    /// <summary>
    /// Returns the confidence of a finger's pose as low or high, which indicates the amount of confidence that the
    /// tracking system has for the finger pose.
    /// </summary>
    /// <param name="finger">The finger you want to test, for example Middle, Index, Pinky, etc.</param>
    public TrackingConfidence GetFingerConfidence(HandFinger finger)
    {
        if (IsDataValid
            && _handState.FingerConfidences != null
            && _handState.FingerConfidences.Length == (int)OVRPlugin.HandFinger.Max)
        {
            return (TrackingConfidence)_handState.FingerConfidences[(int)finger];
        }

        return TrackingConfidence.Low;
    }

    OVRSkeleton.SkeletonType OVRSkeleton.IOVRSkeletonDataProvider.GetSkeletonType()
    {
        switch (HandType)
        {
            case Hand.HandLeft:
                return GlobalHandSkeletonVersion switch
                {
                    OVRHandSkeletonVersion.OVR => OVRSkeleton.SkeletonType.HandLeft,
                    OVRHandSkeletonVersion.OpenXR => OVRSkeleton.SkeletonType.XRHandLeft,
                    _ => OVRSkeleton.SkeletonType.None
                };
            case Hand.HandRight:
                return GlobalHandSkeletonVersion switch
                {
                    OVRHandSkeletonVersion.OVR => OVRSkeleton.SkeletonType.HandRight,
                    OVRHandSkeletonVersion.OpenXR => OVRSkeleton.SkeletonType.XRHandRight,
                    _ => OVRSkeleton.SkeletonType.None
                };
            case Hand.None:
            default:
                return OVRSkeleton.SkeletonType.None;
        }
    }

    OVRSkeleton.SkeletonPoseData OVRSkeleton.IOVRSkeletonDataProvider.GetSkeletonPoseData()
    {
        var data = new OVRSkeleton.SkeletonPoseData();
        data.IsDataValid = IsDataValid;
        if (IsDataValid)
        {
            data.RootPose = _handState.RootPose;
            data.RootScale = _handState.HandScale;
            data.BoneRotations = _handState.BoneRotations;
            data.BoneTranslations = _handState.BonePositions;
            data.IsDataHighConfidence = IsTracked && HandConfidence == TrackingConfidence.High;
        }

        return data;
    }

    /// <summary>
    /// Returns a <see cref="OVRSkeletonRenderer.SkeletonRendererData"/> associated with this hand's state.
    /// You can use the SkeletonRendererData to verify the validity/confidence/scale of the data you are receiving from the hand.
    /// </summary>
    OVRSkeletonRenderer.SkeletonRendererData OVRSkeletonRenderer.IOVRSkeletonRendererDataProvider.
        GetSkeletonRendererData()
    {
        var data = new OVRSkeletonRenderer.SkeletonRendererData();

        data.IsDataValid = IsDataValid;
        if (IsDataValid)
        {
            data.RootScale = _handState.HandScale;
            data.IsDataHighConfidence = IsTracked && HandConfidence == TrackingConfidence.High;
            data.ShouldUseSystemGestureMaterial = IsSystemGestureInProgress;
        }

        return data;
    }


    /// <summary>
    /// Retrieves the current microgesture type based on the hand tracking state.
    /// Returns the current type of microgesture being performed by the hand.
    /// If the hand tracking state is not valid, it returns MicrogestureType.Invalid.
    /// </summary>
    public MicrogestureType GetMicrogestureType()
    {
        OVRPlugin.SendMicrogestureHint();
        if (!_handTrackingStateValid)
        {
            return MicrogestureType.Invalid;
        }

        int microgestureValue = (int)_handTrackingState.Microgesture;
        return microgestureValue >= (int)MicrogestureType.NoGesture && microgestureValue <= (int)MicrogestureType.ThumbTap
            ? (MicrogestureType)microgestureValue
            : MicrogestureType.Invalid;
    }


    /// <summary>
    /// Returns the mesh type associated with this Hand's type.
    /// For example, if the hand type is "HandLeft", the mesh type returned would be "HandLeft".
    /// </summary>
    OVRMesh.MeshType OVRMesh.IOVRMeshDataProvider.GetMeshType()
    {
        switch (HandType)
        {
            case Hand.None:
                return OVRMesh.MeshType.None;
            case Hand.HandLeft:
                return GlobalHandSkeletonVersion switch
                {
                    OVRHandSkeletonVersion.OVR => OVRMesh.MeshType.HandLeft,
                    OVRHandSkeletonVersion.OpenXR => OVRMesh.MeshType.XRHandLeft,
                    _ => OVRMesh.MeshType.None
                };
            case Hand.HandRight:
                return GlobalHandSkeletonVersion switch
                {
                    OVRHandSkeletonVersion.OVR => OVRMesh.MeshType.HandRight,
                    OVRHandSkeletonVersion.OpenXR => OVRMesh.MeshType.XRHandRight,
                    _ => OVRMesh.MeshType.None
                };
            default:
                return OVRMesh.MeshType.None;
        }
    }

    /// <summary>
    /// Returns a <see cref="OVRMeshRenderer.MeshRendererData"/> associated with this hand's state. You can
    /// use MeshRendererData to verify the validity/confidence of the data you are receiving from the hand.
    /// </summary>
    OVRMeshRenderer.MeshRendererData OVRMeshRenderer.IOVRMeshRendererDataProvider.GetMeshRendererData()
    {
        var data = new OVRMeshRenderer.MeshRendererData();

        data.IsDataValid = IsDataValid;
        if (IsDataValid)
        {
            data.IsDataHighConfidence = IsTracked && HandConfidence == TrackingConfidence.High;
            data.ShouldUseSystemGestureMaterial = IsSystemGestureInProgress;
        }

        return data;
    }

    public void OnEnable()
    {
        OVRInputModule.TrackInputSource(this);
        SceneManager.activeSceneChanged += OnSceneChanged;
        if (RayHelper && ShouldShowHandUIRay())
        {
            RayHelper.gameObject.SetActive(true);
        }
    }

    public void OnDisable()
    {
        OVRInputModule.UntrackInputSource(this);
        SceneManager.activeSceneChanged -= OnSceneChanged;
        if (RayHelper)
        {
            RayHelper.gameObject.SetActive(false);
        }
    }

    // handle scene changes if we're marked Don't Destroy On Load.
    private void OnSceneChanged(Scene unloading, Scene loading)
    {
        OVRInputModule.TrackInputSource(this);
    }

    public void OnValidate()
    {
#if UNITY_EDITOR
        if (!Meta.XR.Editor.Callbacks.InitializeOnLoad.EditorReady)
        {
            return;
        }
#endif
        // Verify that all hand side based components on this object are using the same hand side.
        var skeleton = GetComponent<OVRSkeleton>();
        if (skeleton != null)
        {
            if (skeleton.GetSkeletonType() != HandType.AsSkeletonType(GlobalHandSkeletonVersion))
            {
                skeleton.SetSkeletonType(HandType.AsSkeletonType(GlobalHandSkeletonVersion));
            }
        }

        var mesh = GetComponent<OVRMesh>();
        if (mesh != null)
        {
            if (mesh.GetMeshType() != HandType.AsMeshType(GlobalHandSkeletonVersion))
            {
                mesh.SetMeshType(HandType.AsMeshType(GlobalHandSkeletonVersion));
            }
        }
    }

    /// <summary>
    /// When the index finger is pinching, this will return true as it's considered a "press".
    /// </summary>
    public bool IsPressed()
    {
        return GetFingerIsPinching(HandFinger.Index);
    }

    /// <summary>
    /// If the index finger was pinching (in other words, if <see cref="IsPressed"/> was true) and then it stops pinching, this will
    /// return true.
    /// </summary>
    public bool IsReleased()
    {
        return _wasReleased;
    }

    /// <summary>
    /// Returns the <see cref="PointerPose"/>. See <see cref="PointerPose"/> for more information.
    /// </summary>
    public Transform GetPointerRayTransform()
    {
        PointerPose.name = name;
        return PointerPose;
    }

    private bool ShouldShowHandUIRay()
    {
        return m_showState != OVRInput.InputDeviceShowState.ControllerInHand || OVRPlugin.AreControllerDrivenHandPosesNatural();
    }

    /// <summary>
    /// True when this object is not null. This is different from <see cref="IsDataValid"/>, which refers to the validity
    /// of the data itself.
    /// </summary>
    public bool IsValid()
    {
        return this != null;
    }

    /// <summary>
    /// Returns true if <see cref="IsDataValid"/> is true and if the hand pointer ray should be shown.
    /// </summary>
    /// <returns></returns>
    public bool IsActive()
    {
        return ShouldShowHandUIRay() && IsDataValid;
    }

    /// <summary>
    /// Returns the type of this Hand (left or right), see <see cref="Hand"/> for more info.
    /// </summary>
    /// <returns></returns>
    public OVRPlugin.Hand GetHand()
    {
        return (OVRPlugin.Hand)HandType;
    }

    /// <summary>
    /// If <see cref="RayHelper"/> is being used, this will update it with the strength of the current pinch (as explained
    /// in <see cref="GetFingerPinchStrength(HandFinger)"/>.
    /// </summary>
    /// <param name="rayData"></param>
    public void UpdatePointerRay(OVRInputRayData rayData)
    {
        if (RayHelper)
        {
            rayData.ActivationStrength = GetFingerPinchStrength(HandFinger.Index);
            RayHelper.UpdatePointerRay(rayData);
        }
    }
}
