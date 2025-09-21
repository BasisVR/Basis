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
/// Place this script on a GameObject to have a quick way to turn off [chromatic aberration](https://en.wikipedia.org/wiki/Chromatic_aberration)
/// correction using a button on a controller or gamepad. This is not intended for use with other scripts.
/// </summary>
[HelpURL("https://developer.oculus.com/documentation/unity/unity-core-overview/#scripts")]
public class OVRChromaticAberration : MonoBehaviour
{
    /// <summary>
    /// The button that will toggle chromatic aberration correction. You can change this to any other button.
    /// </summary>
    public OVRInput.RawButton toggleButton = OVRInput.RawButton.X;

    private bool chromatic = false;

    void Start()
    {
        // Enable/Disable Chromatic Aberration Correction.
        // NOTE: Enabling Chromatic Aberration for mobile has a large performance cost.
        OVRManager.instance.chromatic = chromatic;
    }

    void Update()
    {
        // NOTE: some of the buttons defined in OVRInput.RawButton are not available on the Android game pad controller
        if (OVRInput.GetDown(toggleButton))
        {
            //*************************
            // toggle chromatic aberration correction
            //*************************
            chromatic = !chromatic;
            OVRManager.instance.chromatic = chromatic;
        }
    }
}
