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
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// This class emits events based on recognized microgestures for a specific OVRHand.
/// It tracks the state of gestures to emit the corresponding events.
/// </summary>
public class OVRMicrogestureEventSource : MonoBehaviour
{
    [SerializeField]
    private OVRHand _hand;

    /// <summary>
    /// UnityEvent triggered when a microgesture is recognized by the system. This event provides the specific type of microgesture detected.
    /// </summary>
    public UnityEvent<OVRHand.MicrogestureType> GestureRecognizedEvent;

    /// <summary>
    /// Event triggered when a microgesture is recognized by the system. This event provides the specific type of microgesture detected.
    /// </summary>
    public Action<OVRHand.MicrogestureType> WhenGestureRecognized = delegate { };

    /// <summary>
    /// Gets or sets the OVRHand associated with this event source, which is used to detect gestures.
    /// </summary>
    public OVRHand Hand
    {
        get { return _hand; }
        set { _hand = value; }
    }

    private void Update()
    {
        OVRHand.MicrogestureType mgType = _hand.GetMicrogestureType();
        if (mgType != OVRHand.MicrogestureType.Invalid && mgType != OVRHand.MicrogestureType.NoGesture)
        {
            RaiseGestureRecognized(mgType);
        }
    }

    private void RaiseGestureRecognized(OVRHand.MicrogestureType gesture)
    {
        GestureRecognizedEvent?.Invoke(gesture);
        WhenGestureRecognized?.Invoke(gesture);
    }
}
