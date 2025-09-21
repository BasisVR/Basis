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


#if USING_XR_MANAGEMENT && (USING_XR_SDK_OCULUS || USING_XR_SDK_OPENXR)
#define USING_XR_SDK
#endif

#if UNITY_2020_1_OR_NEWER
#define REQUIRES_XR_SDK
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
#define OVR_ANDROID_MRC
#endif

#if !UNITY_2018_3_OR_NEWER
#error Oculus Utilities require Unity 2018.3 or higher.
#endif

#if !USING_XR_MANAGEMENT
#warning XR Plug-in Management is not enabled. Your project would not launch in XR mode. Please install it through "Project Settings".
#endif

#if !(USING_XR_SDK_OCULUS || USING_XR_SDK_OPENXR)
#warning Either "Oculus XR Plugin" or "OpenXR Plugin" must be installed for the project to run properly on Oculus/Meta XR Devices. Please install one of them through "XR Plug-in Management" settings, or Package Manager.
#endif

#if UNITY_Y_FLIP_FIX_2021 || UNITY_Y_FLIP_FIX_2022 || UNITY_Y_FLIP_FIX_6
#define UNITY_Y_FLIP_FIX
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine.Rendering;

#if USING_XR_SDK
using UnityEngine.XR;
using UnityEngine.Experimental.XR;
#endif

#if USING_XR_SDK_OPENXR
using Meta.XR;
using UnityEngine.XR.OpenXR;
#endif

#if USING_XR_MANAGEMENT
using UnityEngine.XR.Management;
#endif

#if USING_URP
using UnityEngine.Rendering.Universal;
#endif

#if USING_XR_SDK_OCULUS
using Unity.XR.Oculus;
#endif

using Settings = UnityEngine.XR.XRSettings;
using Node = UnityEngine.XR.XRNode;

/// <summary>
/// OVRManager is the main interface to the Meta Quest system and is added to the [OVRCameraRig prefab](https://developer.oculus.com/documentation/unity/unity-add-camera-rig/).
/// It is a singleton that exposes the core Meta XR SDK functionality to Unity, and includes helper
/// functions that use the stored Meta variables to help configure the system behavior of Meta Quest.
/// If you are not using OVRCameraRig, you can also add OVRManager to your own game object. It should
/// only be added once.
/// For more information, see the Configure Settings section in [Add Camera Rig Using OVRCameraRig](https://developer.oculus.com/documentation/unity/unity-add-camera-rig/#configure-settings).
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-add-camera-rig/#configure-settings")]
public partial class OVRManager : MonoBehaviour, OVRMixedRealityCaptureConfiguration
{
    public enum XrApi
    {
        Unknown = OVRPlugin.XrApi.Unknown,
        CAPI = OVRPlugin.XrApi.CAPI,
        VRAPI = OVRPlugin.XrApi.VRAPI,
        OpenXR = OVRPlugin.XrApi.OpenXR,
    }

    public enum TrackingOrigin
    {
        EyeLevel = OVRPlugin.TrackingOrigin.EyeLevel,
        FloorLevel = OVRPlugin.TrackingOrigin.FloorLevel,
        Stage = OVRPlugin.TrackingOrigin.Stage,
        Stationary = OVRPlugin.TrackingOrigin.Stationary,
    }

    public enum EyeTextureFormat
    {
        Default = OVRPlugin.EyeTextureFormat.Default,
        R16G16B16A16_FP = OVRPlugin.EyeTextureFormat.R16G16B16A16_FP,
        R11G11B10_FP = OVRPlugin.EyeTextureFormat.R11G11B10_FP,
    }

    public enum FoveatedRenderingLevel
    {
        Off = OVRPlugin.FoveatedRenderingLevel.Off,
        Low = OVRPlugin.FoveatedRenderingLevel.Low,
        Medium = OVRPlugin.FoveatedRenderingLevel.Medium,
        High = OVRPlugin.FoveatedRenderingLevel.High,
        HighTop = OVRPlugin.FoveatedRenderingLevel.HighTop,
    }

    [Obsolete("Please use FoveatedRenderingLevel instead")]
    public enum FixedFoveatedRenderingLevel
    {
        Off = OVRPlugin.FixedFoveatedRenderingLevel.Off,
        Low = OVRPlugin.FixedFoveatedRenderingLevel.Low,
        Medium = OVRPlugin.FixedFoveatedRenderingLevel.Medium,
        High = OVRPlugin.FixedFoveatedRenderingLevel.High,
        HighTop = OVRPlugin.FixedFoveatedRenderingLevel.HighTop,
    }

    [Obsolete("Please use FoveatedRenderingLevel instead")]
    public enum TiledMultiResLevel
    {
        Off = OVRPlugin.TiledMultiResLevel.Off,
        LMSLow = OVRPlugin.TiledMultiResLevel.LMSLow,
        LMSMedium = OVRPlugin.TiledMultiResLevel.LMSMedium,
        LMSHigh = OVRPlugin.TiledMultiResLevel.LMSHigh,
        LMSHighTop = OVRPlugin.TiledMultiResLevel.LMSHighTop,
    }

    public enum SystemHeadsetType
    {
        None = OVRPlugin.SystemHeadset.None,

        // Standalone headsets
        Oculus_Quest = OVRPlugin.SystemHeadset.Oculus_Quest,
        Oculus_Quest_2 = OVRPlugin.SystemHeadset.Oculus_Quest_2,
        Meta_Quest_Pro = OVRPlugin.SystemHeadset.Meta_Quest_Pro,
        Meta_Quest_3 = OVRPlugin.SystemHeadset.Meta_Quest_3,
        Meta_Quest_3S = OVRPlugin.SystemHeadset.Meta_Quest_3S,
        Placeholder_13 = OVRPlugin.SystemHeadset.Placeholder_13,
        Placeholder_14 = OVRPlugin.SystemHeadset.Placeholder_14,
        Placeholder_15 = OVRPlugin.SystemHeadset.Placeholder_15,
        Placeholder_16 = OVRPlugin.SystemHeadset.Placeholder_16,
        Placeholder_17 = OVRPlugin.SystemHeadset.Placeholder_17,
        Placeholder_18 = OVRPlugin.SystemHeadset.Placeholder_18,
        Placeholder_19 = OVRPlugin.SystemHeadset.Placeholder_19,
        Placeholder_20 = OVRPlugin.SystemHeadset.Placeholder_20,

        // PC headsets
        Rift_DK1 = OVRPlugin.SystemHeadset.Rift_DK1,
        Rift_DK2 = OVRPlugin.SystemHeadset.Rift_DK2,
        Rift_CV1 = OVRPlugin.SystemHeadset.Rift_CV1,
        Rift_CB = OVRPlugin.SystemHeadset.Rift_CB,
        Rift_S = OVRPlugin.SystemHeadset.Rift_S,
        Oculus_Link_Quest = OVRPlugin.SystemHeadset.Oculus_Link_Quest,
        Oculus_Link_Quest_2 = OVRPlugin.SystemHeadset.Oculus_Link_Quest_2,
        Meta_Link_Quest_Pro = OVRPlugin.SystemHeadset.Meta_Link_Quest_Pro,
        Meta_Link_Quest_3 = OVRPlugin.SystemHeadset.Meta_Link_Quest_3,
        Meta_Link_Quest_3S = OVRPlugin.SystemHeadset.Meta_Link_Quest_3S,
        PC_Placeholder_4106 = OVRPlugin.SystemHeadset.PC_Placeholder_4106,
        PC_Placeholder_4107 = OVRPlugin.SystemHeadset.PC_Placeholder_4107,
        PC_Placeholder_4108 = OVRPlugin.SystemHeadset.PC_Placeholder_4108,
        PC_Placeholder_4109 = OVRPlugin.SystemHeadset.PC_Placeholder_4109,
        PC_Placeholder_4110 = OVRPlugin.SystemHeadset.PC_Placeholder_4110,
        PC_Placeholder_4111 = OVRPlugin.SystemHeadset.PC_Placeholder_4111,
        PC_Placeholder_4112 = OVRPlugin.SystemHeadset.PC_Placeholder_4112,
        PC_Placeholder_4113 = OVRPlugin.SystemHeadset.PC_Placeholder_4113,
    }

    public enum SystemHeadsetTheme
    {
        Dark,
        Light
    }

    public enum XRDevice
    {
        Unknown = 0,
        Oculus = 1,
        OpenVR = 2,
    }

    public enum ColorSpace
    {
        Unknown = OVRPlugin.ColorSpace.Unknown,
        Unmanaged = OVRPlugin.ColorSpace.Unmanaged,
        Rec_2020 = OVRPlugin.ColorSpace.Rec_2020,
        Rec_709 = OVRPlugin.ColorSpace.Rec_709,
        Rift_CV1 = OVRPlugin.ColorSpace.Rift_CV1,
        Rift_S = OVRPlugin.ColorSpace.Rift_S,

        [InspectorName("Quest 1")]
        Quest = OVRPlugin.ColorSpace.Quest,

        [InspectorName("DCI-P3 (Recommended)")]
        P3 = OVRPlugin.ColorSpace.P3,
        Adobe_RGB = OVRPlugin.ColorSpace.Adobe_RGB,
    }

    public enum ProcessorPerformanceLevel
    {
        PowerSavings = OVRPlugin.ProcessorPerformanceLevel.PowerSavings,
        SustainedLow = OVRPlugin.ProcessorPerformanceLevel.SustainedLow,
        SustainedHigh = OVRPlugin.ProcessorPerformanceLevel.SustainedHigh,
        Boost = OVRPlugin.ProcessorPerformanceLevel.Boost,
    }


    public enum ControllerDrivenHandPosesType
    {
        None,
        ConformingToController,
        Natural,
    }


    public interface EventListener
    {
        void OnEvent(OVRPlugin.EventDataBuffer eventData);
    }

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static OVRManager instance { get; private set; }

    /// <summary>
    /// Gets a reference to the active display.
    /// </summary>
    public static OVRDisplay display { get; private set; }

    /// <summary>
    /// Gets a reference to the active sensor.
    /// </summary>
    public static OVRTracker tracker { get; private set; }

    /// <summary>
    /// Gets a reference to the active boundary system.
    /// </summary>
    public static OVRBoundary boundary { get; private set; }

    /// <summary>
    /// Gets a reference to the runtime settings.
    /// </summary>
    public static OVRRuntimeSettings runtimeSettings { get; private set; }

    protected static OVRProfile _profile;

    /// <summary>
    /// Gets the current profile, which contains information about the user's settings and body dimensions.
    /// </summary>
    public static OVRProfile profile
    {
        get
        {
            if (_profile == null)
                _profile = new OVRProfile();

            return _profile;
        }
    }

    protected IEnumerable<Camera> disabledCameras;

    /// <summary>
    /// Occurs when an HMD attached.
    /// </summary>
    public static event Action HMDAcquired;

    /// <summary>
    /// Occurs when an HMD detached.
    /// </summary>
    public static event Action HMDLost;

    /// <summary>
    /// Occurs when an HMD is put on the user's head.
    /// </summary>
    public static event Action HMDMounted;

    /// <summary>
    /// Occurs when an HMD is taken off the user's head.
    /// </summary>
    public static event Action HMDUnmounted;

    /// <summary>
    /// Occurs when VR Focus is acquired.
    /// </summary>
    public static event Action VrFocusAcquired;

    /// <summary>
    /// Occurs when VR Focus is lost.
    /// </summary>
    public static event Action VrFocusLost;

    /// <summary>
    /// Occurs when Input Focus is acquired.
    /// </summary>
    public static event Action InputFocusAcquired;

    /// <summary>
    /// Occurs when Input Focus is lost.
    /// </summary>
    public static event Action InputFocusLost;

    /// <summary>
    /// Occurs when the active Audio Out device has changed and a restart is needed.
    /// </summary>
    public static event Action AudioOutChanged;

    /// <summary>
    /// Occurs when the active Audio In device has changed and a restart is needed.
    /// </summary>
    public static event Action AudioInChanged;

    /// <summary>
    /// Occurs when the sensor gained tracking.
    /// </summary>
    public static event Action TrackingAcquired;

    /// <summary>
    /// Occurs when the sensor lost tracking.
    /// </summary>
    public static event Action TrackingLost;

    /// <summary>
    /// Occurs when the display refresh rate changes
    /// @params (float fromRefreshRate, float toRefreshRate)
    /// </summary>
    public static event Action<float, float> DisplayRefreshRateChanged;

    /// <summary>
    /// Occurs when attempting to create a spatial anchor space
    /// @params (UInt64 requestId, bool result, OVRSpace space, Guid uuid)
    /// </summary>
    public static event Action<UInt64, bool, OVRSpace, Guid> SpatialAnchorCreateComplete;

    /// <summary>
    /// Occurs when attempting to enable a component on a space
    /// @params (UInt64 requestId, bool result, OVRSpace space, Guid uuid, OVRPlugin.SpaceComponentType componentType, bool enabled)
    /// </summary>
    public static event Action<UInt64, bool, OVRSpace, Guid, OVRPlugin.SpaceComponentType, bool>
        SpaceSetComponentStatusComplete;

    /// <summary>
    /// Occurs when one or more spaces are found during query
    /// @params (UInt64 requestId)
    /// </summary>
    public static event Action<UInt64> SpaceQueryResults;

    /// <summary>
    /// Occurs when querying for a space completes
    /// @params (UInt64 requestId, bool result)
    /// </summary>
    public static event Action<UInt64, bool> SpaceQueryComplete;

    /// <summary>
    /// Occurs when saving a space
    /// @params (UInt64 requestId, OVRSpace space, bool result, Guid uuid)
    /// </summary>
    public static event Action<UInt64, OVRSpace, bool, Guid> SpaceSaveComplete;

    /// <summary>
    /// Occurs when erasing a space
    /// @params (UInt64 requestId, bool result, Guid uuid, SpaceStorageLocation location)
    /// </summary>
    public static event Action<UInt64, bool, Guid, OVRPlugin.SpaceStorageLocation> SpaceEraseComplete;

    /// <summary>
    /// Occurs when sharing spatial entities
    /// @params (UInt64 requestId, OVRSpatialAnchor.OperationResult result)
    /// </summary>
    public static event Action<UInt64, OVRSpatialAnchor.OperationResult> ShareSpacesComplete;

    /// <summary>
    /// Occurs when saving space list
    /// @params (UInt64 requestId, OVRSpatialAnchor.OperationResult result)
    /// </summary>
    public static event Action<UInt64, OVRSpatialAnchor.OperationResult> SpaceListSaveComplete;

    /// <summary>
    /// Occurs when a scene capture request completes
    /// @params (UInt64 requestId, bool result)
    /// </summary>
    public static event Action<UInt64, bool> SceneCaptureComplete;




    /// <summary>
    /// Occurs when a passthrough layer has been rendered and presented on the HMD screen for the first time after being restarted.
    /// </summary>
    /// <remarks>
    /// @params (int layerId)
    /// </remarks>
    public static event Action<int> PassthroughLayerResumed;

    /// <summary>
    /// Occurs when the system's boundary visibility has been changed
    /// </summary>
    /// <remarks>
    /// @params (OVRPlugin.BoundaryVisibility newBoundaryVisibility)
    /// </remarks>
    public static event Action<OVRPlugin.BoundaryVisibility> BoundaryVisibilityChanged;


    /// <summary>
    /// Occurs when there is a change happening to a tracking origin, such as a recenter.
    /// @params (TrackingOrigin trackingOrigin, OVRPose? poseInPreviousSpace)
    /// </summary>
    /// <remarks>
    /// The new pose of the tracking origin is provided with respect to the previous space.
    /// This can be null when no previous space/tracking origin was defined.
    /// </remarks>
    public static event Action<TrackingOrigin, OVRPose?> TrackingOriginChangePending;

    /// <summary>
    /// Occurs when Health & Safety Warning is dismissed.
    /// </summary>
    //Disable the warning about it being unused. It's deprecated.
#pragma warning disable 0067
    [Obsolete]
    public static event Action HSWDismissed;
#pragma warning restore

    private static int _isHmdPresentCacheFrame = -1;
    private static bool _isHmdPresent = false;
    private static bool _wasHmdPresent = false;



