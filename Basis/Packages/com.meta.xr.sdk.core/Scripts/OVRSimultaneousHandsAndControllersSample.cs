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
using UnityEngine.UI;

/// <summary>
/// This is a simple script for running the "Simultaneous Hands And Controllers" sample.
/// </summary>
/// <remarks>
/// The sample is extremely straightforward: when the enabled button is pressed,
/// <see cref="OVRInput.EnableSimultaneousHandsAndControllers"/> is invoked such that simultaneous
/// hand and controller usage becomes feasible; correspondingly,
/// <see cref="OVRInput.DisableSimultaneousHandsAndControllers"/> when the disable button is pressed.
/// The sample also populates display text with the string representation of the currently active
/// <see cref="OVRInput.Controller"/>.
/// </remarks>
[DisallowMultipleComponent]
public class OVRSimultaneousHandsAndControllersSample : MonoBehaviour
{
    [SerializeField]
    private Button enableButton;

    [SerializeField]
    private Button disableButton;

    /// <summary>
    /// The text asset using which the string representation of the currently active
    /// <see cref="OVRInput.Controller"/> will be displayed.
    /// </summary>
    /// <remarks>
    /// This field can be set or modified programmatically; however, leaving it null during an update
    /// is unsupported and will cause errors.
    /// </remarks>
    [SerializeField]
    public Text displayText;

    private void Update()
    {
        displayText.text = OVRInput.GetActiveController().ToString();
    }

    /// <summary>
    /// Enables simultaneous hands and controllers, disables the enable button, and enables the
    /// disable button.
    /// </summary>
    /// <remarks>
    /// This method is intended to be invoked in response to a press on the enable button.
    /// </remarks>
    public void EnableSimultaneousHandsAndControllers()
    {
        OVRInput.EnableSimultaneousHandsAndControllers();
        enableButton.interactable = false;
        disableButton.interactable = true;
    }

    /// <summary>
    /// Disables simultaneous hands and controllers, enables the enable button, and disables the
    /// disable button.
    /// </summary>
    /// <remarks>
    /// This method is intended to be invoked in response to a press on the enable button.
    /// </remarks>
    public void DisableSimultaneousHandsAndControllers()
    {
        OVRInput.DisableSimultaneousHandsAndControllers();
        enableButton.interactable = true;
        disableButton.interactable = false;
    }
}
