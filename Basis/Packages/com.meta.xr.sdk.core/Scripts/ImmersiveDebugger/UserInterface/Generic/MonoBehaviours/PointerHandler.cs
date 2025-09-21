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


using UnityEngine.EventSystems;

namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> for responsible as handler for pointer events.
    /// Used by all the interactable UI elements of Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class PointerHandler : UIBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>
        /// The corresponding <see cref="InteractableController"/> that's used to handle pointer events.
        /// This is the UI elements that's receiving the pointer events.
        /// </summary>
        public InteractableController Controller { get; set; }

        /// <summary>
        /// Implementation of the <see cref="IPointerClickHandler"/> that's receiving the pointer click events
        /// and forwarding to <see cref="InteractableController"/>'s corresponding function in this class.
        /// </summary>
        /// <param name="eventData">The pointer event data from the click event</param>
        public void OnPointerClick(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            if (Controller != null)
            {
                Controller.OnPointerClick();
            }
        }

        /// <summary>
        /// Implementation of the <see cref="IPointerEnterHandler"/> that's receiving the pointer enter events
        /// and forwarding to <see cref="InteractableController"/>'s corresponding function in this class.
        /// </summary>
        /// <param name="eventData">The pointer event data from the enter event</param>
        public void OnPointerEnter(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            if (Controller != null)
            {
                Controller.OnPointerEnter();
            }
        }

        /// <summary>
        /// Implementation of the <see cref="IPointerExitHandler"/> that's receiving the pointer exit events
        /// and forwarding to <see cref="InteractableController"/>'s corresponding function in this class.
        /// </summary>
        /// <param name="eventData">The pointer event data from the exit event</param>
        public void OnPointerExit(PointerEventData eventData)
        {
            // Filtering out any event that is not triggered during our bespoke processing of the PanelInputModule.
            if (!PanelInputModule.Processing)
            {
                return;
            }

            if (Controller != null)
            {
                Controller.OnPointerExit();
            }
        }
    }
}