    /// <summary>
    /// If true, a head-mounted display is connected and present.
    /// </summary>
    public static bool isHmdPresent
    {
        get
        {
            // Caching to ensure that IsHmdPresent() is called only once per frame
            if (_isHmdPresentCacheFrame != Time.frameCount)
            {
                _isHmdPresentCacheFrame = Time.frameCount;
                _isHmdPresent = OVRNodeStateProperties.IsHmdPresent();
            }
            return _isHmdPresent;
        }
    }

    /// <summary>
    /// Gets the audio output device identifier.
    /// </summary>
    /// <description>
    /// On Windows, this is a string containing the GUID of the IMMDevice for the Windows audio endpoint to use.
    /// </description>
    public static string audioOutId
    {
        get { return OVRPlugin.audioOutId; }
    }

    /// <summary>
    /// Gets the audio input device identifier.
    /// </summary>
    /// <description>
    /// On Windows, this is a string containing the GUID of the IMMDevice for the Windows audio endpoint to use.
    /// </description>
    public static string audioInId
    {
        get { return OVRPlugin.audioInId; }
    }

    private static bool _hasVrFocusCached = false;
    private static bool _hasVrFocus = false;
    private static bool _hadVrFocus = false;

    /// <summary>
    /// If true, the app has VR Focus.
    /// </summary>
    public static bool hasVrFocus
    {
        get
        {
            if (!_hasVrFocusCached)
            {
                _hasVrFocusCached = true;
                _hasVrFocus = OVRPlugin.hasVrFocus;
            }

            return _hasVrFocus;
        }

        private set
        {
            _hasVrFocusCached = true;
            _hasVrFocus = value;
        }
    }

    private static bool _hadInputFocus = true;

    /// <summary>
    /// If true, the app has Input Focus.
    /// </summary>
    public static bool hasInputFocus
    {
        get { return OVRPlugin.hasInputFocus; }
    }

    /// <summary>
    /// If true, chromatic de-aberration will be applied, improving the image at the cost of texture bandwidth.
    /// </summary>
    public bool chromatic
    {
        get
        {
            if (!isHmdPresent)
                return false;

            return OVRPlugin.chromatic;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.chromatic = value;
        }
    }

    [Header("Performance/Quality")]
    /// <summary>
    /// If true, both eyes will see the same image, rendered from the center eye pose, saving performance.
    /// </summary>
    [SerializeField]
    [Tooltip("If true, both eyes will see the same image, rendered from the center eye pose, saving performance.")]
    private bool _monoscopic = false;

    public bool monoscopic
    {
        get
        {
            if (!isHmdPresent)
                return _monoscopic;

            return OVRPlugin.monoscopic;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.monoscopic = value;
            _monoscopic = value;
        }
    }

    [SerializeField]
    [Tooltip("The sharpen filter of the eye buffer. This amplifies contrast and fine details.")]
    private OVRPlugin.LayerSharpenType _sharpenType = OVRPlugin.LayerSharpenType.None;

    /// <summary>
    /// The sharpen type for the eye buffer
    /// </summary>
    public OVRPlugin.LayerSharpenType sharpenType
    {
        get { return _sharpenType; }
        set
        {
            _sharpenType = value;
            OVRPlugin.SetEyeBufferSharpenType(_sharpenType);
        }
    }

    [HideInInspector]
    private OVRManager.ColorSpace _colorGamut = OVRManager.ColorSpace.P3;

    /// <summary>
    /// The target color gamut the HMD will perform a color space transformation to
    /// </summary>
    public OVRManager.ColorSpace colorGamut
    {
        get { return _colorGamut; }
        set
        {
            _colorGamut = value;
            OVRPlugin.SetClientColorDesc((OVRPlugin.ColorSpace)_colorGamut);
        }
    }

    /// <summary>
    /// The native color gamut of the target HMD
    /// </summary>
    public OVRManager.ColorSpace nativeColorGamut
    {
        get { return (OVRManager.ColorSpace)OVRPlugin.GetHmdColorDesc(); }
    }

    [SerializeField]
    [HideInInspector]
    [Tooltip("Enable Dynamic Resolution. This will allocate render buffers to maxDynamicResolutionScale size and " +
             "will change the viewport to adapt performance. Mobile only.")]
    private bool _enableDynamicResolution = false;
    public bool enableDynamicResolution
    {
        get { return _enableDynamicResolution; }
        set
        {
            _enableDynamicResolution = value;

#if USING_XR_SDK_OPENXR && UNITY_ANDROID
            OVRPlugin.SetExternalLayerDynresEnabled(value ? OVRPlugin.Bool.True : OVRPlugin.Bool.False);
#endif
        }
    }

    [HideInInspector]
    public float minDynamicResolutionScale = 1.0f;
    [HideInInspector]
    public float maxDynamicResolutionScale = 1.0f;

    [SerializeField]
    [HideInInspector]
    public float quest2MinDynamicResolutionScale = 0.7f;

    [SerializeField]
    [HideInInspector]
    public float quest2MaxDynamicResolutionScale = 1.3f;

    [SerializeField]
    [HideInInspector]
    public float quest3MinDynamicResolutionScale = 0.7f;

    [SerializeField]
    [HideInInspector]
    public float quest3MaxDynamicResolutionScale = 1.6f;

    private const int _pixelStepPerFrame = 32;

    /// <summary>
    /// Adaptive Resolution is based on Unity engine's renderViewportScale/eyeTextureResolutionScale feature
    /// But renderViewportScale was broken in an array of Unity engines, this function help to filter out those broken engines
    /// </summary>
    ///
    [System.Obsolete("Deprecated. Use Dynamic Render Scaling instead.", false)]
    public static bool IsAdaptiveResSupportedByEngine()
    {
        return true;
    }

    /// <summary>
    /// Min RenderScale the app can reach under adaptive resolution mode ( enableAdaptiveResolution = true );
    /// </summary>
    [RangeAttribute(0.5f, 2.0f)]
    [HideInInspector]
    [Tooltip("Min RenderScale the app can reach under adaptive resolution mode")]
    [System.Obsolete("Deprecated. Use minDynamicRenderScale instead.", false)]
    public float minRenderScale = 0.7f;

    /// <summary>
    /// Max RenderScale the app can reach under adaptive resolution mode ( enableAdaptiveResolution = true );
    /// </summary>
    [RangeAttribute(0.5f, 2.0f)]
    [HideInInspector]
    [Tooltip("Max RenderScale the app can reach under adaptive resolution mode")]
    [System.Obsolete("Deprecated. Use maxDynamicRenderScale instead.", false)]
    public float maxRenderScale = 1.0f;

    /// <summary>
    /// Set the relative offset rotation of head poses
    /// </summary>
    [SerializeField]
    [Tooltip("Set the relative offset rotation of head poses")]
    private Vector3 _headPoseRelativeOffsetRotation;

    public Vector3 headPoseRelativeOffsetRotation
    {
        get { return _headPoseRelativeOffsetRotation; }
        set
        {
            OVRPlugin.Quatf rotation;
            OVRPlugin.Vector3f translation;
            if (OVRPlugin.GetHeadPoseModifier(out rotation, out translation))
            {
                Quaternion finalRotation = Quaternion.Euler(value);
                rotation = finalRotation.ToQuatf();
                OVRPlugin.SetHeadPoseModifier(ref rotation, ref translation);
            }

            _headPoseRelativeOffsetRotation = value;
        }
    }

    /// <summary>
    /// Set the relative offset translation of head poses
    /// </summary>
    [SerializeField]
    [Tooltip("Set the relative offset translation of head poses")]
    private Vector3 _headPoseRelativeOffsetTranslation;

    public Vector3 headPoseRelativeOffsetTranslation
    {
        get { return _headPoseRelativeOffsetTranslation; }
        set
        {
            OVRPlugin.Quatf rotation;
            OVRPlugin.Vector3f translation;
            if (OVRPlugin.GetHeadPoseModifier(out rotation, out translation))
            {
                if (translation.FromFlippedZVector3f() != value)
                {
                    translation = value.ToFlippedZVector3f();
                    OVRPlugin.SetHeadPoseModifier(ref rotation, ref translation);
                }
            }

            _headPoseRelativeOffsetTranslation = value;
        }
    }

    /// <summary>
    /// The TCP listening port of Oculus Profiler Service, which will be activated in Debug/Developerment builds
    /// When the app is running on editor or device, open "Meta/Tools/(Deprecated) Oculus Profiler Panel" to view the realtime system metrics
    /// </summary>
    public int profilerTcpPort = OVRSystemPerfMetrics.TcpListeningPort;

    /// <summary>
    /// If premultipled alpha blending is used for the eye fov layer.
    /// Useful for changing how the eye fov layer blends with underlays.
    /// </summary>
    [HideInInspector]
    public static bool eyeFovPremultipliedAlphaModeEnabled
    {
        get { return OVRPlugin.eyeFovPremultipliedAlphaModeEnabled; }
        set { OVRPlugin.eyeFovPremultipliedAlphaModeEnabled = value; }
    }

#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
    /// <summary>
    /// If true, the MixedRealityCapture properties will be displayed
    /// </summary>
    [HideInInspector]
    public bool expandMixedRealityCapturePropertySheet = false;

    /// <summary>
    /// If true, Mixed Reality mode will be enabled
    /// </summary>
    [HideInInspector, Tooltip("If true, Mixed Reality mode will be enabled. It would be always set to false when " +
                              "the game is launching without editor")]
    public bool enableMixedReality = false;

    public enum CompositionMethod
    {
        External,

        [System.Obsolete("Deprecated. Direct composition is no longer supported", false)]
        Direct
    }

    /// <summary>
    /// Composition method
    /// </summary>
    [HideInInspector]
    public CompositionMethod compositionMethod = CompositionMethod.External;

    /// <summary>
    /// Extra hidden layers
    /// </summary>
    [HideInInspector, Tooltip("Extra hidden layers")]
    public LayerMask extraHiddenLayers;

    /// <summary>
    /// Extra visible layers
    /// </summary>
    [HideInInspector, Tooltip("Extra visible layers")]
    public LayerMask extraVisibleLayers;

    /// <summary>
    /// Whether MRC should dynamically update the culling mask using the Main Camera's culling mask, extraHiddenLayers, and extraVisibleLayers
    /// </summary>
    [HideInInspector, Tooltip("Dynamic Culling Mask")]
    public bool dynamicCullingMask = true;

    /// <summary>
    /// The backdrop color will be used when rendering the foreground frames (on Rift). It only applies to External Composition.
    /// </summary>
    [HideInInspector, Tooltip("Backdrop color for Rift (External Compositon)")]
    public Color externalCompositionBackdropColorRift = Color.green;

    /// <summary>
    /// The backdrop color will be used when rendering the foreground frames (on Quest). It only applies to External Composition.
    /// </summary>
    [HideInInspector, Tooltip("Backdrop color for Quest (External Compositon)")]
    public Color externalCompositionBackdropColorQuest = Color.clear;

    /// <summary>
    /// (Deprecated) If true, Mixed Reality mode will use direct composition from the first web camera
    /// </summary>
    [System.Obsolete("Deprecated", false)]
    public enum CameraDevice
    {
        WebCamera0,
        WebCamera1,
        ZEDCamera
    }

    /// <summary>
    /// (Deprecated) The camera device for direct composition
    /// </summary>
    [HideInInspector, Tooltip("The camera device for direct composition")]
    [System.Obsolete("Deprecated", false)]
    public CameraDevice capturingCameraDevice = CameraDevice.WebCamera0;

    /// <summary>
    /// (Deprecated) Flip the camera frame horizontally
    /// </summary>
    [HideInInspector, Tooltip("Flip the camera frame horizontally")]
    [System.Obsolete("Deprecated", false)]
    public bool flipCameraFrameHorizontally = false;

    /// <summary>
    /// (Deprecated) Flip the camera frame vertically
    /// </summary>
    [HideInInspector, Tooltip("Flip the camera frame vertically")]
    [System.Obsolete("Deprecated", false)]
    public bool flipCameraFrameVertically = false;

    /// <summary>
    /// (Deprecated) Delay the touch controller pose by a short duration (0 to 0.5 second)
    /// to match the physical camera latency
    /// </summary>
    [HideInInspector, Tooltip("Delay the touch controller pose by a short duration (0 to 0.5 second) " +
                              "to match the physical camera latency")]
    [System.Obsolete("Deprecated", false)]
    public float handPoseStateLatency = 0.0f;

    /// <summary>
    /// (Deprecated) Delay the foreground / background image in the sandwich composition to match the physical camera latency.
    /// The maximum duration is sandwichCompositionBufferedFrames / {Game FPS}
    /// </summary>
    [HideInInspector, Tooltip("Delay the foreground / background image in the sandwich composition to match " +
                              "the physical camera latency. The maximum duration is sandwichCompositionBufferedFrames / {Game FPS}")]
    [System.Obsolete("Deprecated", false)]
    public float sandwichCompositionRenderLatency = 0.0f;

    /// <summary>
    /// (Deprecated) The number of frames are buffered in the SandWich composition.
    /// The more buffered frames, the more memory it would consume.
    /// </summary>
    [HideInInspector, Tooltip("The number of frames are buffered in the SandWich composition. " +
                              "The more buffered frames, the more memory it would consume.")]
    [System.Obsolete("Deprecated", false)]
    public int sandwichCompositionBufferedFrames = 8;


    /// <summary>
    /// (Deprecated) Chroma Key Color
    /// </summary>
    [HideInInspector, Tooltip("Chroma Key Color")]
    [System.Obsolete("Deprecated", false)]
    public Color chromaKeyColor = Color.green;

    /// <summary>
    /// (Deprecated) Chroma Key Similarity
    /// </summary>
    [HideInInspector, Tooltip("Chroma Key Similarity")]
    [System.Obsolete("Deprecated", false)]
    public float chromaKeySimilarity = 0.60f;

    /// <summary>
    /// (Deprecated) Chroma Key Smooth Range
    /// </summary>
    [HideInInspector, Tooltip("Chroma Key Smooth Range")]
    [System.Obsolete("Deprecated", false)]
    public float chromaKeySmoothRange = 0.03f;

    /// <summary>
    /// (Deprecated) Chroma Key Spill Range
    /// </summary>
    [HideInInspector, Tooltip("Chroma Key Spill Range")]
    [System.Obsolete("Deprecated", false)]
    public float chromaKeySpillRange = 0.06f;

    /// <summary>
    /// (Deprecated) Use dynamic lighting (Depth sensor required)
    /// </summary>
    [HideInInspector, Tooltip("Use dynamic lighting (Depth sensor required)")]
    [System.Obsolete("Deprecated", false)]
    public bool useDynamicLighting = false;

    [System.Obsolete("Deprecated", false)]
    public enum DepthQuality
    {
        Low,
        Medium,
        High
    }

    /// <summary>
    /// (Deprecated) The quality level of depth image. The lighting could be more smooth and accurate
    /// with high quality depth, but it would also be more costly in performance.
    /// </summary>
    [HideInInspector, Tooltip("The quality level of depth image. The lighting could be more smooth and accurate " +
                              "with high quality depth, but it would also be more costly in performance.")]
    [System.Obsolete("Deprecated", false)]
    public DepthQuality depthQuality = DepthQuality.Medium;

    /// <summary>
    /// (Deprecated) Smooth factor in dynamic lighting. Larger is smoother
    /// </summary>
    [HideInInspector, Tooltip("Smooth factor in dynamic lighting. Larger is smoother")]
    [System.Obsolete("Deprecated", false)]
    public float dynamicLightingSmoothFactor = 8.0f;

    /// <summary>
    /// (Deprecated) The maximum depth variation across the edges.
    /// Make it smaller to smooth the lighting on the edges.
    /// </summary>
    [HideInInspector, Tooltip("The maximum depth variation across the edges. " +
                              "Make it smaller to smooth the lighting on the edges.")]
    [System.Obsolete("Deprecated", false)]
    public float dynamicLightingDepthVariationClampingValue = 0.001f;

    [System.Obsolete("Deprecated", false)]
    public enum VirtualGreenScreenType
    {
        Off,

        [System.Obsolete("Deprecated. This enum value will not be supported in OpenXR", false)]
        OuterBoundary,
        PlayArea
    }

    /// <summary>
    /// (Deprecated) Set the current type of the virtual green screen
    /// </summary>
    [HideInInspector, Tooltip("Type of virutal green screen ")]
    [System.Obsolete("Deprecated", false)]
    public VirtualGreenScreenType virtualGreenScreenType = VirtualGreenScreenType.Off;

