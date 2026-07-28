using System;
using UnityEngine;

public partial class BasisHandHeldCameraUI
{
    [Serializable]
    public class CameraSettings
    {
        /// <summary>
        /// Bumped whenever fields are added whose zero-fill value (JsonUtility leaves absent fields
        /// at 0/false) differs from their intended default. LoadSettings migrates older files.
        /// v2 added the auto-follow config, capture toggles and MSAA.
        /// </summary>
        public const int CurrentVersion = 5;
        public int settingsVersion = CurrentVersion;

        public CameraSettings()
        {
            settingsVersion = CurrentVersion;

            autoFollowPositionOffset = new Vector3(0.5f, 0f, 1.4f);
            autoFollowRotationOffset = Vector3.zero;
            autoFollowPlayspace = true;
            autoFollowLookAtPlayer = true;
            autoFollowLookAtHeightOffset = 0f;

            dofMode = 2;          // Bokeh, matching the authored profile
            dofFocalLength = 50f;
            dofBladeCount = 5;

            resolutionIndex = 1;
            formatIndex = 0;
            apertureIndex = 0;
            shutterSpeedIndex = 0;
            isoIndex = 0;
            fov = 40;
            focusDistance = 10f;
            sensorSizeX = 36f;
            sensorSizeY = 24f;
            bloomIntensity = 0.5f;
            bloomThreshold = 0.5f;
            contrast = 1f;
            saturation = 1f;
            depthAperture = 2.8f;
            depthFocusDistance = 10f;
            depthIsActive = false;
            useManualFocus = true;
            showExposureOnCamera = false;

            VolumetricFogVolumedensity = 0.01f;
            VolumetricFogenableAPVContribution = true;
            VolumetricFogenableMainLightContribution = true;

            msaaSamples = 2;
        }

        public int resolutionIndex = 1;
        public int formatIndex = 0;
        public int msaaSamples = 2;

        public int apertureIndex;
        public int shutterSpeedIndex;
        public int isoIndex;

        public int exposureIndex = 6;

        /// <summary>Whether the exposure slider is shown on the camera's own interface. Off unless turned on from the camera panel.</summary>
        public bool showExposureOnCamera = false;


        public float fov;
        public float focusDistance;
        public float sensorSizeX;
        public float sensorSizeY;

        public float bloomIntensity;
        public float bloomThreshold;

        public float contrast;
        public float saturation;
        public float hueShift;

        public float depthAperture;
        public float depthFocusDistance;
        public bool depthIsActive;
        public int dofMode;
        public float dofFocalLength;
        public int dofBladeCount;

        public bool useManualFocus = true;

        public float VolumetricFogVolumedensity;
        public bool VolumetricFogenableAPVContribution;
        public bool VolumetricFogenableMainLightContribution;

        // Extra post-processing (0 = effect off, so a fresh install adds nothing to the shot).
        public float vignette;
        public float chromaticAberration;
        public float filmGrain;
        public float whiteBalanceTemperature;
        public float whiteBalanceTint;
        public float lensDistortion;

        public bool autoFocusFollowSubject;

        // Auto-follow configuration (the follow target itself is per-session and not persisted).
        public Vector3 autoFollowPositionOffset;
        public Vector3 autoFollowRotationOffset;
        public bool autoFollowPlayspace;
        public bool autoFollowLookAtPlayer;
        public float autoFollowLookAtHeightOffset;

        // Capture-mode toggles.
        public bool capture360;
        public bool useAutoLeveling;
        public bool useVRHandheldSmoothing;
    }
}
