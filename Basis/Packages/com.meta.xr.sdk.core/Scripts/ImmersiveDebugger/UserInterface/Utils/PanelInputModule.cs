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


using System.Collections;
using System.Collections.Generic;
using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using Cursor = Meta.XR.ImmersiveDebugger.UserInterface.Generic.Cursor;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    /// <summary>
    /// Override of <see cref="OVRInputModule"/> which handles the case if there are more than one BaseInputModule on the same game object,
    /// It'll force process this input module for Immersive Debugger.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    internal class PanelInputModule : OVRInputModule
    {
        /// <summary>
        /// An internal stateful static flag that lets the PointerHandler knows events come from
        /// this input module.
        /// </summary>
        /// <remarks>This is a workaround to avoid disrupting existing event systems and input modules.</remarks>
        internal static bool Processing;

        private Interface _debugInterface;

        private OVRInput.Controller _controller;

        private static OVRPlugin.HandState _handState = new();

        // Registering of raycasters will be static, as PanelInputModule may not exists in the same scene and
        // at the same time than raycasters.
        private static readonly List<PanelRaycaster> _raycasters = new();

        public static void RegisterRaycaster(PanelRaycaster raycaster)
        {
            if (_raycasters.Contains(raycaster)) return;

            _raycasters.Add(raycaster);
        }

        public static void UnregisterRaycaster(PanelRaycaster raycaster)
        {
            if (!_raycasters.Contains(raycaster)) return;

            _raycasters.Remove(raycaster);
        }

        internal void SetDebugInterface(Interface debugInterface)
        {
            _debugInterface = debugInterface;
        }

        protected override void Awake()
        {
            // We are not calling the base as to avoid using the singleton pattern of OVRInputModule
            var gameObjectForRay = new GameObject("rayHelper");
            rayTransform = gameObjectForRay.transform;
            rayTransform.SetParent(transform);
        }

        /// <summary>
        /// Overriding ShouldActivateModule from <see cref="PointerInputModule"/> as always false.
        /// We don't want the EventSystem to call this input module on its own.
        /// </summary>
        /// <returns>Always false</returns>
        public override bool ShouldActivateModule()
        {
            // We want this input module to be always off.
            // This way it will never disrupt any of the possibly already existing input modules from the user.
            return false;
        }

        /// <summary>
        /// Overriding IsModuleSupported from <see cref="PointerInputModule"/> as always false.
        /// We don't want the EventSystem to call this input module on its own.
        /// </summary>
        /// <returns>Always false</returns>
        public override bool IsModuleSupported()
        {
            // We want this input module to be always off.
            // This way it will never disrupt any of the possibly already existing input modules from the user.
            return false;
        }

        private void Update()
        {
            if (_debugInterface && !_debugInterface.Visibility)
            {
                return; // not process the input module if the debug interface is not visible
            }

            Process(); // The Process is controlled internally by this module, and not by the event system.
        }

        private static IComparer<RaycastResult> _comparer = new RaycastComparer();

        // Implement the IComparer interface
        public class RaycastComparer : IComparer<RaycastResult>
        {
            public int Compare(RaycastResult lhs, RaycastResult rhs)
            {
                var lhsPanelRaycaster = lhs.module as PanelRaycaster;
                var rhsPanelRaycaster = rhs.module as PanelRaycaster;
                if (lhsPanelRaycaster != null &&
                    rhsPanelRaycaster != null &&
                    lhsPanelRaycaster.sortOrder != rhsPanelRaycaster.sortOrder)
                {
                    return rhsPanelRaycaster.sortOrder.CompareTo(lhsPanelRaycaster.sortOrder);
                }

                if (lhs.depth != rhs.depth && lhs.module.rootRaycaster == rhs.module.rootRaycaster)
                    return rhs.depth.CompareTo(lhs.depth);

                if (lhs.distance != rhs.distance)
                    return lhs.distance.CompareTo(rhs.distance);

                return lhs.index.CompareTo(rhs.index);
            }
        }

        private bool Raycast(PointerEventData data, out RaycastResult raycast)
        {
            foreach (var raycaster in _raycasters)
            {
                if (!raycaster.IsValid) continue;
                raycaster.RaycastOnRaycastableGraphics(data, m_RaycastResultCache);
            }

            m_RaycastResultCache.Sort(_comparer);

            raycast = FindFirstRaycast(m_RaycastResultCache);
            data.pointerCurrentRaycast = raycast;
            m_RaycastResultCache.Clear();

            return raycast.isValid;
        }

        private MouseState GetMouseStateFromRaycast(OVRInput.Controller controller, Transform rayOrigin)
        {
            if (m_Cursor) m_Cursor.SetCursorRay(rayOrigin);

            // Get the OVRRayPointerEventData reference
            GetPointerData(kMouseLeftId, out var leftData, true);
            leftData.Reset();

            leftData.worldSpaceRay = new Ray(rayOrigin.position, rayOrigin.forward);
            leftData.scrollDelta = GetExtraScrollDelta();
            leftData.button = PointerEventData.InputButton.Left;
            leftData.useDragThreshold = true;

            // Perform raycast to find intersections with world
            if (Raycast(leftData, out var raycast))
            {
                var raycaster = raycast.module as PanelRaycaster;
                // We're only interested in intersections from panel raycasters
                if (raycaster)
                {
                    // The Unity UI system expects event data to have a screen position
                    // so even though this raycast came from a world space ray we must get a screen
                    // space position for the camera attached to this raycaster for compatability
                    leftData.position = raycaster.GetScreenPosition(raycast);

                    // And we're only interested in intersections on RectTransforms
                    if (m_Cursor && raycast.gameObject.TryGetComponent(out RectTransform graphicRect))
                    {
                        // Set are gaze indicator with this world position and normal
                        var worldPos = raycast.worldPosition;
                        var normal = GetRectTransformNormal(graphicRect);
                        m_Cursor.SetCursorStartDest(rayOrigin.position, worldPos, normal);
                    }
                }
            }

            GetPointerData(kMouseRightId, out var rightData, true);
            CopyFromTo(leftData, rightData);
            rightData.button = PointerEventData.InputButton.Right;

            GetPointerData(kMouseMiddleId, out var middleData, true);
            CopyFromTo(leftData, middleData);
            middleData.button = PointerEventData.InputButton.Middle;

            var controllerState = ComputeControllerState(controller);
            if (m_Cursor is Cursor cursor)
            {
                cursor.SetClickState(controllerState);
            }

            m_MouseState.SetButtonState(PointerEventData.InputButton.Left,
                controllerState, leftData);
            m_MouseState.SetButtonState(PointerEventData.InputButton.Right,
                PointerEventData.FramePressState.NotChanged, rightData);
            m_MouseState.SetButtonState(PointerEventData.InputButton.Middle,
                PointerEventData.FramePressState.NotChanged, middleData);

            return m_MouseState;
        }

        /// <summary>
        /// Process this InputModule. It has a much simpler process than the original OVRInputModule
        /// As it only cares about the chosen rayTransform, proposed by the Interface itself.
        /// </summary>
        public override void Process()
        {
            // Because we are bypassing the event system in this process
            // We cannot fully rely on the event data sent throughout this processing.
            // So we'll use this additional stateful static variable to test if this comes from our
            // processing.
            Processing = true;

            // Update controller choice
            _controller = ChooseBestController(previousController: _controller);

            // Update our internal ray logic
            UpdateRayTransform(rayTransform, _controller);

            // Process it
            ProcessMouseEvent(GetMouseStateFromRaycast(_controller, rayTransform));

            // We still need to clear the list of objects hit
            _objectsHitThisFrame.Clear();

            Processing = false;
        }

        private static PointerEventData.FramePressState ComputeControllerState(OVRInput.Controller controller)
        {
            var button = RuntimeSettings.Instance.ClickButton;

            var pressed = OVRInput.GetDown(button, controller);
            var released = OVRInput.GetUp(button, controller);

            if (pressed && released)
                return PointerEventData.FramePressState.PressedAndReleased;

            if (pressed)
                return PointerEventData.FramePressState.Pressed;

            if (released)
                return PointerEventData.FramePressState.Released;

            return PointerEventData.FramePressState.NotChanged;
        }

        private static OVRInput.Controller ChooseBestController(OVRInput.Controller previousController)
        {
            var controller = previousController;

            // Decide which controller to use
            var leftController = OVRInput.GetActiveControllerForHand(OVRInput.Handedness.LeftHanded);
            var rightController = OVRInput.GetActiveControllerForHand(OVRInput.Handedness.RightHanded);

            if (controller == OVRInput.Controller.None ||
                (controller != leftController && controller != rightController))
            {
                // If the last controller was neither, we need at list to pick one
                if (rightController == OVRInput.Controller.None)
                {
                    // If only left exists, no choice
                    controller = leftController;
                }
                else if (leftController == OVRInput.Controller.None)
                {
                    // If only right exists, no choice
                    controller = rightController;
                }
                else
                {
                    // Both being valid, we choose by handedness
                    controller = OVRInput.GetDominantHand() == OVRInput.Handedness.LeftHanded
                        ? leftController
                        : rightController;
                }
            }

            // In case of pressing down any button, this controller will take priority
            if (controller != leftController && OVRInput.Get(OVRInput.Button.Any, leftController))
            {
                controller = leftController;
            }

            if (controller != rightController && OVRInput.Get(OVRInput.Button.Any, rightController))
            {
                controller = rightController;
            }

            if (controller == OVRInput.Controller.None)
            {
                // Last minute fallback, in case none are considered active
                // This was the previously existing behaviour
                controller = OVRInput.Controller.RTouch;
            }

            return controller;
        }

        private void UpdateRayTransform(Transform rayTransform, OVRInput.Controller controller)
        {
            var handToFetch = controller switch
            {
                OVRInput.Controller.LHand => OVRPlugin.Hand.HandLeft,
                OVRInput.Controller.RHand => OVRPlugin.Hand.HandRight,
                _ => OVRPlugin.Hand.None
            };

            if (handToFetch != OVRPlugin.Hand.None)
            {
                OVRPlugin.GetHandState(OVRPlugin.Step.Render, handToFetch, ref _handState);
            }

            var localPosition = controller switch
            {
                OVRInput.Controller.RHand => _handState.PointerPose.Position.FromFlippedZVector3f(),
                OVRInput.Controller.LHand => _handState.PointerPose.Position.FromFlippedZVector3f(),
                _ => OVRInput.GetLocalControllerPosition(controller)
            };

            var localRotation = controller switch
            {
                OVRInput.Controller.RHand => _handState.PointerPose.Orientation.FromFlippedZQuatf(),
                OVRInput.Controller.LHand => _handState.PointerPose.Orientation.FromFlippedZQuatf(),
                _ => OVRInput.GetLocalControllerRotation(controller)
            };

            var ovrPose = new OVRPose() { position = localPosition, orientation = localRotation };

            // The Pose we get from OVR is in the tracking space,
            // We'll need to convert to it to world space.
            ovrPose = ovrPose.ToWorldSpacePose(_debugInterface.Camera);

            rayTransform.SetPositionAndRotation(ovrPose.position, ovrPose.orientation);
        }
    }
}