    /// <summary>
    /// (Deprecated) Top Y of virtual screen
    /// </summary>
    [HideInInspector, Tooltip("Top Y of virtual green screen")]
    [System.Obsolete("Deprecated", false)]
    public float virtualGreenScreenTopY = 10.0f;

    /// <summary>
    /// (Deprecated) Bottom Y of virtual screen
    /// </summary>
    [HideInInspector, Tooltip("Bottom Y of virtual green screen")]
    [System.Obsolete("Deprecated", false)]
    public float virtualGreenScreenBottomY = -10.0f;

    /// <summary>
    /// (Deprecated) When using a depth camera (e.g. ZED), whether to use the depth in virtual green screen culling.
    /// </summary>
    [HideInInspector, Tooltip("When using a depth camera (e.g. ZED), " +
                              "whether to use the depth in virtual green screen culling.")]
    [System.Obsolete("Deprecated", false)]
    public bool virtualGreenScreenApplyDepthCulling = false;

    /// <summary>
    /// (Deprecated) The tolerance value (in meter) when using the virtual green screen with a depth camera.
    /// Make it bigger if the foreground objects got culled incorrectly.
    /// </summary>
    [HideInInspector, Tooltip("The tolerance value (in meter) when using the virtual green screen with " +
                              "a depth camera. Make it bigger if the foreground objects got culled incorrectly.")]
    [System.Obsolete("Deprecated", false)]
    public float virtualGreenScreenDepthTolerance = 0.2f;

    public enum MrcActivationMode
    {
        Automatic,
        Disabled
    }

    /// <summary>
    /// (Quest-only) control if the mixed reality capture mode can be activated automatically through remote network connection.
    /// </summary>
    [HideInInspector, Tooltip("(Quest-only) control if the mixed reality capture mode can be activated automatically " +
                              "through remote network connection.")]
    public MrcActivationMode mrcActivationMode;

    public enum MrcCameraType
    {
        Normal,
        Foreground,
        Background
    }

    public delegate GameObject InstantiateMrcCameraDelegate(GameObject mainCameraGameObject, MrcCameraType cameraType);

    /// <summary>
    /// Allows overriding the internal mrc camera creation
    /// </summary>
    public InstantiateMrcCameraDelegate instantiateMixedRealityCameraGameObject = null;

    // OVRMixedRealityCaptureConfiguration Interface implementation
    bool OVRMixedRealityCaptureConfiguration.enableMixedReality
    {
        get { return enableMixedReality; }
        set { enableMixedReality = value; }
    }

    LayerMask OVRMixedRealityCaptureConfiguration.extraHiddenLayers
    {
        get { return extraHiddenLayers; }
        set { extraHiddenLayers = value; }
    }

    LayerMask OVRMixedRealityCaptureConfiguration.extraVisibleLayers
    {
        get { return extraVisibleLayers; }
        set { extraVisibleLayers = value; }
    }

    bool OVRMixedRealityCaptureConfiguration.dynamicCullingMask
    {
        get { return dynamicCullingMask; }
        set { dynamicCullingMask = value; }
    }

    CompositionMethod OVRMixedRealityCaptureConfiguration.compositionMethod
    {
        get { return compositionMethod; }
        set { compositionMethod = value; }
    }

    Color OVRMixedRealityCaptureConfiguration.externalCompositionBackdropColorRift
    {
        get { return externalCompositionBackdropColorRift; }
        set { externalCompositionBackdropColorRift = value; }
    }

    Color OVRMixedRealityCaptureConfiguration.externalCompositionBackdropColorQuest
    {
        get { return externalCompositionBackdropColorQuest; }
        set { externalCompositionBackdropColorQuest = value; }
    }

    [Obsolete("Deprecated", false)]
    CameraDevice OVRMixedRealityCaptureConfiguration.capturingCameraDevice
    {
        get { return capturingCameraDevice; }
        set { capturingCameraDevice = value; }
    }

    bool OVRMixedRealityCaptureConfiguration.flipCameraFrameHorizontally
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return flipCameraFrameHorizontally; }
        set { flipCameraFrameHorizontally = value; }
#pragma warning restore CS0618
    }

    bool OVRMixedRealityCaptureConfiguration.flipCameraFrameVertically
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return flipCameraFrameVertically; }
        set { flipCameraFrameVertically = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.handPoseStateLatency
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return handPoseStateLatency; }
        set { handPoseStateLatency = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.sandwichCompositionRenderLatency
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return sandwichCompositionRenderLatency; }
        set { sandwichCompositionRenderLatency = value; }
#pragma warning restore CS0618
    }

    int OVRMixedRealityCaptureConfiguration.sandwichCompositionBufferedFrames
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return sandwichCompositionBufferedFrames; }
        set { sandwichCompositionBufferedFrames = value; }
#pragma warning restore CS0618
    }

    Color OVRMixedRealityCaptureConfiguration.chromaKeyColor
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return chromaKeyColor; }
        set { chromaKeyColor = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.chromaKeySimilarity
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return chromaKeySimilarity; }
        set { chromaKeySimilarity = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.chromaKeySmoothRange
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return chromaKeySmoothRange; }
        set { chromaKeySmoothRange = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.chromaKeySpillRange
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return chromaKeySpillRange; }
        set { chromaKeySpillRange = value; }
#pragma warning restore CS0618
    }

    bool OVRMixedRealityCaptureConfiguration.useDynamicLighting
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return useDynamicLighting; }
        set { useDynamicLighting = value; }
#pragma warning restore CS0618
    }

    [Obsolete("Deprecated", false)]
    DepthQuality OVRMixedRealityCaptureConfiguration.depthQuality
    {
        get { return depthQuality; }
        set { depthQuality = value; }
    }

    float OVRMixedRealityCaptureConfiguration.dynamicLightingSmoothFactor
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return dynamicLightingSmoothFactor; }
        set { dynamicLightingSmoothFactor = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.dynamicLightingDepthVariationClampingValue
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return dynamicLightingDepthVariationClampingValue; }
        set { dynamicLightingDepthVariationClampingValue = value; }
#pragma warning restore CS0618
    }

    [Obsolete("Deprecated", false)]
    VirtualGreenScreenType OVRMixedRealityCaptureConfiguration.virtualGreenScreenType
    {
        get { return virtualGreenScreenType; }
        set { virtualGreenScreenType = value; }
    }

    float OVRMixedRealityCaptureConfiguration.virtualGreenScreenTopY
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return virtualGreenScreenTopY; }
        set { virtualGreenScreenTopY = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.virtualGreenScreenBottomY
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return virtualGreenScreenBottomY; }
        set { virtualGreenScreenBottomY = value; }
#pragma warning restore CS0618
    }

    bool OVRMixedRealityCaptureConfiguration.virtualGreenScreenApplyDepthCulling
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return virtualGreenScreenApplyDepthCulling; }
        set { virtualGreenScreenApplyDepthCulling = value; }
#pragma warning restore CS0618
    }

    float OVRMixedRealityCaptureConfiguration.virtualGreenScreenDepthTolerance
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return virtualGreenScreenDepthTolerance; }
        set { virtualGreenScreenDepthTolerance = value; }
#pragma warning restore CS0618
    }

    MrcActivationMode OVRMixedRealityCaptureConfiguration.mrcActivationMode
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return mrcActivationMode; }
        set { mrcActivationMode = value; }
#pragma warning restore CS0618
    }

    InstantiateMrcCameraDelegate OVRMixedRealityCaptureConfiguration.instantiateMixedRealityCameraGameObject
    {
#pragma warning disable CS0618 // Field is deprecated, but property encapsulation is not
        get { return instantiateMixedRealityCameraGameObject; }
        set { instantiateMixedRealityCameraGameObject = value; }
#pragma warning restore CS0618
    }

