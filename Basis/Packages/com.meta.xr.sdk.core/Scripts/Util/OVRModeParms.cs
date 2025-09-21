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

using UnityEngine;

/// <summary>
/// Place this script on a GameObject to log when the application enters power saving mode.
/// This script can also simulate power saving mode with a gamepad or controller button press by forcing
/// CPU level to power saving and the GPU level to sustained low. Refer to [CPU/GPU Level](https://developer.oculus.com/resources/os-cpu-gpu-levels/)
/// documentation for more information. This is not intended to use with other scripts.
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-core-overview/#scripts")]
public class OVRModeParms : MonoBehaviour
{
    #region Member Variables

    /// <summary>
    /// The <see cref="OVRInput.RawButton"/> that will trigger the power saving mode simulation. You can change this to any other button.
    /// </summary>
    public OVRInput.RawButton resetButton = OVRInput.RawButton.X;

    #endregion

    /// <summary>
    /// Starts a repeating call to check for power saving mode every 10 seconds.
    /// </summary>
    void Start()
    {
        if (!OVRManager.isHmdPresent)
        {
            enabled = false;
            return;
        }

        // Call TestPowerLevelState after 10 seconds
        // and repeats every 10 seconds.
        InvokeRepeating("TestPowerStateMode", 10, 10.0f);
    }

    /// <summary>
    /// Changes default VR mode parameters dynamically when the <see cref="OVRModeParms.resetButton"/> is pressed.  This may cause 1 frame of flicker as it leaves and re-enters VR mode.
    /// </summary>
    void Update()
    {
        // NOTE: some of the buttons defined in OVRInput.RawButton are not available on the Android game pad controller
        if (OVRInput.GetDown(resetButton))
        {
            //*************************
            // Dynamically change VrModeParms cpu and gpu level.
            // NOTE: Reset will cause 1 frame of flicker as it leaves
            // and re-enters Vr mode.
            //*************************
            OVRPlugin.suggestedCpuPerfLevel = OVRPlugin.ProcessorPerformanceLevel.PowerSavings;
            OVRPlugin.suggestedGpuPerfLevel = OVRPlugin.ProcessorPerformanceLevel.SustainedLow;
        }
    }

    /// <summary>
    /// Checks if power saving mode is active and prints a log if it is active.
    /// </summary>
    void TestPowerStateMode()
    {
        //*************************
        // Check power-level state mode
        //*************************
        if (OVRPlugin.powerSaving)
        {
            // The device has been throttled
            Debug.Log("POWER SAVE MODE ACTIVATED");
        }
    }
}
