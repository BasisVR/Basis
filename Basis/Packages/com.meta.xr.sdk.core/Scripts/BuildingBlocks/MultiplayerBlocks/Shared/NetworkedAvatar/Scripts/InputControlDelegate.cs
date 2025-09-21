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

#if META_AVATAR_SDK_DEFINED
using Oculus.Avatar2;
using Button = OVRInput.Button;
using Touch = OVRInput.Touch;

namespace Meta.XR.MultiplayerBlocks.Shared
{
    public class InputControlDelegate : OvrAvatarInputControlDelegate
    {
        public override bool GetInputControlState(out OvrAvatarInputControlState inputControlState)
        {
            inputControlState = new OvrAvatarInputControlState();
            inputControlState.type = GetControllerType();

            UpdateControllerInput(ref inputControlState.leftControllerState, OVRInput.Controller.LTouch);
            UpdateControllerInput(ref inputControlState.rightControllerState, OVRInput.Controller.RTouch);

            return true;
        }

        private void UpdateControllerInput(ref OvrAvatarControllerState controllerState, OVRInput.Controller controller)
        {
            controllerState.buttonMask = 0;
            controllerState.touchMask = 0;

            // Button Press
            if (OVRInput.Get(Button.One, controller))
            {
                controllerState.buttonMask |= CAPI.ovrAvatar2Button.One;
            }

            if (OVRInput.Get(Button.Two, controller))
            {
                controllerState.buttonMask |= CAPI.ovrAvatar2Button.Two;
            }

            if (OVRInput.Get(Button.Three, controller))
            {
                controllerState.buttonMask |= CAPI.ovrAvatar2Button.Three;
            }

            if (OVRInput.Get(Button.PrimaryThumbstick, controller))
            {
                controllerState.buttonMask |= CAPI.ovrAvatar2Button.Joystick;
            }

            // Button Touch
            if (OVRInput.Get(Touch.One, controller))
            {
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.One;
            }

            if (OVRInput.Get(Touch.Two, controller))
            {
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.Two;
            }

            if (OVRInput.Get(Touch.PrimaryThumbstick, controller))
            {
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.Joystick;
            }

            if (OVRInput.Get(Touch.PrimaryThumbRest, controller))
            {
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.ThumbRest;
            }

            // Trigger
            controllerState.indexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
            if (OVRInput.Get(Touch.PrimaryIndexTrigger, controller))
            {
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.Index;
            }
            else if (controllerState.indexTrigger <= 0f)
            {
                // TODO: Not sure if this is the correct way to do this
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.Pointing;
            }

            // Grip
            controllerState.handTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller);

            // Set ThumbUp if no other thumb-touch is set.
            // TODO: Not sure if this is the correct way to do this
            if ((controllerState.touchMask & (CAPI.ovrAvatar2Touch.One | CAPI.ovrAvatar2Touch.Two |
                                              CAPI.ovrAvatar2Touch.Joystick | CAPI.ovrAvatar2Touch.ThumbRest)) == 0)
            {
                controllerState.touchMask |= CAPI.ovrAvatar2Touch.ThumbUp;
            }
        }
    }
}
#endif // META_AVATAR_SDK_DEFINED