#endif

    /// <summary>
    /// Specify if simultaneous hands and controllers should be enabled.
    /// </summary>
    [HideInInspector, Tooltip("Specify if simultaneous hands and controllers should be enabled. ")]
    public bool launchSimultaneousHandsControllersOnStartup = false;

    /// <summary>
    /// Specify if Insight Passthrough should be enabled.
    /// Passthrough layers can only be used if passthrough is enabled.
    /// </summary>
    [HideInInspector, Tooltip("Specify if Insight Passthrough should be enabled. " +
                              "Passthrough layers can only be used if passthrough is enabled.")]
    public bool isInsightPassthroughEnabled = false;

    /// <summary>
    /// The desired state for the Guardian boundary visibility. The system may
    /// ignore a request to suppress the boundary visibility if deemed necessary.
    /// </summary>
    /// <remarks>
    /// If Passthrough has been initialized, then an attempt will be made
    /// every frame to update the boundary state if different from the
    /// system state. It is important to therefore keep this variable aligned
    /// with the state of your Passthrough layers (e.g. set boundary suppression
    /// to false when disabling the <see cref="OVRPassthroughLayer"/>, and set boundary
    /// suppression to true only when the layer is active).
    /// </remarks>
    [HideInInspector] public bool shouldBoundaryVisibilityBeSuppressed = false;

    /// <summary>
    /// The system state of the Guardian boundary visibility.
    /// </summary>
    public bool isBoundaryVisibilitySuppressed { get; private set; } = false;

    // boundary logging helper to avoid spamming
    private bool _updateBoundaryLogOnce = false;



    #region Permissions

    /// <summary>`
    /// Specify if the app will request body tracking permission on startup.
    /// </summary>
    [SerializeField, HideInInspector]
    internal bool requestBodyTrackingPermissionOnStartup;

    /// <summary>
    /// Specify if the app will request face tracking permission on startup.
    /// </summary>
    [SerializeField, HideInInspector]
    internal bool requestFaceTrackingPermissionOnStartup;

    /// <summary>
    /// Specify if the app will request eye tracking permission on startup.
    /// </summary>
    [SerializeField, HideInInspector]
    internal bool requestEyeTrackingPermissionOnStartup;

    /// <summary>
    /// Specify if the app will request scene permission on startup.
    /// </summary>
    [SerializeField, HideInInspector]
    internal bool requestScenePermissionOnStartup;

    /// <summary>
    /// Specify if the app will request audio recording permission on startup.
    /// </summary>
    [SerializeField, HideInInspector]
    internal bool requestRecordAudioPermissionOnStartup;
    #endregion

    /// <summary>
    /// The native XR API being used
    /// </summary>
    public XrApi xrApi
    {
        get { return (XrApi)OVRPlugin.nativeXrApi; }
    }

    /// <summary>
    /// The value of current XrInstance when using OpenXR
    /// </summary>
    public UInt64 xrInstance
    {
        get { return OVRPlugin.GetNativeOpenXRInstance(); }
    }

    /// <summary>
    /// The value of current XrSession when using OpenXR
    /// </summary>
    public UInt64 xrSession
    {
        get { return OVRPlugin.GetNativeOpenXRSession(); }
    }

    /// <summary>
    /// The number of expected display frames per rendered frame.
    /// </summary>
    public int vsyncCount
    {
        get
        {
            if (!isHmdPresent)
                return 1;

            return OVRPlugin.vsyncCount;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.vsyncCount = value;
        }
    }

    public static string OCULUS_UNITY_NAME_STR = "Oculus";
    public static string OPENVR_UNITY_NAME_STR = "OpenVR";

    public static XRDevice loadedXRDevice;

    /// <summary>
    /// Gets the current battery level (Deprecated).
    /// </summary>
    /// <returns><c>battery level in the range [0.0,1.0]</c>
    /// <param name="batteryLevel">Battery level.</param>
    [System.Obsolete("Deprecated. Please use SystemInfo.batteryLevel", false)]
    public static float batteryLevel
    {
        get
        {
            if (!isHmdPresent)
                return 1f;

            return OVRPlugin.batteryLevel;
        }
    }

    /// <summary>
    /// Gets the current battery temperature (Deprecated).
    /// </summary>
    /// <returns><c>battery temperature in Celsius</c>
    /// <param name="batteryTemperature">Battery temperature.</param>
    [System.Obsolete("Deprecated. This function will not be supported in OpenXR", false)]
    public static float batteryTemperature
    {
        get
        {
            if (!isHmdPresent)
                return 0f;

            return OVRPlugin.batteryTemperature;
        }
    }

    /// <summary>
    /// Gets the current battery status (Deprecated).
    /// </summary>
    /// <returns><c>battery status</c>
    /// <param name="batteryStatus">Battery status.</param>
    [System.Obsolete("Deprecated. Please use SystemInfo.batteryStatus", false)]
    public static int batteryStatus
    {
        get
        {
            if (!isHmdPresent)
                return -1;

            return (int)OVRPlugin.batteryStatus;
        }
    }

    /// <summary>
    /// Gets the current volume level (Deprecated).
    /// </summary>
    /// <returns><c>volume level in the range [0,1].</c>
    [System.Obsolete("Deprecated. This function will not be supported in OpenXR", false)]
    public static float volumeLevel
    {
        get
        {
            if (!isHmdPresent)
                return 0f;

            return OVRPlugin.systemVolume;
        }
    }

    /// <summary>
    /// Gets or sets the current suggested CPU performance level, which can be overriden by the Power Management system.
    /// </summary>
    public static ProcessorPerformanceLevel suggestedCpuPerfLevel
    {
        get
        {
            if (!isHmdPresent)
                return ProcessorPerformanceLevel.PowerSavings;

            return (ProcessorPerformanceLevel)OVRPlugin.suggestedCpuPerfLevel;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.suggestedCpuPerfLevel = (OVRPlugin.ProcessorPerformanceLevel)value;
        }
    }

    /// <summary>
    /// Gets or sets the current suggested GPU performance level, which can be overriden by the Power Management system.
    /// </summary>
    public static ProcessorPerformanceLevel suggestedGpuPerfLevel
    {
        get
        {
            if (!isHmdPresent)
                return ProcessorPerformanceLevel.PowerSavings;

            return (ProcessorPerformanceLevel)OVRPlugin.suggestedGpuPerfLevel;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.suggestedGpuPerfLevel = (OVRPlugin.ProcessorPerformanceLevel)value;
        }
    }

    /// <summary>
    /// Gets or sets the current CPU performance level (0-2). Lower performance levels save more power. (Deprecated)
    /// </summary>
    [System.Obsolete("Deprecated. Please use suggestedCpuPerfLevel", false)]
    public static int cpuLevel
    {
        get
        {
            if (!isHmdPresent)
                return 2;

            return OVRPlugin.cpuLevel;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.cpuLevel = value;
        }
    }

    /// <summary>
    /// Gets or sets the current GPU performance level (0-2). Lower performance levels save more power. (Deprecated)
    /// </summary>
    [System.Obsolete("Deprecated. Please use suggestedGpuPerfLevel", false)]
    public static int gpuLevel
    {
        get
        {
            if (!isHmdPresent)
                return 2;

            return OVRPlugin.gpuLevel;
        }

        set
        {
            if (!isHmdPresent)
                return;

            OVRPlugin.gpuLevel = value;
        }
    }

    /// <summary>
    /// If true, the CPU and GPU are currently throttled to save power and/or reduce the temperature.
    /// </summary>
    public static bool isPowerSavingActive
    {
        get
        {
            if (!isHmdPresent)
                return false;

            return OVRPlugin.powerSaving;
        }
    }

    /// <summary>
    /// Gets or sets the eye texture format.
    /// </summary>
    public static EyeTextureFormat eyeTextureFormat
    {
        get { return (OVRManager.EyeTextureFormat)OVRPlugin.GetDesiredEyeTextureFormat(); }

        set { OVRPlugin.SetDesiredEyeTextureFormat((OVRPlugin.EyeTextureFormat)value); }
    }

    /// <summary>
    /// Gets if eye tracked foveated rendering feature is supported on this device
    /// </summary>
    public static bool eyeTrackedFoveatedRenderingSupported
    {
        get
        {
            return GetEyeTrackedFoveatedRenderingSupported();
        }
    }

    /// <summary>
    /// Gets or sets if eye tracked foveated rendering is enabled or not.
    /// </summary>
    public static bool eyeTrackedFoveatedRenderingEnabled
    {
        get
        {
            return GetEyeTrackedFoveatedRenderingEnabled();
        }
        set
        {
            if (eyeTrackedFoveatedRenderingSupported)
            {
                if (value)
                {
                    if (OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.EyeTracking))
                    {
                        SetEyeTrackedFoveatedRenderingEnabled(value);
                    }
#if OCULUS_XR_ETFR_DELAYED_PERMISSION_REQUEST
                    else
                    {
                        OVRPermissionsRequester.PermissionGranted += OnPermissionGranted;
                        OVRPermissionsRequester.Request(new List<OVRPermissionsRequester.Permission> { OVRPermissionsRequester.Permission.EyeTracking });
                    }
#endif
                }
                else
                {
                    SetEyeTrackedFoveatedRenderingEnabled(value);
                }
            }
        }
    }

    protected static void OnPermissionGranted(string permissionId)
    {
        if (permissionId == OVRPermissionsRequester.GetPermissionId(OVRPermissionsRequester.Permission.EyeTracking))
        {
            OVRPermissionsRequester.PermissionGranted -= OnPermissionGranted;
            SetEyeTrackedFoveatedRenderingEnabled(true);
        }
    }


    /// <summary>
    /// Gets or sets the tiled-based multi-resolution level
    /// This feature is only supported on QCOMM-based Android devices
    /// </summary>
    public static FoveatedRenderingLevel foveatedRenderingLevel
    {
        get
        {
            return GetFoveatedRenderingLevel();
        }
        set
        {
            SetFoveatedRenderingLevel(value);
        }
    }

    public static bool fixedFoveatedRenderingSupported
    {
        get { return GetFixedFoveatedRenderingSupported(); }
    }

    [Obsolete("Please use foveatedRenderingLevel instead", false)]
    public static FixedFoveatedRenderingLevel fixedFoveatedRenderingLevel
    {
        get { return (FixedFoveatedRenderingLevel)OVRPlugin.fixedFoveatedRenderingLevel; }
        set { OVRPlugin.fixedFoveatedRenderingLevel = (OVRPlugin.FixedFoveatedRenderingLevel)value; }
    }

    public static bool useDynamicFoveatedRendering
    {
        get
        {
            return GetDynamicFoveatedRenderingEnabled();
        }
        set
        {
            SetDynamicFoveatedRenderingEnabled(value);
        }
    }

    /// <summary>
    /// Let the system decide the best foveation level adaptively (Off .. fixedFoveatedRenderingLevel)
    /// This feature is only supported on QCOMM-based Android devices
    /// </summary>
    [Obsolete("Please use useDynamicFoveatedRendering instead", false)]
    public static bool useDynamicFixedFoveatedRendering
    {
        get { return OVRPlugin.useDynamicFixedFoveatedRendering; }
        set { OVRPlugin.useDynamicFixedFoveatedRendering = value; }
    }

    [Obsolete("Please use fixedFoveatedRenderingSupported instead", false)]
    public static bool tiledMultiResSupported
    {
        get { return OVRPlugin.tiledMultiResSupported; }
    }

    [Obsolete("Please use foveatedRenderingLevel instead", false)]
    public static TiledMultiResLevel tiledMultiResLevel
    {
        get { return (TiledMultiResLevel)OVRPlugin.tiledMultiResLevel; }
        set { OVRPlugin.tiledMultiResLevel = (OVRPlugin.TiledMultiResLevel)value; }
    }

    /// <summary>
    /// Gets if the GPU Utility is supported
    /// This feature is only supported on QCOMM-based Android devices
    /// </summary>
    public static bool gpuUtilSupported
    {
        get { return OVRPlugin.gpuUtilSupported; }
    }

    /// <summary>
    /// Gets the GPU Utilised Level (0.0 - 1.0)
    /// This feature is only supported on QCOMM-based Android devices
    /// </summary>
    public static float gpuUtilLevel
    {
        get
        {
            if (!OVRPlugin.gpuUtilSupported)
            {
                Debug.LogWarning("GPU Util is not supported");
            }

            return OVRPlugin.gpuUtilLevel;
        }
    }

    /// <summary>
    /// Get the system headset type
    /// </summary>
    public static SystemHeadsetType systemHeadsetType
    {
        get { return (SystemHeadsetType)OVRPlugin.GetSystemHeadsetType(); }
    }

    /// <summary>
    /// Get the system headset theme.
    /// This feature is only supported on Android-based devices.
    /// It will return dark (the default theme) on other devices.
    /// </summary>
    public static SystemHeadsetTheme systemHeadsetTheme
    {
        get { return GetSystemHeadsetTheme(); }
    }

    private static bool _isSystemHeadsetThemeCached = false;
    private static SystemHeadsetTheme _cachedSystemHeadsetTheme = SystemHeadsetTheme.Dark;

    static private SystemHeadsetTheme GetSystemHeadsetTheme()
    {
        if (!_isSystemHeadsetThemeCached)
        {
#if UNITY_ANDROID
            const int UI_MODE_NIGHT_MASK = 0x30;
            const int UI_MODE_NIGHT_NO = 0x10;
            AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            AndroidJavaObject currentResources = currentActivity.Call<AndroidJavaObject>("getResources");
            AndroidJavaObject currentConfiguration = currentResources.Call<AndroidJavaObject>("getConfiguration");
            int uiMode = currentConfiguration.Get<int>("uiMode");
            int currentUIMode = uiMode & UI_MODE_NIGHT_MASK;
            _cachedSystemHeadsetTheme = currentUIMode == UI_MODE_NIGHT_NO ? SystemHeadsetTheme.Light : SystemHeadsetTheme.Dark;
#endif // UNITY_ANDROID
            _isSystemHeadsetThemeCached = true;
        }
        return _cachedSystemHeadsetTheme;
    }

    /// <summary>
    /// Sets the Color Scale and Offset which is commonly used for effects like fade-to-black.
    /// In our compositor, once a given frame is rendered, warped, and ready to be displayed, we then multiply
    /// each pixel by colorScale and add it to colorOffset, whereby newPixel = oldPixel * colorScale + colorOffset.
    /// Note that for mobile devices (Quest, etc.), colorOffset is only supported with OpenXR, so colorScale is all that can
    /// be used. A colorScale of (1, 1, 1, 1) and colorOffset of (0, 0, 0, 0) will lead to an identity multiplication
    /// and have no effect.
    /// </summary>
    public static void SetColorScaleAndOffset(Vector4 colorScale, Vector4 colorOffset, bool applyToAllLayers)
    {
        SetColorScaleAndOffset_Internal(colorScale, colorOffset, applyToAllLayers);
    }

    /// <summary>
    /// Specifies OpenVR pose local to tracking space
    /// </summary>
    public static void SetOpenVRLocalPose(Vector3 leftPos, Vector3 rightPos, Quaternion leftRot, Quaternion rightRot)
    {
        if (loadedXRDevice == XRDevice.OpenVR)
            OVRInput.SetOpenVRLocalPose(leftPos, rightPos, leftRot, rightRot);
    }

    //Series of offsets that line up the virtual controllers to the phsyical world.
    protected static Vector3 OpenVRTouchRotationOffsetEulerLeft = new Vector3(40.0f, 0.0f, 0.0f);
    protected static Vector3 OpenVRTouchRotationOffsetEulerRight = new Vector3(40.0f, 0.0f, 0.0f);
    protected static Vector3 OpenVRTouchPositionOffsetLeft = new Vector3(0.0075f, -0.005f, -0.0525f);
    protected static Vector3 OpenVRTouchPositionOffsetRight = new Vector3(-0.0075f, -0.005f, -0.0525f);

    /// <summary>
    /// Specifies the pose offset required to make an OpenVR controller's reported pose match the virtual pose.
    /// Currently we only specify this offset for Oculus Touch on OpenVR.
    /// </summary>
    public static OVRPose GetOpenVRControllerOffset(Node hand)
    {
        OVRPose poseOffset = OVRPose.identity;
        if ((hand == Node.LeftHand || hand == Node.RightHand) && loadedXRDevice == XRDevice.OpenVR)
        {
            int index = (hand == Node.LeftHand) ? 0 : 1;
            if (OVRInput.openVRControllerDetails[index].controllerType == OVRInput.OpenVRController.OculusTouch)
            {
                Vector3 offsetOrientation = (hand == Node.LeftHand)
                    ? OpenVRTouchRotationOffsetEulerLeft
                    : OpenVRTouchRotationOffsetEulerRight;
                poseOffset.orientation =
                    Quaternion.Euler(offsetOrientation.x, offsetOrientation.y, offsetOrientation.z);
                poseOffset.position = (hand == Node.LeftHand)
                    ? OpenVRTouchPositionOffsetLeft
                    : OpenVRTouchPositionOffsetRight;
            }
        }

        return poseOffset;
    }

    /// <summary>
    /// Enables or disables space warp
    /// </summary>
    public static void SetSpaceWarp(bool enabled)
    {
        Camera mainCamera = FindMainCamera();
        if (enabled)
        {
            if (mainCamera != null)
            {
                PrepareCameraForSpaceWarp(mainCamera);
                m_lastSpaceWarpCamera = new WeakReference<Camera>(mainCamera);
            }
        }
        else
        {
            Camera lastSpaceWarpCamera;
            if (mainCamera != null && m_lastSpaceWarpCamera != null && m_lastSpaceWarpCamera.TryGetTarget(out lastSpaceWarpCamera) && lastSpaceWarpCamera == mainCamera)
            {
                // Restore the depth texture mode only if we're disabling space warp on the same camera we enabled it on.
                mainCamera.depthTextureMode = m_CachedDepthTextureMode;
            }

            m_AppSpaceTransform = null;
            m_lastSpaceWarpCamera = null;
        }

        SetSpaceWarp_Internal(enabled);
        m_SpaceWarpEnabled = enabled;
    }

    private static void PrepareCameraForSpaceWarp(Camera camera)
    {
        m_CachedDepthTextureMode = camera.depthTextureMode;
        camera.depthTextureMode |= (DepthTextureMode.MotionVectors | DepthTextureMode.Depth);
        m_AppSpaceTransform = camera.transform.parent;
    }

    protected static WeakReference<Camera> m_lastSpaceWarpCamera;
    protected static bool m_SpaceWarpEnabled;
    protected static Transform m_AppSpaceTransform;
    protected static DepthTextureMode m_CachedDepthTextureMode;

    public static bool GetSpaceWarp()
    {
        return m_SpaceWarpEnabled;
    }

#if OCULUS_XR_3_3_0_OR_NEWER
    public static bool SetDepthSubmission(bool enable)
    {
#if USING_XR_SDK_OCULUS
        OculusXRPlugin.SetDepthSubmission(enable);
        return true;
#else
        return false;
#endif
    }
#endif

    [SerializeField]
    [Tooltip("Available only for devices that support local dimming. It improves visual quality with " +
             "a better display contrast ratio, but at a minor GPU performance cost.")]
    private bool _localDimming = true;

    [Header("Tracking")]
    [SerializeField]
    [Tooltip("Defines the current tracking origin type.")]
    private OVRManager.TrackingOrigin _trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;

    /// <summary>
    /// Defines the current tracking origin type.
    /// </summary>
    public OVRManager.TrackingOrigin trackingOriginType
    {
        get
        {
            if (!isHmdPresent)
                return _trackingOriginType;

            return (OVRManager.TrackingOrigin)OVRPlugin.GetTrackingOriginType();
        }

        set
        {
            if (!isHmdPresent)
            {
                _trackingOriginType = value;
                return;
            }

            OVRPlugin.TrackingOrigin newOrigin = (OVRPlugin.TrackingOrigin)value;

#if USING_XR_SDK_OPENXR
            if (OVRPlugin.UnityOpenXR.Enabled)
            {
                if (GetCurrentInputSubsystem() == null)
                {
                    Debug.LogError("InputSubsystem not found");
                    return;
                }

                TrackingOriginModeFlags mode = TrackingOriginModeFlags.Unknown;
                if (newOrigin == OVRPlugin.TrackingOrigin.EyeLevel)
                {
                    mode = TrackingOriginModeFlags.Device;
                }
#if UNITY_OPENXR_1_9_0
                else if (newOrigin == OVRPlugin.TrackingOrigin.FloorLevel)
                {
                    // Unity OpenXR Plugin defines Floor as Floor with Recentering on
                    mode = TrackingOriginModeFlags.Floor;
                    OpenXRSettings.SetAllowRecentering(true);
                }
                else if (newOrigin == OVRPlugin.TrackingOrigin.Stage)
                {
                    // Unity OpenXR Plugin defines Stage as Floor with Recentering off
                    mode = TrackingOriginModeFlags.Floor;
                    OpenXRSettings.SetAllowRecentering(false);
                }
#else
                else if (newOrigin == OVRPlugin.TrackingOrigin.FloorLevel || newOrigin == OVRPlugin.TrackingOrigin.Stage)
                {
                    mode = TrackingOriginModeFlags.Floor; // Stage in OpenXR
                }
#endif

                // if the tracking origin mode is unsupported in OpenXR, we set the origin via OVRPlugin
                if (mode != TrackingOriginModeFlags.Unknown)
                {
                    bool success = GetCurrentInputSubsystem().TrySetTrackingOriginMode(mode);
                    if (!success)
                    {
                        Debug.LogError($"Unable to set TrackingOrigin {mode} to Unity Input Subsystem");
                    }
                    else
                    {
                        _trackingOriginType = value;
#if UNITY_OPENXR_PLUGIN_1_11_0_OR_NEWER
                        OpenXRSettings.RefreshRecenterSpace();
#endif
                    }
                    return;
                }
            }
#endif

            if (OVRPlugin.SetTrackingOriginType(newOrigin))
            {
                // Keep the field exposed in the Unity Editor synchronized with any changes.
                _trackingOriginType = value;
            }
        }
    }

    /// <summary>
    /// If true, head tracking will affect the position of each OVRCameraRig's cameras.
    /// </summary>
    [Tooltip("If true, head tracking will affect the position of each OVRCameraRig's cameras.")]
    public bool usePositionTracking = true;

    /// <summary>
    /// If true, head tracking will affect the rotation of each OVRCameraRig's cameras.
    /// </summary>
    [HideInInspector]
    public bool useRotationTracking = true;

    /// <summary>
    /// If true, the distance between the user's eyes will affect the position of each OVRCameraRig's cameras.
    /// </summary>
    [Tooltip("If true, the distance between the user's eyes will affect the position of each OVRCameraRig's cameras.")]
    public bool useIPDInPositionTracking = true;

    /// <summary>
    /// If true, each scene load will cause the head pose to reset. This function only works on Rift.
    /// </summary>
    [Tooltip("If true, each scene load will cause the head pose to reset. This function only works on Rift.")]
    public bool resetTrackerOnLoad = false;

    /// <summary>
    /// If true, the Reset View in the universal menu will cause the pose to be reset in PC VR. This should
    /// generally be enabled for applications with a stationary position in the virtual world and will allow
    /// the View Reset command to place the person back to a predefined location (such as a cockpit seat).
    /// Set this to false if you have a locomotion system because resetting the view would effectively teleport
    /// the player to potentially invalid locations.
    /// </summary>
    [Tooltip("If true, the Reset View in the universal menu will cause the pose to be reset in PC VR. This should " +
             "generally be enabled for applications with a stationary position in the virtual world and will allow " +
             "the View Reset command to place the person back to a predefined location (such as a cockpit seat). " +
             "Set this to false if you have a locomotion system because resetting the view would effectively teleport " +
             "the player to potentially invalid locations.")]
    public bool AllowRecenter = true;

    /// <summary>
    /// If true, a lower-latency update will occur right before rendering. If false, the only controller pose update
    /// will occur at the start of simulation for a given frame.
    /// Selecting this option lowers rendered latency for controllers and is often a net positive; however,
    /// it also creates a slight disconnect between rendered and simulated controller poses.
    /// Visit online Oculus documentation to learn more.
    /// </summary>
    [Tooltip("If true, rendered controller latency is reduced by several ms, as the left/right controllers will " +
             "have their positions updated right before rendering.")]
    public bool LateControllerUpdate = true;

#if UNITY_2020_3_OR_NEWER
    [Tooltip("Late latching is a feature that can reduce rendered head/controller latency by a substantial amount. " +
             "Before enabling, be sure to go over the documentation to ensure that the feature is used correctly. " +
             "This feature must also be enabled through the Oculus XR Plugin settings.")]
    public bool LateLatching = false;
