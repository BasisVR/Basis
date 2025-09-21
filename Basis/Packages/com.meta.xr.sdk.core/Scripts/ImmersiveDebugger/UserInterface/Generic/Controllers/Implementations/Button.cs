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

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> used for generic Button UI element.
    /// Normally serves as the base component for more specific functioned buttons in Immersive Debugger.
    /// Contains logic for handling events with <see cref="Callback"/> and a standard pattern of haptics (<see cref="HapticsClip"/> is played upon hovering).
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class Button : InteractableController
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            _hapticsClip = null;
        }

        private static OVRHapticsClip _hapticsClip;
        private static OVRHapticsClip HapticsClip
        {
            get
            {
                if (OVRHaptics.Config.SampleSizeInBytes == 0)
                {
                    return null;
                }

                return _hapticsClip ??= new OVRHapticsClip(new byte[5] { 128, 255, 255, 128, 255 }, 5);
            }
        }

        /// <summary>
        /// The callback that is invoked upon clicking the button UI (from the overriden <see cref="OnPointerClick"/> function).
        /// </summary>
        public Action Callback { get; set; }

        /// <summary>
        /// The overriden function from the <see cref="PointerHandler"/> ancestor class.
        /// Invoking the <see cref="Callback"/> within the same class.
        /// </summary>
        public override void OnPointerClick()
        {
            Callback?.Invoke();

            Telemetry.OnButtonClicked(this);
        }

        protected override void OnHoverChanged()
        {
            base.OnHoverChanged();
            if (Hover)
            {
                PlayHaptics(HapticsClip);
            }
        }
    }
}

