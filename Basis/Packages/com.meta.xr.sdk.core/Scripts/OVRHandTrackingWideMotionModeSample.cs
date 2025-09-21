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
using Vector3 = UnityEngine.Vector3;
using UnityEngine.EventSystems;

/// <summary>
/// This script runs the "HandTrackingWideMotionMode" scene in the Core SDK samples. It is an example of how to
/// turn the Wide Motion Mode on and off in <see cref="OVRManager"/> as well as showing the impact that this feature
/// has on hand tracking behavior.
/// </summary>
[DisallowMultipleComponent]
[HelpURL("https://developer.oculus.com/documentation/unity/unity-wide-motion-mode/")]
public class OVRHandTrackingWideMotionModeSample : MonoBehaviour
{
    /// <summary>
    /// The toggle used to turn Wide Motion Mode on and off.
    /// </summary>
    [SerializeField]
    public Toggle fusionToggle;
    [SerializeField]
    private LineRenderer leftLinePointer;
    [SerializeField]
    private LineRenderer rightLinePointer;

    [SerializeField]
    private OVRHand handLeft;
    [SerializeField]
    private OVRHand handRight;

    [SerializeField]
    private OVRInputModule inputModule;

    void OnEnable()
    {
        fusionToggle.onValueChanged.AddListener(OnFusionToggleChanged);
    }

    void OnDisable()
    {
        fusionToggle.onValueChanged.RemoveListener(OnFusionToggleChanged);
    }

    private void Update()
    {
        UpdateLineRenderer();
    }

    private void UpdateLineRenderer()
    {
        leftLinePointer.enabled = false;
        rightLinePointer.enabled = false;

        UpdateLineRendererForHand(false);
        UpdateLineRendererForHand(true);
    }

    private void UpdateLineRendererForHand(bool isLeft)
    {
        Transform inputTransform = null;

        if (isLeft)
        {
            if (handLeft != null && handLeft.IsPointerPoseValid)
            {
                inputTransform = handLeft.PointerPose;
                if (handLeft.GetFingerIsPinching(OVRHand.HandFinger.Index))
                {
                    inputModule.rayTransform = inputTransform;
                }
            }
            else
            {
                inputTransform = null;
            }
        }
        else
        {
            if (handRight != null && handRight.IsPointerPoseValid)
            {
                inputTransform = handRight.PointerPose;
                if (handRight.GetFingerIsPinching(OVRHand.HandFinger.Index))
                {
                    inputModule.rayTransform = inputTransform;
                }
            }
            else
            {
                inputTransform = null;
            }
            inputTransform = (handRight != null && handRight.IsPointerPoseValid)
                    ? handRight.PointerPose
                    : null;

        }
        if (inputTransform == null)
        {
            return;
        }

        var inputPosition = inputTransform.position;

        LineRenderer linePointer = (isLeft) ? leftLinePointer : rightLinePointer;
        var ray = new Ray(inputPosition, inputTransform.rotation * Vector3.forward);

        linePointer.enabled = true;
        linePointer.SetPosition(0, inputTransform.position + ray.direction * 0.05f);
        linePointer.SetPosition(1, inputPosition + ray.direction * 2.5f);
    }

    private void OnFusionToggleChanged(bool newValue)
    {
        OVRManager.instance.wideMotionModeHandPosesEnabled = newValue;
    }
}