#endif

    private static OVRManager.ControllerDrivenHandPosesType _readOnlyControllerDrivenHandPosesType = OVRManager.ControllerDrivenHandPosesType.None;
    [Tooltip("Defines if hand poses can be populated by controller data.")]
    public OVRManager.ControllerDrivenHandPosesType controllerDrivenHandPosesType = OVRManager.ControllerDrivenHandPosesType.None;

    [Tooltip("Allows the application to use simultaneous hands and controllers functionality. This option must be enabled at build time.")]
    public bool SimultaneousHandsAndControllersEnabled = false;

    [SerializeField]
    [HideInInspector]
    private bool _readOnlyWideMotionModeHandPosesEnabled = false;
    [Tooltip("Defines if hand poses can leverage algorithms to retrieve hand poses outside of the normal tracking area.")]
    public bool wideMotionModeHandPosesEnabled = false;

    public bool IsSimultaneousHandsAndControllersSupported
    {
        get => (_readOnlyControllerDrivenHandPosesType != OVRManager.ControllerDrivenHandPosesType.None) || launchSimultaneousHandsControllersOnStartup;
    }

    /// <summary>
    /// True if the current platform supports virtual reality.
    /// </summary>
    public bool isSupportedPlatform { get; private set; }

    private static bool _isUserPresentCached = false;
    private static bool _isUserPresent = false;
    private static bool _wasUserPresent = false;

    /// <summary>
    /// True if the user is currently wearing the display.
    /// </summary>
    public bool isUserPresent
    {
        get
        {
            if (!_isUserPresentCached)
            {
                _isUserPresentCached = true;
                _isUserPresent = OVRPlugin.userPresent;
            }

            return _isUserPresent;
        }

        private set
        {
            _isUserPresentCached = true;
            _isUserPresent = value;
        }
    }

    private static bool prevAudioOutIdIsCached = false;
    private static bool prevAudioInIdIsCached = false;
    private static string prevAudioOutId = string.Empty;
    private static string prevAudioInId = string.Empty;
    private static bool wasPositionTracked = false;

    private static OVRPlugin.EventDataBuffer eventDataBuffer = new OVRPlugin.EventDataBuffer();

    private HashSet<EventListener> eventListeners = new HashSet<EventListener>();

    public void RegisterEventListener(EventListener listener)
    {
        eventListeners.Add(listener);
    }

    public void DeregisterEventListener(EventListener listener)
    {
        eventListeners.Remove(listener);
    }

    public static System.Version utilitiesVersion
    {
        get { return OVRPlugin.wrapperVersion; }
    }

    public static System.Version pluginVersion
    {
        get { return OVRPlugin.version; }
    }

    public static System.Version sdkVersion
    {
        get { return OVRPlugin.nativeSDKVersion; }
    }

#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
    private static bool MixedRealityEnabledFromCmd()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].ToLower() == "-mixedreality")
                return true;
        }

        return false;
    }

    private static bool UseDirectCompositionFromCmd()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].ToLower() == "-directcomposition")
                return true;
        }

        return false;
    }

    private static bool UseExternalCompositionFromCmd()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].ToLower() == "-externalcomposition")
                return true;
        }

        return false;
    }

    private static bool CreateMixedRealityCaptureConfigurationFileFromCmd()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].ToLower() == "-create_mrc_config")
                return true;
        }

        return false;
    }

    private static bool LoadMixedRealityCaptureConfigurationFileFromCmd()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].ToLower() == "-load_mrc_config")
                return true;
        }

        return false;
    }
#endif

    public static bool IsUnityAlphaOrBetaVersion()
    {
        string ver = Application.unityVersion;
        int pos = ver.Length - 1;

        while (pos >= 0 && ver[pos] >= '0' && ver[pos] <= '9')
        {
            --pos;
        }

        if (pos >= 0 && (ver[pos] == 'a' || ver[pos] == 'b'))
            return true;

        return false;
    }

    public static string UnityAlphaOrBetaVersionWarningMessage =
        "WARNING: It's not recommended to use Unity alpha/beta release in Oculus development. Use a stable release if you encounter any issue.";

    #region Unity Messages

#if UNITY_EDITOR
    [AOT.MonoPInvokeCallback(typeof(OVRPlugin.LogCallback2DelegateType))]
    static void OVRPluginLogCallback(OVRPlugin.LogLevel logLevel, IntPtr message, int size)
    {
        string logString = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message, size);
        if (logLevel <= OVRPlugin.LogLevel.Info)
        {
            UnityEngine.Debug.Log("[OVRPlugin] " + logString);
        }
        else
        {
            UnityEngine.Debug.LogWarning("[OVRPlugin] " + logString);
        }
    }
#endif

    public static int MaxDynamicResolutionVersion = 1;
    [SerializeField]
    [HideInInspector]
    public int dynamicResolutionVersion = 0;

    private void Reset()
    {
        dynamicResolutionVersion = MaxDynamicResolutionVersion;
    }

    public static bool OVRManagerinitialized = false;

    private void InitOVRManager()
    {
        using var marker = new OVRTelemetryMarker(OVRTelemetryConstants.OVRManager.MarkerId.Init);
        marker.AddSDKVersionAnnotation();

        // Only allow one instance at runtime.
        if (instance != null)
        {
            enabled = false;
            DestroyImmediate(this);

            marker.SetResult(OVRPlugin.Qpl.ResultType.Fail);
            return;
        }

        instance = this;

        runtimeSettings = OVRRuntimeSettings.GetRuntimeSettings();

        // uncomment the following line to disable the callstack printed to log
        //Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);  // TEMPORARY

        string versionMessage = "Unity v" + Application.unityVersion + ", " +
                  "Oculus Utilities v" + OVRPlugin.wrapperVersion + ", " +
                  "OVRPlugin v" + OVRPlugin.version + ", " +
                  "SDK v" + OVRPlugin.nativeSDKVersion + ".";

        if (OVRPlugin.version < OVRPlugin.wrapperVersion)
        {
            Debug.LogWarning(versionMessage);
            Debug.LogWarning("You are using an old version of OVRPlugin. Some features may not work correctly. " +
                             "You will be prompted to restart the Editor for any OVRPlugin changes.");
        }
        else
        {
            Debug.Log(versionMessage);
        }

        Debug.LogFormat("SystemHeadset {0}, API {1}", systemHeadsetType.ToString(), xrApi.ToString());

        if (xrApi == XrApi.OpenXR)
        {
            Debug.LogFormat("OpenXR instance 0x{0:X} session 0x{1:X}", xrInstance, xrSession);
        }

#if !UNITY_EDITOR
        if (IsUnityAlphaOrBetaVersion())
        {
            Debug.LogWarning(UnityAlphaOrBetaVersionWarningMessage);
        }
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        var supportedTypes =
            UnityEngine.Rendering.GraphicsDeviceType.Direct3D11.ToString() + ", " +
            UnityEngine.Rendering.GraphicsDeviceType.Direct3D12.ToString();

        if (!supportedTypes.Contains(SystemInfo.graphicsDeviceType.ToString()))
            Debug.LogWarning("VR rendering requires one of the following device types: (" + supportedTypes +
                             "). Your graphics device: " + SystemInfo.graphicsDeviceType.ToString());
#endif

        // Detect whether this platform is a supported platform
        RuntimePlatform currPlatform = Application.platform;
        if (currPlatform == RuntimePlatform.Android ||
            // currPlatform == RuntimePlatform.LinuxPlayer ||
            currPlatform == RuntimePlatform.OSXEditor ||
            currPlatform == RuntimePlatform.OSXPlayer ||
            currPlatform == RuntimePlatform.WindowsEditor ||
            currPlatform == RuntimePlatform.WindowsPlayer)
        {
            isSupportedPlatform = true;
        }
        else
        {
            isSupportedPlatform = false;
        }

        if (!isSupportedPlatform)
        {
            Debug.LogWarning("This platform is unsupported");
            marker.SetResult(OVRPlugin.Qpl.ResultType.Fail);
            return;
        }

#if UNITY_EDITOR
        OVRPlugin.SetLogCallback2(OVRPluginLogCallback);
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        // Turn off chromatic aberration by default to save texture bandwidth.
        chromatic = false;
#endif

#if (UNITY_STANDALONE_WIN || UNITY_ANDROID) && !UNITY_EDITOR
        // we should never start the standalone game in MxR mode, unless the command-line parameter is provided
        enableMixedReality = false;
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        if (!staticMixedRealityCaptureInitialized)
        {
            bool loadMrcConfig = LoadMixedRealityCaptureConfigurationFileFromCmd();
            bool createMrcConfig = CreateMixedRealityCaptureConfigurationFileFromCmd();

            if (loadMrcConfig || createMrcConfig)
            {
                OVRMixedRealityCaptureSettings mrcSettings =
                    ScriptableObject.CreateInstance<OVRMixedRealityCaptureSettings>();
                mrcSettings.ReadFrom(this);
                if (loadMrcConfig)
                {
                    mrcSettings.CombineWithConfigurationFile();
                    mrcSettings.ApplyTo(this);
                }

                if (createMrcConfig)
                {
                    mrcSettings.WriteToConfigurationFile();
                }

                ScriptableObject.Destroy(mrcSettings);
            }

            if (MixedRealityEnabledFromCmd())
            {
                enableMixedReality = true;
            }

            if (enableMixedReality)
            {
                Debug.Log("OVR: Mixed Reality mode enabled");
                if (UseDirectCompositionFromCmd())
                {
                    Debug.Log("DirectionComposition deprecated. Fallback to ExternalComposition");
                    compositionMethod = CompositionMethod.External; // CompositionMethod.Direct;
                }

                if (UseExternalCompositionFromCmd())
                {
                    compositionMethod = CompositionMethod.External;
                }

                Debug.Log("OVR: CompositionMethod : " + compositionMethod);
            }
        }
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || OVR_ANDROID_MRC
        StaticInitializeMixedRealityCapture(this);
#endif

        Initialize();
        InitPermissionRequest();

        marker.AddPoint(OVRTelemetryConstants.OVRManager.InitPermissionRequest);

        Debug.LogFormat("Current display frequency {0}, available frequencies [{1}]",
            display.displayFrequency,
            string.Join(", ", display.displayFrequenciesAvailable.Select(f => f.ToString()).ToArray()));

        if (resetTrackerOnLoad)
            display.RecenterPose();

        if (Debug.isDebugBuild)
        {
            // Activate system metrics collection in Debug/Developerment build
            if (GetComponent<OVRSystemPerfMetrics.OVRSystemPerfMetricsTcpServer>() == null)
            {
                gameObject.AddComponent<OVRSystemPerfMetrics.OVRSystemPerfMetricsTcpServer>();
            }

            OVRSystemPerfMetrics.OVRSystemPerfMetricsTcpServer perfTcpServer =
                GetComponent<OVRSystemPerfMetrics.OVRSystemPerfMetricsTcpServer>();
            perfTcpServer.listeningPort = profilerTcpPort;
            if (!perfTcpServer.enabled)
            {
                perfTcpServer.enabled = true;
            }
#if !UNITY_EDITOR
            OVRPlugin.SetDeveloperMode(OVRPlugin.Bool.True);
#endif
        }

        // Refresh the client color space
        OVRManager.ColorSpace clientColorSpace = runtimeSettings.colorSpace;
        colorGamut = clientColorSpace;

        // Set the eyebuffer sharpen type at the start
        OVRPlugin.SetEyeBufferSharpenType(_sharpenType);

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        // Force OcculusionMesh on all the time, you can change the value to false if you really need it be off for some reasons,
        // be aware there are performance drops if you don't use occlusionMesh.
        OVRPlugin.occlusionMesh = true;
#endif

        // Inform the plugin of multimodal mode
        if (!OVRPlugin.SetSimultaneousHandsAndControllersEnabled(launchSimultaneousHandsControllersOnStartup))
        {
            Debug.Log("Failed to set multimodal hands and controllers mode!");
        }

        if (isInsightPassthroughEnabled)
        {
            InitializeInsightPassthrough();

            marker.AddPoint(OVRTelemetryConstants.OVRManager.InitializeInsightPassthrough);
        }

        // Apply validation criteria to _localDimming toggle to ensure it isn't active on invalid systems
        if (!OVRPlugin.localDimmingSupported)
        {
            Debug.LogWarning("Local Dimming feature is not supported");
            _localDimming = false;
        }
        else
        {
            OVRPlugin.localDimming = _localDimming;
        }

        UpdateDynamicResolutionVersion();

        switch (systemHeadsetType)
        {
            case SystemHeadsetType.Oculus_Quest_2:
            case SystemHeadsetType.Meta_Quest_Pro:
                minDynamicResolutionScale = quest2MinDynamicResolutionScale;
                maxDynamicResolutionScale = quest2MaxDynamicResolutionScale;
                break;
            default:
                minDynamicResolutionScale = quest3MinDynamicResolutionScale;
                maxDynamicResolutionScale = quest3MaxDynamicResolutionScale;
                break;
        }

#if USING_XR_SDK && UNITY_ANDROID
// Dynamic resolution in the Unity OpenXR plugin is only supported on package versions 3.4.1 on Unity 2021 and 4.3.1 on Unity 2022 and up.
#if (USING_XR_SDK_OCULUS || (USING_XR_SDK_OPENXR && UNITY_Y_FLIP_FIX))
        if (enableDynamicResolution)
        {
#if USING_XR_SDK_OPENXR
            OVRPlugin.SetExternalLayerDynresEnabled(enableDynamicResolution ? OVRPlugin.Bool.True : OVRPlugin.Bool.False);
#endif

            XRSettings.eyeTextureResolutionScale = maxDynamicResolutionScale;
#if USING_URP
            if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urpPipelineAsset)
                urpPipelineAsset.renderScale = maxDynamicResolutionScale;
#endif
        }
#endif
#endif

        InitializeBoundary();

        if (OVRPlugin.HandSkeletonVersion != runtimeSettings.HandSkeletonVersion)
        {
            OVRPlugin.SetHandSkeletonVersion(runtimeSettings.HandSkeletonVersion);
        }
        Debug.Log($"[OVRManager] Current hand skeleton version is {OVRPlugin.HandSkeletonVersion}");

#if UNITY_OPENXR_PLUGIN_1_11_0_OR_NEWER
        var openXrSettings = OpenXRSettings.Instance;
        if (openXrSettings != null)
        {
            var subsampledFeature = openXrSettings.GetFeature<MetaXRSubsampledLayout>();
            var spaceWarpFeature = openXrSettings.GetFeature<MetaXRSpaceWarp>();

            bool subsampledOn = false;
            if (subsampledFeature != null)
                subsampledOn = subsampledFeature.enabled;

            bool spaceWarpOn = false;
            if (spaceWarpFeature != null)
                spaceWarpOn = spaceWarpFeature.enabled;

            Debug.Log(string.Format("OpenXR Meta Quest Runtime Settings:\nDepth Submission Mode - {0}\nRendering Mode - {1}\nOptimize Buffer Discards - {2}\nSymmetric Projection - {3}\nSubsampled Layout - {4}\nSpace Warp - {5}",
                    openXrSettings.depthSubmissionMode, openXrSettings.renderMode, openXrSettings.optimizeBufferDiscards, openXrSettings.symmetricProjection, subsampledOn, spaceWarpOn));
        }
#endif
#if OCULUS_XR_PLUGIN_4_3_0_OR_NEWER
        var oculusLoader = XRGeneralSettings.Instance.Manager.activeLoader as OculusLoader;
        if (oculusLoader != null)
        {
            var oculusSettings = oculusLoader.GetSettings();
            Debug.Log(string.Format("Oculus XR Runtime Settings:\nDepth Submission - {0}\nFoveated Rendering Method - {1}\nOptimize Buffer Discards - {2}\nSymmetric Projection - {3}\nSubsampled Layout - {4}\nSpace Warp - {5}\nLate Latching - {6}\nLow Overhead Mode - {7}",
                    oculusSettings.DepthSubmission, oculusSettings.FoveatedRenderingMethod, oculusSettings.OptimizeBufferDiscards, oculusSettings.SymmetricProjection, oculusSettings.SubsampledLayout, oculusSettings.SpaceWarp, oculusSettings.LateLatching, oculusSettings.LowOverheadMode));
        }
