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


namespace Meta.XR.ImmersiveDebugger.UserInterface.Generic
{
    /// <summary>
    /// This is a <see cref="MonoBehaviour"/> served as the base element of any interactable UI elements of Immersive Debugger.
    /// Manages the pointer handler and hover states of the UI element, and providing a helper function to play haptics by inherited classes.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class InteractableController : Controller
    {
        private PointerHandler _handler;
        private bool _hover;

        protected bool Hover
        {
            get => _hover;
            private set
            {
                if (_hover == value)
                {
                    return;
                }

                _hover = value;
                OnHoverChanged();
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

            _handler = GameObject.AddComponent<PointerHandler>();
            _handler.Controller = this;
        }

        /// <summary>
        /// The function that is called by the <see cref="PointerHandler"/> when the pointer enters into the UI component.
        /// </summary>
        public void OnPointerEnter()
        {
            Hover = true;
        }
        /// <summary>
        /// The function that is called by the <see cref="PointerHandler"/> when the pointer exits from the UI component.
        /// </summary>
        public void OnPointerExit()
        {
            Hover = false;
        }

        /// <summary>
        /// The function that is called by the <see cref="PointerHandler"/> when the pointer clicked onto the UI component.
        /// Expect to be overriden by inherited classes.
        /// </summary>
        public virtual void OnPointerClick()
        {

        }

        protected virtual void OnHoverChanged()
        {
        }

        protected virtual void OnDisable()
        {
            Hover = false;
        }

        protected void PlayHaptics(OVRHapticsClip hapticsClip)
        {
            if (hapticsClip == null) return;

            switch (OVRInput.GetActiveController())
            {
                case OVRInput.Controller.LTouch:
                    OVRHaptics.LeftChannel.Mix(hapticsClip);
                    break;
                case OVRInput.Controller.RTouch:
                    OVRHaptics.RightChannel.Mix(hapticsClip);
                    break;
                default:
                    break;
            }
        }
    }
}

