using System;

public partial class BasisHandHeldCameraUI
{
    [Serializable]
    public class CameraSettings
    {
        public CameraSettings()
        {
            resolutionIndex = 0;
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
            depthAperture = 1f;
            depthFocusDistance = 10f;
            depthIsActive = false;
            useManualFocus = true;

            VolumetricFogVolumedensity = 0.01f;
            VolumetricFogenableAPVContribution = true;
            VolumetricFogenableMainLightContribution = true;
            VolumetricenableAdditionalLightsContribution = true;
        }

        public int resolutionIndex = 0;
        public int formatIndex = 0;

        public int apertureIndex;
        public int shutterSpeedIndex;
        public int isoIndex;

        public int exposureIndex = 6;

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

        public bool useManualFocus = true;

        public float VolumetricFogVolumedensity;
        public bool VolumetricFogenableAPVContribution;
        public bool VolumetricFogenableMainLightContribution;
        public bool VolumetricenableAdditionalLightsContribution;
    }
}