#endif

        OVRManagerinitialized = true;
    }

    private void InitPermissionRequest()
    {
        var permissions = new HashSet<OVRPermissionsRequester.Permission>();

        if (requestBodyTrackingPermissionOnStartup)
        {
            permissions.Add(OVRPermissionsRequester.Permission.BodyTracking);
        }

        if (requestFaceTrackingPermissionOnStartup)
        {
            permissions.Add(OVRPermissionsRequester.Permission.FaceTracking);
        }

        if (requestEyeTrackingPermissionOnStartup)
        {
            permissions.Add(OVRPermissionsRequester.Permission.EyeTracking);
        }

        if (requestScenePermissionOnStartup)
        {
            permissions.Add(OVRPermissionsRequester.Permission.Scene);
        }

        if (requestRecordAudioPermissionOnStartup)
        {
            permissions.Add(OVRPermissionsRequester.Permission.RecordAudio);
        }

        OVRPermissionsRequester.Request(permissions);
    }

    private void Awake()
    {
#if !USING_XR_SDK
        //For legacy, we should initialize OVRManager in all cases.
        //For now, in XR SDK, only initialize if OVRPlugin is initialized.
        InitOVRManager();
#else
        if (OVRPlugin.initialized)
            InitOVRManager();
#endif
    }

#if UNITY_EDITOR
    private static bool _scriptsReloaded;

    [UnityEditor.Callbacks.DidReloadScripts]
    static void ScriptsReloaded()
    {
        _scriptsReloaded = true;
    }
#endif

    void SetCurrentXRDevice()
    {
#if USING_XR_SDK
        XRDisplaySubsystem currentDisplaySubsystem = GetCurrentDisplaySubsystem();
        XRDisplaySubsystemDescriptor currentDisplaySubsystemDescriptor = GetCurrentDisplaySubsystemDescriptor();
#endif
        if (OVRPlugin.initialized)
        {
            loadedXRDevice = XRDevice.Oculus;
        }
#if USING_XR_SDK
        else if (currentDisplaySubsystem != null && currentDisplaySubsystemDescriptor != null &&
                 currentDisplaySubsystem.running)
#else
        else if (Settings.enabled)
#endif
        {
#if USING_XR_SDK
            string loadedXRDeviceName = currentDisplaySubsystemDescriptor.id;
#else
            string loadedXRDeviceName = Settings.loadedDeviceName;
#endif
            if (loadedXRDeviceName == OPENVR_UNITY_NAME_STR)
                loadedXRDevice = XRDevice.OpenVR;
            else
                loadedXRDevice = XRDevice.Unknown;
        }
        else
        {
            loadedXRDevice = XRDevice.Unknown;
        }
    }

#if USING_XR_SDK
    static List<XRDisplaySubsystem> s_displaySubsystems;

    public static XRDisplaySubsystem GetCurrentDisplaySubsystem()
    {
        if (s_displaySubsystems == null)
            s_displaySubsystems = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(s_displaySubsystems);
        if (s_displaySubsystems.Count > 0)
            return s_displaySubsystems[0];
        return null;
    }

    static List<XRDisplaySubsystemDescriptor> s_displaySubsystemDescriptors;

    public static XRDisplaySubsystemDescriptor GetCurrentDisplaySubsystemDescriptor()
    {
        if (s_displaySubsystemDescriptors == null)
            s_displaySubsystemDescriptors = new List<XRDisplaySubsystemDescriptor>();
        SubsystemManager.GetSubsystemDescriptors(s_displaySubsystemDescriptors);
        if (s_displaySubsystemDescriptors.Count > 0)
            return s_displaySubsystemDescriptors[0];
        return null;
    }

    static List<XRInputSubsystem> s_inputSubsystems;
    public static XRInputSubsystem GetCurrentInputSubsystem()
    {
        if (s_inputSubsystems == null)
            s_inputSubsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(s_inputSubsystems);
        if (s_inputSubsystems.Count > 0)
            return s_inputSubsystems[0];
        return null;
    }
#endif

    void Initialize()
    {
        if (display == null)
            display = new OVRDisplay();
        if (tracker == null)
            tracker = new OVRTracker();
        if (boundary == null)
            boundary = new OVRBoundary();

        SetCurrentXRDevice();
    }

    private void Update()
    {
        //Only if we're using the XR SDK do we have to check if OVRManager isn't yet initialized, and init it.
        //If we're on legacy, we know initialization occurred properly in Awake()
#if USING_XR_SDK
        if (!OVRManagerinitialized)
        {
            XRDisplaySubsystem currentDisplaySubsystem = GetCurrentDisplaySubsystem();
            XRDisplaySubsystemDescriptor currentDisplaySubsystemDescriptor = GetCurrentDisplaySubsystemDescriptor();
            if (currentDisplaySubsystem == null || currentDisplaySubsystemDescriptor == null || !OVRPlugin.initialized)
                return;
            //If we're using the XR SDK and the display subsystem is present, and OVRPlugin is initialized, we can init OVRManager
            InitOVRManager();
        }
#endif

#if !USING_XR_SDK_OPENXR && (!OCULUS_XR_3_3_0_OR_NEWER || !UNITY_2021_1_OR_NEWER)
        if (enableDynamicResolution && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
        {
            Debug.LogError("Vulkan Dynamic Resolution is not supported on your current build version. Ensure you are on Unity 2021+ with the Oculus XR plugin v3.3.0+ or the Unity OpenXR plugin v1.12.1+");
            enableDynamicResolution = false;
        }
#endif

#if USING_XR_SDK_OPENXR && !UNITY_Y_FLIP_FIX
        if (enableDynamicResolution)
        {
#if UNITY_2021
            Debug.LogError("Dynamic Resolution is not supported on your current build version. Ensure you are using Unity 2021.3.45f1 or greater.");
#elif UNITY_2022
            Debug.LogError("Dynamic Resolution is not supported on your current build version. Ensure you are using Unity 2022.3.49f1 or greater.");
#elif UNITY_6000_0_OR_NEWER
            Debug.LogError("Dynamic Resolution is not supported on your current build version. Ensure you are using Unity 6000.0.25f1 or greater.");
#endif

            enableDynamicResolution = false;
        }
#endif

#if UNITY_EDITOR
        if (_scriptsReloaded)
        {
            _scriptsReloaded = false;
            instance = this;
            Initialize();
        }
#endif

        SetCurrentXRDevice();

        if (OVRPlugin.shouldQuit)
        {
            Debug.Log("[OVRManager] OVRPlugin.shouldQuit detected");
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || OVR_ANDROID_MRC
            StaticShutdownMixedRealityCapture(instance);
#endif

            ShutdownInsightPassthrough();

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
            // do an early return to avoid calling the rest of the Update() logic.
            return;
#else
            Application.Quit();
#endif
        }

#if USING_XR_SDK && UNITY_ANDROID
        if (enableDynamicResolution)
        {
            OVRPlugin.Sizei recommendedResolution;
            if (OVRPlugin.GetEyeLayerRecommendedResolution(out recommendedResolution))
            {
                OVRPlugin.Sizei currentScaledResolution = new OVRPlugin.Sizei {
                    w = (int)(XRSettings.eyeTextureWidth * XRSettings.renderViewportScale),
                    h = (int)(XRSettings.eyeTextureHeight * XRSettings.renderViewportScale)
                };

                // Don't scale up or down more than a certain number of pixels per frame to avoid submitting a viewport that has disabled tiles.
                recommendedResolution.w = Mathf.Clamp(recommendedResolution.w,
                    currentScaledResolution.w - _pixelStepPerFrame,
                    currentScaledResolution.w + _pixelStepPerFrame);
                recommendedResolution.h = Mathf.Clamp(recommendedResolution.h,
                    currentScaledResolution.h - _pixelStepPerFrame,
                    currentScaledResolution.h + _pixelStepPerFrame);

                OVRPlugin.Sizei minResolution = new OVRPlugin.Sizei {
                    w = (int)(XRSettings.eyeTextureWidth * minDynamicResolutionScale / maxDynamicResolutionScale),
                    h = (int)(XRSettings.eyeTextureHeight * minDynamicResolutionScale / maxDynamicResolutionScale)
                };

                int targetWidth = Mathf.Clamp(recommendedResolution.w, minResolution.w, XRSettings.eyeTextureWidth);
                int targetHeight = Mathf.Clamp(recommendedResolution.h, minResolution.h, XRSettings.eyeTextureHeight);

                float scalingFactorX = targetWidth / (float)Settings.eyeTextureWidth;
                float scalingFactorY = targetHeight / (float)Settings.eyeTextureHeight;

                // Scaling factor is a single floating point value.
                // Try to determine which scaling factor produces the recommended resolution.
                float scalingFactor;
                if ((int)(scalingFactorX * (float)Settings.eyeTextureHeight) == targetHeight) {
                    // scalingFactorX will produce the recommended resolution for both width and height.
                    scalingFactor = scalingFactorX;
                } else if ((int)(scalingFactorY * (float)Settings.eyeTextureWidth) == targetWidth) {
                    // scalingFactorY will produce the recommended resolution for both width and height.
                    scalingFactor = scalingFactorY;
                } else {
                    // otherwise, use the smaller of the two to make sure we don't exceed the the recommended
                    // resolution size.
                    scalingFactor = Mathf.Min(scalingFactorX, scalingFactorY);
                }

                XRSettings.renderViewportScale = scalingFactor;
                ScalableBufferManager.ResizeBuffers(scalingFactor, scalingFactor);
            }
        }
#endif

        if (AllowRecenter && OVRPlugin.shouldRecenter)
        {
            OVRManager.display.RecenterPose();
        }

#if !UNITY_OPENXR_1_9_0
        if (OVRPlugin.UnityOpenXR.Enabled && _trackingOriginType == OVRManager.TrackingOrigin.FloorLevel)
        {
            Debug.LogWarning("Floor Level tracking origin is unsupported on this OpenXR Plugin version. Falling back to Stage tracking origin. Please update the OpenXR Plugin to use Floor tracking origin.");
            _trackingOriginType = OVRManager.TrackingOrigin.Stage;
        }
#endif
        if (trackingOriginType != _trackingOriginType)
            trackingOriginType = _trackingOriginType;

        tracker.isEnabled = usePositionTracking;

        OVRPlugin.rotation = useRotationTracking;

        OVRPlugin.useIPDInPositionTracking = useIPDInPositionTracking;

        // Dispatch HMD events.
        if (monoscopic != _monoscopic)
        {
            monoscopic = _monoscopic;
        }

        if (headPoseRelativeOffsetRotation != _headPoseRelativeOffsetRotation)
        {
            headPoseRelativeOffsetRotation = _headPoseRelativeOffsetRotation;
        }

        if (headPoseRelativeOffsetTranslation != _headPoseRelativeOffsetTranslation)
        {
            headPoseRelativeOffsetTranslation = _headPoseRelativeOffsetTranslation;
        }

        if (_wasHmdPresent && !isHmdPresent)
        {
            try
            {
                Debug.Log("[OVRManager] HMDLost event");
                if (HMDLost != null)
                    HMDLost();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        if (!_wasHmdPresent && isHmdPresent)
        {
            try
            {
                Debug.Log("[OVRManager] HMDAcquired event");
                if (HMDAcquired != null)
                    HMDAcquired();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        _wasHmdPresent = isHmdPresent;

        // Dispatch HMD mounted events.

        isUserPresent = OVRPlugin.userPresent;

        if (_wasUserPresent && !isUserPresent)
        {
            try
            {
                Debug.Log("[OVRManager] HMDUnmounted event");
                if (HMDUnmounted != null)
                    HMDUnmounted();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        if (!_wasUserPresent && isUserPresent)
        {
            try
            {
                Debug.Log("[OVRManager] HMDMounted event");
                if (HMDMounted != null)
                    HMDMounted();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        _wasUserPresent = isUserPresent;

        // Dispatch VR Focus events.

        hasVrFocus = OVRPlugin.hasVrFocus;

        if (_hadVrFocus && !hasVrFocus)
        {
            try
            {
                Debug.Log("[OVRManager] VrFocusLost event");
                if (VrFocusLost != null)
                    VrFocusLost();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        if (!_hadVrFocus && hasVrFocus)
        {
            try
            {
                Debug.Log("[OVRManager] VrFocusAcquired event");
                if (VrFocusAcquired != null)
                    VrFocusAcquired();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        _hadVrFocus = hasVrFocus;

        // Dispatch VR Input events.

        bool hasInputFocus = OVRPlugin.hasInputFocus;

        if (_hadInputFocus && !hasInputFocus)
        {
            try
            {
                Debug.Log("[OVRManager] InputFocusLost event");
                if (InputFocusLost != null)
                    InputFocusLost();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        if (!_hadInputFocus && hasInputFocus)
        {
            try
            {
                Debug.Log("[OVRManager] InputFocusAcquired event");
                if (InputFocusAcquired != null)
                    InputFocusAcquired();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        _hadInputFocus = hasInputFocus;

        // Dispatch Audio Device events.

        string audioOutId = OVRPlugin.audioOutId;
        if (!prevAudioOutIdIsCached)
        {
            prevAudioOutId = audioOutId;
            prevAudioOutIdIsCached = true;
        }
        else if (audioOutId != prevAudioOutId)
        {
            try
            {
                Debug.Log("[OVRManager] AudioOutChanged event");
                if (AudioOutChanged != null)
                    AudioOutChanged();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }

            prevAudioOutId = audioOutId;
        }

        string audioInId = OVRPlugin.audioInId;
        if (!prevAudioInIdIsCached)
        {
            prevAudioInId = audioInId;
            prevAudioInIdIsCached = true;
        }
        else if (audioInId != prevAudioInId)
        {
            try
            {
                Debug.Log("[OVRManager] AudioInChanged event");
                if (AudioInChanged != null)
                    AudioInChanged();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }

            prevAudioInId = audioInId;
        }

        // Dispatch tracking events.

        if (wasPositionTracked && !tracker.isPositionTracked)
        {
            try
            {
                Debug.Log("[OVRManager] TrackingLost event");
                if (TrackingLost != null)
                    TrackingLost();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        if (!wasPositionTracked && tracker.isPositionTracked)
        {
            try
            {
                Debug.Log("[OVRManager] TrackingAcquired event");
                if (TrackingAcquired != null)
                    TrackingAcquired();
            }
            catch (Exception e)
            {
                Debug.LogError("Caught Exception: " + e);
            }
        }

        wasPositionTracked = tracker.isPositionTracked;

        display.Update();

#if UNITY_EDITOR
        if (Application.isBatchMode)
        {
            OVRPlugin.UpdateInBatchMode();
        }

        // disable head pose update when xrSession is invisible
        OVRPlugin.SetTrackingPoseEnabledForInvisibleSession(false);
#endif

        if (_readOnlyControllerDrivenHandPosesType != controllerDrivenHandPosesType)
        {
            _readOnlyControllerDrivenHandPosesType = controllerDrivenHandPosesType;
            switch (_readOnlyControllerDrivenHandPosesType)
            {
                case OVRManager.ControllerDrivenHandPosesType.None:
                    OVRPlugin.SetControllerDrivenHandPoses(false);
                    OVRPlugin.SetControllerDrivenHandPosesAreNatural(false);
                    break;
                case OVRManager.ControllerDrivenHandPosesType.ConformingToController:
                    OVRPlugin.SetControllerDrivenHandPoses(true);
                    OVRPlugin.SetControllerDrivenHandPosesAreNatural(false);
                    break;
                case OVRManager.ControllerDrivenHandPosesType.Natural:
                    OVRPlugin.SetControllerDrivenHandPoses(true);
                    OVRPlugin.SetControllerDrivenHandPosesAreNatural(true);
                    break;
            }
        }

        if (_readOnlyWideMotionModeHandPosesEnabled != wideMotionModeHandPosesEnabled)
        {
            _readOnlyWideMotionModeHandPosesEnabled = wideMotionModeHandPosesEnabled;
            OVRPlugin.SetWideMotionModeHandPoses(_readOnlyWideMotionModeHandPosesEnabled);
        }


        OVRInput.Update();

        UpdateHMDEvents();

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || OVR_ANDROID_MRC
        StaticUpdateMixedRealityCapture(this, gameObject, trackingOriginType);
#endif

        UpdateInsightPassthrough(isInsightPassthroughEnabled);
        UpdateBoundary();

    }

    private void UpdateHMDEvents()
    {
        while (OVRPlugin.PollEvent(ref eventDataBuffer))
        {
            switch (eventDataBuffer.EventType)
            {
                case OVRPlugin.EventType.DisplayRefreshRateChanged:
                    if (DisplayRefreshRateChanged != null)
                    {
                        var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.DisplayRefreshRateChangedData>(
                            eventDataBuffer.EventData);
                        DisplayRefreshRateChanged(data.FromRefreshRate, data.ToRefreshRate);
                    }

                    break;
                case OVRPlugin.EventType.SpatialAnchorCreateComplete:
                {
                    var data =
                        OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpatialAnchorCreateCompleteData>(
                            eventDataBuffer.EventData);

                    OVRTask.SetResult(data.RequestId,
                        data.Result >= 0 ? new OVRAnchor(data.Space, data.Uuid) : OVRAnchor.Null);
                    SpatialAnchorCreateComplete?.Invoke(data.RequestId, data.Result >= 0, data.Space, data.Uuid);
                    break;
                }
                case OVRPlugin.EventType.SpaceSetComponentStatusComplete:
                {
                    var data = OVRDeserialize
                        .ByteArrayToStructure<OVRDeserialize.SpaceSetComponentStatusCompleteData>(eventDataBuffer
                            .EventData);
                    SpaceSetComponentStatusComplete?.Invoke(data.RequestId, data.Result >= 0, data.Space, data.Uuid,
                        data.ComponentType, data.Enabled != 0);

                    OVRTask.SetResult(data.RequestId, data.Result >= 0);
                    OVRAnchor.OnSpaceSetComponentStatusComplete(data);
                    break;
                }
                case OVRPlugin.EventType.SpaceQueryResults:
                    if (SpaceQueryResults != null)
                    {
                        var data =
                            OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceQueryResultsData>(eventDataBuffer
                                .EventData);
                        SpaceQueryResults(data.RequestId);
                    }

                    break;
                case OVRPlugin.EventType.SpaceQueryComplete:
                {
                    var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceQueryCompleteData>(
                        eventDataBuffer.EventData);
                    SpaceQueryComplete?.Invoke(data.RequestId, data.Result >= 0);
                    OVRAnchor.OnSpaceQueryComplete(data);
                    break;
                }
                case OVRPlugin.EventType.SpaceSaveComplete:
                    if (SpaceSaveComplete != null)
                    {
                        var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceSaveCompleteData>(
                            eventDataBuffer.EventData);
                        SpaceSaveComplete(data.RequestId, data.Space, data.Result >= 0, data.Uuid);
                    }

                    break;
                case OVRPlugin.EventType.SpaceEraseComplete:
                {
                    var data =
                        OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceEraseCompleteData>(eventDataBuffer
                            .EventData);

                    var result = data.Result >= 0;
                    OVRAnchor.OnSpaceEraseComplete(data);
                    SpaceEraseComplete?.Invoke(data.RequestId, result, data.Uuid, data.Location);
                    OVRTask.SetResult(data.RequestId, result);
                    break;
                }
                case OVRPlugin.EventType.SpaceShareResult:
                {
                    var data =
                        OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceShareResultData>(
                            eventDataBuffer.EventData);

                    OVRTask.SetResult(data.RequestId, OVRResult.From((OVRAnchor.ShareResult)data.Result));
                    ShareSpacesComplete?.Invoke(data.RequestId, (OVRSpatialAnchor.OperationResult)data.Result);
                    break;
                }
                case OVRPlugin.EventType.SpaceListSaveResult:
                {
                    var data =
                        OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceListSaveResultData>(
                            eventDataBuffer.EventData);

                    OVRAnchor.OnSpaceListSaveResult(data);
                    SpaceListSaveComplete?.Invoke(data.RequestId, (OVRSpatialAnchor.OperationResult)data.Result);
                    break;
                }
                case OVRPlugin.EventType.SpaceShareToGroupsComplete:
                {
                    var data = eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.ShareSpacesToGroupsCompleteData>();
                    OVRAnchor.OnShareAnchorsToGroupsComplete(data.RequestId, data.Result);
                    break;
                }
                case OVRPlugin.EventType.SceneCaptureComplete:
                {
                    var data =
                        OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SceneCaptureCompleteData>(eventDataBuffer
                            .EventData);
                    SceneCaptureComplete?.Invoke(data.RequestId, data.Result >= 0);
                    OVRTask.SetResult(data.RequestId, data.Result >= 0);
                }

                break;
                case OVRPlugin.EventType.ColocationSessionStartAdvertisementComplete:
                {
                    var data = eventDataBuffer
                        .MarshalEntireStructAs<OVRDeserialize.StartColocationSessionAdvertisementCompleteData>();
                    OVRColocationSession.OnColocationSessionStartAdvertisementComplete(data.RequestId, data.Result, data.AdvertisementUuid);
                    break;
                }

                case OVRPlugin.EventType.ColocationSessionStopAdvertisementComplete:
                {
                    var data = eventDataBuffer
                        .MarshalEntireStructAs<OVRDeserialize.StopColocationSessionAdvertisementCompleteData>();
                    OVRColocationSession.OnColocationSessionStopAdvertisementComplete(data.RequestId, data.Result);
                    break;
                }

                case OVRPlugin.EventType.ColocationSessionStartDiscoveryComplete:
                {
                    var data =
                        eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.StartColocationSessionDiscoveryCompleteData>();
                    OVRColocationSession.OnColocationSessionStartDiscoveryComplete(data.RequestId, data.Result);
                    break;
                }

                case OVRPlugin.EventType.ColocationSessionStopDiscoveryComplete:
                {
                    var data =
                        eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.StopColocationSessionDiscoveryCompleteData>();
                    OVRColocationSession.OnColocationSessionStopDiscoveryComplete(
                        data.RequestId,
                        data.Result);
                    break;
                }
                case OVRPlugin.EventType.ColocationSessionDiscoveryResult:
                {
                    unsafe
                    {
                        var data = eventDataBuffer
                            .MarshalEntireStructAs<OVRDeserialize.ColocationSessionDiscoveryResultData>();

                        OVRColocationSession.OnColocationSessionDiscoveryResult(
                            data.RequestId,
                            data.AdvertisementUuid,
                            data.AdvertisementMetadataCount,
                            data.AdvertisementMetadata);
                    }

                    break;
                }
                case OVRPlugin.EventType.ColocationSessionAdvertisementComplete:
                {
                    var data = eventDataBuffer
                        .MarshalEntireStructAs<OVRDeserialize.ColocationSessionAdvertisementCompleteData>();
                    OVRColocationSession.OnColocationSessionAdvertisementComplete(data.RequestId, data.Result);
                    break;
                }
                case OVRPlugin.EventType.ColocationSessionDiscoveryComplete:
                {
                    var data = eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.ColocationSessionDiscoveryCompleteData>();
                    OVRColocationSession.OnColocationSessionDiscoveryComplete(data.RequestId, data.Result);
                    break;
                }
                case OVRPlugin.EventType.SpaceDiscoveryComplete:
                {
                    var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceDiscoveryCompleteData>(
                        eventDataBuffer.EventData);
                    OVRAnchor.OnSpaceDiscoveryComplete(data);
                    break;
                }
                case OVRPlugin.EventType.SpaceDiscoveryResultsAvailable:
                {
                    var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpaceDiscoveryResultsData>(
                        eventDataBuffer.EventData);
                    OVRAnchor.OnSpaceDiscoveryResultsAvailable(data);
                    break;
                }
                case OVRPlugin.EventType.SpacesSaveResult:
                {
                    var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpacesSaveResultData>(
                        eventDataBuffer.EventData);
                    OVRAnchor.OnSaveSpacesResult(data);
                    OVRTask.SetResult(data.RequestId, OVRResult.From(data.Result));
                    break;
                }
                case OVRPlugin.EventType.SpacesEraseResult:
                {
                    var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.SpacesEraseResultData>(
                        eventDataBuffer.EventData);
                    OVRAnchor.OnEraseSpacesResult(data);
                    OVRTask.SetResult(data.RequestId, OVRResult.From(data.Result));
                    break;
                }
                case OVRPlugin.EventType.PassthroughLayerResumed:
                {
                    if (PassthroughLayerResumed != null)

                    {
                        var data =
                            OVRDeserialize.ByteArrayToStructure<OVRDeserialize.PassthroughLayerResumedData>(
                                eventDataBuffer.EventData);

                        PassthroughLayerResumed(data.LayerId);
                    }
                    break;
                }
                case OVRPlugin.EventType.BoundaryVisibilityChanged:
                {
                    var data = OVRDeserialize.ByteArrayToStructure<OVRDeserialize.BoundaryVisibilityChangedData>(
                        eventDataBuffer.EventData);
                    BoundaryVisibilityChanged?.Invoke(data.BoundaryVisibility);
                    isBoundaryVisibilitySuppressed = data.BoundaryVisibility == OVRPlugin.BoundaryVisibility.Suppressed;
                    break;
                }
                case OVRPlugin.EventType.CreateDynamicObjectTrackerResult:
                {
                    var data = eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.CreateDynamicObjectTrackerResultData>();
                    OVRTask.SetResult(
                        OVRTask.GetId(data.Tracker, data.EventType),
                        OVRResult<ulong, OVRPlugin.Result>.From(data.Tracker, data.Result));
                    break;
                }
                case OVRPlugin.EventType.SetDynamicObjectTrackedClassesResult:
                {
                    var data = eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.SetDynamicObjectTrackedClassesResultData>();
                    OVRTask.SetResult(
                        OVRTask.GetId(data.Tracker, data.EventType),
                        OVRResult<OVRPlugin.Result>.From(data.Result));
                    break;
                }
                case OVRPlugin.EventType.ReferenceSpaceChangePending:
                {
                    var data = eventDataBuffer.MarshalEntireStructAs<OVRDeserialize.EventDataReferenceSpaceChangePending>();
                    TrackingOriginChangePending?.Invoke(
                        (TrackingOrigin)data.ReferenceSpaceType,
                        data.PoseValid == OVRPlugin.Bool.True ? data.PoseInPreviousSpace.ToOVRPose() : null);
                    break;
                }
                default:
                    foreach (var listener in eventListeners)
                    {
                        listener.OnEvent(eventDataBuffer);
                    }

                    break;
            }
        }
    }

    public void UpdateDynamicResolutionVersion()
    {
        if (dynamicResolutionVersion == 0)
        {
            quest2MinDynamicResolutionScale = minDynamicResolutionScale;
            quest2MaxDynamicResolutionScale = maxDynamicResolutionScale;
            quest3MinDynamicResolutionScale = minDynamicResolutionScale;
            quest3MaxDynamicResolutionScale = maxDynamicResolutionScale;
        }

        dynamicResolutionVersion = MaxDynamicResolutionVersion;
    }


    private static bool multipleMainCameraWarningPresented = false;
    private static bool suppressUnableToFindMainCameraMessage = false;
    private static WeakReference<Camera> lastFoundMainCamera = null;

    public static Camera FindMainCamera()
    {
        Camera lastCamera;
        if (lastFoundMainCamera != null &&
            lastFoundMainCamera.TryGetTarget(out lastCamera) &&
            lastCamera != null &&
            lastCamera.isActiveAndEnabled &&
            lastCamera.CompareTag("MainCamera"))
        {
            return lastCamera;
        }

        Camera result = null;

        GameObject[] objects = GameObject.FindGameObjectsWithTag("MainCamera");
        List<Camera> cameras = new List<Camera>(4);
        foreach (GameObject obj in objects)
        {
            Camera camera = obj.GetComponent<Camera>();
            if (camera != null && camera.enabled)
            {
                OVRCameraRig cameraRig = camera.GetComponentInParent<OVRCameraRig>();
                if (cameraRig != null && cameraRig.trackingSpace != null)
                {
                    cameras.Add(camera);
                }
            }
        }

        if (cameras.Count == 0)
        {
            result = Camera.main; // pick one of the cameras which tagged as "MainCamera"
        }
        else if (cameras.Count == 1)
        {
            result = cameras[0];
        }
        else
        {
            if (!multipleMainCameraWarningPresented)
            {
                Debug.LogWarning(
                    "Multiple MainCamera found. Assume the real MainCamera is the camera with the least depth");
                multipleMainCameraWarningPresented = true;
            }

            // return the camera with least depth
            cameras.Sort((Camera c0, Camera c1) =>
            {
                return c0.depth < c1.depth ? -1 : (c0.depth > c1.depth ? 1 : 0);
            });
            result = cameras[0];
        }

        if (result != null)
        {
            //Debug.LogFormat("[OVRManager] mainCamera found: {0}", result.gameObject.name);
            suppressUnableToFindMainCameraMessage = false;
        }
        else if (!suppressUnableToFindMainCameraMessage)
        {
            Debug.Log("[OVRManager] unable to find a valid camera");
            suppressUnableToFindMainCameraMessage = true;
        }

        lastFoundMainCamera = new WeakReference<Camera>(result);
        return result;
    }

    private void OnDisable()
    {
        OVRSystemPerfMetrics.OVRSystemPerfMetricsTcpServer perfTcpServer =
            GetComponent<OVRSystemPerfMetrics.OVRSystemPerfMetricsTcpServer>();
        if (perfTcpServer != null)
        {
            perfTcpServer.enabled = false;
        }
    }

    private void LateUpdate()
    {
        OVRHaptics.Process();

        if (m_SpaceWarpEnabled)
        {
            Camera currentMainCamera = FindMainCamera();

            if (currentMainCamera != null)
            {
                Camera lastSpaceWarpCamera = null;
                if (m_lastSpaceWarpCamera != null)
                {
                    m_lastSpaceWarpCamera.TryGetTarget(out lastSpaceWarpCamera);
                }
                if (currentMainCamera != lastSpaceWarpCamera)
                {
                    Debug.Log("Main camera changed. Updating new camera for space warp.");

                    // If a camera is changed while space warp is still enabled, there is some setup we have to do
                    // to make sure space warp works properly such as setting the depth texture mode.
                    PrepareCameraForSpaceWarp(currentMainCamera);
                    m_lastSpaceWarpCamera = new WeakReference<Camera>(currentMainCamera);
                }

                var pos = m_AppSpaceTransform.position;
                var rot = m_AppSpaceTransform.rotation;

                // Strange behavior may occur with non-uniform scale
                var scale = m_AppSpaceTransform.lossyScale;
                SetAppSpacePosition(pos.x / scale.x, pos.y / scale.y, pos.z / scale.z);
                SetAppSpaceRotation(rot.x, rot.y, rot.z, rot.w);
            }
            else
            {
                SetAppSpacePosition(0.0f, 0.0f, 0.0f);
                SetAppSpaceRotation(0.0f, 0.0f, 0.0f, 1.0f);
            }
        }
    }

    private void FixedUpdate()
    {
        OVRInput.FixedUpdate();
    }

    private void OnDestroy()
    {
        Debug.Log("[OVRManager] OnDestroy");
#if UNITY_EDITOR
        OVRPlugin.SetLogCallback2(null);
#endif
        OVRManagerinitialized = false;
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            Debug.Log("[OVRManager] OnApplicationPause(true)");
        }
        else
        {
            Debug.Log("[OVRManager] OnApplicationPause(false)");
        }
    }

    private void OnApplicationFocus(bool focus)
    {
        if (focus)
        {
            Debug.Log("[OVRManager] OnApplicationFocus(true)");
        }
        else
        {
            Debug.Log("[OVRManager] OnApplicationFocus(false)");
        }
    }

    private void OnApplicationQuit()
    {
        Debug.Log("[OVRManager] OnApplicationQuit");
    }

    #endregion // Unity Messages

    /// <summary>
    /// Leaves the application/game and returns to the launcher/dashboard
    /// </summary>
    [System.Obsolete("Deprecated. This function will not be supported in OpenXR", false)]
    public void ReturnToLauncher()
    {
        // show the platform UI quit prompt
        OVRManager.PlatformUIConfirmQuit();
    }

    [System.Obsolete("Deprecated. This function will not be supported in OpenXR", false)]
    public static void PlatformUIConfirmQuit()
    {
        if (!isHmdPresent)
            return;

        OVRPlugin.ShowUI(OVRPlugin.PlatformUI.ConfirmQuit);
    }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || OVR_ANDROID_MRC

    public static bool staticMixedRealityCaptureInitialized = false;
    public static bool staticPrevEnableMixedRealityCapture = false;
    public static OVRMixedRealityCaptureSettings staticMrcSettings = null;
    private static bool suppressDisableMixedRealityBecauseOfNoMainCameraWarning = false;

    public static void StaticInitializeMixedRealityCapture(OVRMixedRealityCaptureConfiguration configuration)
    {
        if (!staticMixedRealityCaptureInitialized)
        {
            staticMrcSettings = ScriptableObject.CreateInstance<OVRMixedRealityCaptureSettings>();
            staticMrcSettings.ReadFrom(configuration);

#if OVR_ANDROID_MRC
            bool mediaInitialized = OVRPlugin.Media.Initialize();
            Debug.Log(mediaInitialized ? "OVRPlugin.Media initialized" : "OVRPlugin.Media not initialized");
            if (mediaInitialized)
            {
                var audioConfig = AudioSettings.GetConfiguration();
                if (audioConfig.sampleRate > 0)
                {
                    OVRPlugin.Media.SetMrcAudioSampleRate(audioConfig.sampleRate);
                    Debug.LogFormat("[MRC] SetMrcAudioSampleRate({0})", audioConfig.sampleRate);
                }

                OVRPlugin.Media.SetMrcInputVideoBufferType(OVRPlugin.Media.InputVideoBufferType.TextureHandle);
                Debug.LogFormat("[MRC] Active InputVideoBufferType:{0}", OVRPlugin.Media.GetMrcInputVideoBufferType());
                if (configuration.mrcActivationMode == MrcActivationMode.Automatic)
                {
                    OVRPlugin.Media.SetMrcActivationMode(OVRPlugin.Media.MrcActivationMode.Automatic);
                    Debug.LogFormat("[MRC] ActivateMode: Automatic");
                }
                else if (configuration.mrcActivationMode == MrcActivationMode.Disabled)
                {
                    OVRPlugin.Media.SetMrcActivationMode(OVRPlugin.Media.MrcActivationMode.Disabled);
                    Debug.LogFormat("[MRC] ActivateMode: Disabled");
                }
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
                {
                    OVRPlugin.Media.SetAvailableQueueIndexVulkan(1);
                    OVRPlugin.Media.SetMrcFrameImageFlipped(true);
                }
            }
#endif
            staticPrevEnableMixedRealityCapture = false;

            staticMixedRealityCaptureInitialized = true;
        }
        else
        {
            staticMrcSettings.ApplyTo(configuration);
        }
    }

    public static void StaticUpdateMixedRealityCapture(OVRMixedRealityCaptureConfiguration configuration,
        GameObject gameObject, TrackingOrigin trackingOrigin)
    {
        if (!staticMixedRealityCaptureInitialized)
        {
            return;
        }

#if OVR_ANDROID_MRC
        configuration.enableMixedReality = OVRPlugin.Media.GetInitialized() && OVRPlugin.Media.IsMrcActivated();

        // force external composition on Android MRC
        configuration.compositionMethod = CompositionMethod.External;

        if (OVRPlugin.Media.GetInitialized())
        {
            OVRPlugin.Media.Update();
        }
#endif

        if (configuration.enableMixedReality)
        {
            Camera mainCamera = FindMainCamera();
            if (mainCamera != null)
            {
                if (!staticPrevEnableMixedRealityCapture)
                {
                    OVRPlugin.SendEvent("mixed_reality_capture", "activated");
                    Debug.Log("MixedRealityCapture: activate");
                    staticPrevEnableMixedRealityCapture = true;
                }

                OVRMixedReality.Update(gameObject, mainCamera, configuration, trackingOrigin);
                suppressDisableMixedRealityBecauseOfNoMainCameraWarning = false;
            }
            else if (!suppressDisableMixedRealityBecauseOfNoMainCameraWarning)
            {
                Debug.LogWarning("Main Camera is not set, Mixed Reality disabled");
                suppressDisableMixedRealityBecauseOfNoMainCameraWarning = true;
            }
        }
        else if (staticPrevEnableMixedRealityCapture)
        {
            Debug.Log("MixedRealityCapture: deactivate");
            staticPrevEnableMixedRealityCapture = false;
            OVRMixedReality.Cleanup();
        }

        staticMrcSettings.ReadFrom(configuration);
    }

    public static void StaticShutdownMixedRealityCapture(OVRMixedRealityCaptureConfiguration configuration)
    {
        if (staticMixedRealityCaptureInitialized)
        {
            ScriptableObject.Destroy(staticMrcSettings);
            staticMrcSettings = null;

            OVRMixedReality.Cleanup();

#if OVR_ANDROID_MRC
            if (OVRPlugin.Media.GetInitialized())
            {
                OVRPlugin.Media.Shutdown();
            }
#endif
            staticMixedRealityCaptureInitialized = false;
        }
    }

#endif


    enum PassthroughInitializationState
    {
        Unspecified,
        Pending,
        Initialized,
        Failed
    };

    public static Action<bool> OnPassthroughInitializedStateChange;

    private static Observable<PassthroughInitializationState> _passthroughInitializationState
        = new Observable<PassthroughInitializationState>(PassthroughInitializationState.Unspecified,
            newValue => OnPassthroughInitializedStateChange?.Invoke(newValue == PassthroughInitializationState.Initialized));

    private static bool PassthroughInitializedOrPending(PassthroughInitializationState state)
    {
        return state == PassthroughInitializationState.Pending || state == PassthroughInitializationState.Initialized;
    }

    private static bool InitializeInsightPassthrough()
    {
        if (PassthroughInitializedOrPending(_passthroughInitializationState.Value))
            return false;

        bool passthroughResult = OVRPlugin.InitializeInsightPassthrough();
        OVRPlugin.Result result = OVRPlugin.GetInsightPassthroughInitializationState();
        if (result < 0)
        {
            _passthroughInitializationState.Value = PassthroughInitializationState.Failed;
#if UNITY_EDITOR_WIN
            // Looks like the developer is trying to run PT over Link. One possible failure cause is missing PTOL setup.
            string ptolDocLink = "https://developer.oculus.com/documentation/unity/unity-passthrough-gs/#prerequisites-1";
            string ptolDocLinkTag = $"<a href=\"{ptolDocLink}\">{ptolDocLink}</a>";
            Debug.LogError($"Failed to initialize Insight Passthrough. Please ensure that all prerequisites for " +
                           $"running Passthrough over Link are met: {ptolDocLinkTag}. " +
                           $"Passthrough will be unavailable. Error {result.ToString()}.");
#else
            Debug.LogError("Failed to initialize Insight Passthrough. Passthrough will be unavailable. Error " + result.ToString() + ".");
#endif
        }
        else
        {
            if (result == OVRPlugin.Result.Success_Pending)
            {
                _passthroughInitializationState.Value = PassthroughInitializationState.Pending;
            }
            else
            {
                _passthroughInitializationState.Value = PassthroughInitializationState.Initialized;
            }
        }

        return PassthroughInitializedOrPending(_passthroughInitializationState.Value);
    }

    private static void ShutdownInsightPassthrough()
    {
        if (PassthroughInitializedOrPending(_passthroughInitializationState.Value))
        {
            if (OVRPlugin.ShutdownInsightPassthrough())
            {
                _passthroughInitializationState.Value = PassthroughInitializationState.Unspecified;
            }
            else
            {
                // If it did not shut down, it may already be deinitialized.
                bool isInitialized = OVRPlugin.IsInsightPassthroughInitialized();
                if (isInitialized)
                {
                    Debug.LogError("Failed to shut down passthrough. It may be still in use.");
                }
                else
                {
                    _passthroughInitializationState.Value = PassthroughInitializationState.Unspecified;
                }
            }
        }
        else
        {
            // Allow initialization to proceed on restart.
            _passthroughInitializationState.Value = PassthroughInitializationState.Unspecified;
        }
    }

    private static void UpdateInsightPassthrough(bool shouldBeEnabled)
    {
        if (shouldBeEnabled != PassthroughInitializedOrPending(_passthroughInitializationState.Value))
        {
            if (shouldBeEnabled)
            {
                // Prevent attempts to initialize on every update if failed once.
                if (_passthroughInitializationState.Value != PassthroughInitializationState.Failed)
                    InitializeInsightPassthrough();
            }
            else
            {
                ShutdownInsightPassthrough();
            }
        }
        else
        {
            // If the initialization was pending, it may have successfully completed.
            if (_passthroughInitializationState.Value == PassthroughInitializationState.Pending)
            {
                OVRPlugin.Result result = OVRPlugin.GetInsightPassthroughInitializationState();
                if (result == OVRPlugin.Result.Success)
                {
                    _passthroughInitializationState.Value = PassthroughInitializationState.Initialized;
                }
                else if (result < 0)
                {
                    _passthroughInitializationState.Value = PassthroughInitializationState.Failed;
                    Debug.LogError("Failed to initialize Insight Passthrough. " +
                                   "Passthrough will be unavailable. Error " + result.ToString() + ".");
                }
            }
        }
    }

    private static PassthroughCapabilities _passthroughCapabilities;

    private void InitializeBoundary()
    {
        var result = OVRPlugin.GetBoundaryVisibility(out var boundaryVisibility);
        if (result == OVRPlugin.Result.Success)
        {
            isBoundaryVisibilitySuppressed = boundaryVisibility == OVRPlugin.BoundaryVisibility.Suppressed;
        }
        else if (result == OVRPlugin.Result.Failure_Unsupported || result == OVRPlugin.Result.Failure_NotYetImplemented)
        {
            isBoundaryVisibilitySuppressed = false;
            shouldBoundaryVisibilityBeSuppressed = false;
        }
        else
        {
            Debug.LogWarning("Could not retrieve initial boundary visibility state. " +
                             "Defaulting to not suppressed.");
            isBoundaryVisibilitySuppressed = false;
        }
    }

    private void UpdateBoundary()
    {
        // will repeat the request as long as Passthrough is setup and
        // the desired state != actual state of the boundary
        if (shouldBoundaryVisibilityBeSuppressed == isBoundaryVisibilitySuppressed)
            return;

        var ptSupported = PassthroughInitializedOrPending(
            _passthroughInitializationState.Value) && isInsightPassthroughEnabled;
        if (!ptSupported)
            return;

        var desiredVisibility = shouldBoundaryVisibilityBeSuppressed
            ? OVRPlugin.BoundaryVisibility.Suppressed
            : OVRPlugin.BoundaryVisibility.NotSuppressed;

        var result = OVRPlugin.RequestBoundaryVisibility(desiredVisibility);
        if (result == OVRPlugin.Result.Warning_BoundaryVisibilitySuppressionNotAllowed)
        {
            if (!_updateBoundaryLogOnce)
            {
                _updateBoundaryLogOnce = true;
                Debug.LogWarning("Cannot suppress boundary visibility as it's required to be on.");
            }
        }
        else if (result == OVRPlugin.Result.Success)
        {
            _updateBoundaryLogOnce = false;
            isBoundaryVisibilitySuppressed = shouldBoundaryVisibilityBeSuppressed;
        }
    }

    /// <summary>
    /// Checks whether simultaneous hands and controllers is currently supported by the system.
    /// This method should only be called when the XR Plug-in is initialized.
    /// </summary>
    public static bool IsMultimodalHandsControllersSupported()
    {
        return OVRPlugin.IsMultimodalHandsControllersSupported();
    }

    /// <summary>
    /// Checks whether Passthrough is supported by the system. This method should only be called when the XR Plug-in is initialized.
    /// </summary>
    public static bool IsInsightPassthroughSupported()
    {
        return OVRPlugin.IsInsightPassthroughSupported();
    }

    /// <summary>
    /// This class is used by <see cref="OVRManager.GetPassthroughCapabilities()"/> to report on Passthrough
    /// capabilities provided by the system.
    /// Use it to configure various passthrough color mapping techniques, e.g. color LUTs. See
    /// [Color Mapping Techniques](https://developer.oculus.com/documentation/unity/unity-customize-passthrough-color-mapping/) for more details.
    /// </summary>
    public class PassthroughCapabilities
    {
        /// <summary>
        /// Indicates that Passthrough is available on the current system.
        /// </summary>
        public bool SupportsPassthrough { get; }

        /// <summary>
        /// Indicates that the system can show Passthrough with realistic colors. If 'false', then the system
        /// either supports the basic grayscale passthrough, or doesn't support passthrough at all.
        /// </summary>
        public bool SupportsColorPassthrough { get; }

        /// <summary>
        /// Maximum color LUT resolution supported by the system. Use it together with the <see cref="OVRPassthroughLayer.SetColorLut(OVRPassthroughColorLut, float)"/>
        /// method to apply a color LUT to a <see cref="OVRPassthroughLayer"/> component.
        /// </summary>
        public uint MaxColorLutResolution { get; }

        public PassthroughCapabilities(bool supportsPassthrough, bool supportsColorPassthrough,
            uint maxColorLutResolution)
        {
            SupportsPassthrough = supportsPassthrough;
            SupportsColorPassthrough = supportsColorPassthrough;
            MaxColorLutResolution = maxColorLutResolution;
        }
    }

    /// <summary>
    /// Returns information about Passthrough capabilities provided by the system. This method should only be called when the XR Plug-in is initialized.
    /// </summary>
    public static PassthroughCapabilities GetPassthroughCapabilities()
    {
        if (_passthroughCapabilities == null)
        {
            OVRPlugin.PassthroughCapabilities internalCapabilities = new OVRPlugin.PassthroughCapabilities();
            if (!OVRPlugin.IsSuccess(OVRPlugin.GetPassthroughCapabilities(ref internalCapabilities)))
            {
                // Fallback to querying flags only
                internalCapabilities.Flags = OVRPlugin.GetPassthroughCapabilityFlags();
                internalCapabilities.MaxColorLutResolution = 64; // 64 is the value supported at initial release
            }

            _passthroughCapabilities = new PassthroughCapabilities(
                supportsPassthrough: (internalCapabilities.Flags & OVRPlugin.PassthroughCapabilityFlags.Passthrough) ==
                                     OVRPlugin.PassthroughCapabilityFlags.Passthrough,
                supportsColorPassthrough: (internalCapabilities.Flags & OVRPlugin.PassthroughCapabilityFlags.Color) ==
                                          OVRPlugin.PassthroughCapabilityFlags.Color,
                maxColorLutResolution: internalCapabilities.MaxColorLutResolution
            );
        }

        return _passthroughCapabilities;
    }

    /// Checks whether Passthrough is initialized.
    /// \return Boolean value to indicate the current state of passthrough. If the value returned is true, Passthrough is initialized.
    public static bool IsInsightPassthroughInitialized()
    {
        return _passthroughInitializationState.Value == PassthroughInitializationState.Initialized;
    }

    /// Checks whether Passthrough has failed initialization.
    /// \return Boolean value to indicate the passthrough initialization failed status. If the value returned is true, Passthrough has failed the initialization.
    public static bool HasInsightPassthroughInitFailed()
    {
        return _passthroughInitializationState.Value == PassthroughInitializationState.Failed;
    }

    /// Checks whether Passthrough is in the process of initialization.
    /// \return Boolean value to indicate the current state of passthrough. If the value returned is true, Passthrough is initializing.
    public static bool IsInsightPassthroughInitPending()
    {
        return _passthroughInitializationState.Value == PassthroughInitializationState.Pending;
    }

    /// <summary>
    /// Get a system recommendation on whether Passthrough should be active.
    /// When set, it is recommended for apps which optionally support an MR experience with Passthrough to default to that mode.
    /// Currently, this is determined based on whether the user has Passthrough active in the home environment.
    /// </summary>
    /// <returns>Flag indicating whether Passthrough is recommended.</returns>
    public static bool IsPassthroughRecommended()
    {
        OVRPlugin.GetPassthroughPreferences(out var preferences);
        return (preferences.Flags & OVRPlugin.PassthroughPreferenceFlags.DefaultToActive) ==
            OVRPlugin.PassthroughPreferenceFlags.DefaultToActive;
    }

    #region Utils

    private class Observable<T>
    {
        private T _value;

        public Action<T> OnChanged;

        public T Value
        {
            get { return _value; }
            set
            {
                var oldValue = _value;
                this._value = value;
                if (OnChanged != null)
                {
                    OnChanged(value);
                }
            }
        }

        public Observable()
        {
        }

        public Observable(T defaultValue)
        {
            _value = defaultValue;
        }

        public Observable(T defaultValue, Action<T> callback)
            : this(defaultValue)
        {
            OnChanged += callback;
        }
    }

    #endregion
}
