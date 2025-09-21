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

/*
 If you're not using standard OVRCameraRig and controllers for Oculus Integration,
 to integrate with Immersive Debugger consider using a custom config.

 Make sure UseCustomIntegrationConfig option is enabled in settings.

 There are two ways of integrating it:
 1. Subscription based with the static CustomIntegrationConfig class, you can freely subscribe/unsubscribe anytime but harder to maintain.
 2. Implement a class of ICustomIntegrationConfig or [recommended] overriding CustomIntegrationConfigBase and put it in the settings slot.
 For the file in the settings slot, we'll automatically attach the monobehaviour in Immersive Debugger setup and use it.
*/
namespace Meta.XR.ImmersiveDebugger
{
    /// <summary>
    /// Subscribe to the delegate events to register custom config of the integration with Immersive Debugger,
    /// all should be only subscribed once for each scene.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public static class CustomIntegrationConfig
    {
        /// <summary>
        /// Delegate type for the GetCamera function
        /// </summary>
        public delegate Camera GetCameraDelegate();
        /// <summary>
        /// Delegate type for the GetLeftControllerTransformDelegate function
        /// </summary>
        public delegate Transform GetLeftControllerTransformDelegate();
        /// <summary>
        /// Delegate type for the GetRightControllerTransformDelegate function
        /// </summary>
        public delegate Transform GetRightControllerTransformDelegate();
        public static event GetCameraDelegate GetCameraHandler;

        /// <summary>
        /// Setup all the configs with provided ICustomIntegrationConfig so it's used by Immersive Debugger
        /// Note the config is only gonna be used if UseCustomIntegrationConfig is enabled in settings
        /// </summary>
        /// <param name="customConfig">The implementation of ICustomIntegrationConfig</param>
        public static void SetupAllConfig(ICustomIntegrationConfig customConfig)
        {
            GetCameraHandler += customConfig.GetCamera;
        }

        /// <summary>
        /// Remove the registered customConfig from Immersive Debugger
        /// </summary>
        /// <param name="customConfig">The implementation of ICustomIntegrationConfig</param>
        public static void ClearAllConfig(ICustomIntegrationConfig customConfig)
        {
            GetCameraHandler -= customConfig.GetCamera;
        }

        /// <summary>
        /// Get Camera of the current scene, could be null. Used for show panel in relation to
        /// the camera's pose in runtime if you're not using OVRCameraRig.
        /// </summary>
        public static Camera GetCamera()
        {
            return GetCameraHandler?.Invoke();
        }
    }

    /// <summary>
    /// Interface for the custom integration config, implement this to allow integrating with
    /// Immersive Debugger in a customized way. Currently only exposing customization of
    /// overriding camera and controllers. Might be expanded in the future.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public interface ICustomIntegrationConfig
    {
        public Camera GetCamera();
    }

    /// <summary>
    /// A Base class implementing <see cref="ICustomIntegrationConfig"/> which automatically
    /// setup/clear all the configurations in awake/destroy life cycle.
    /// This is intend to make it more convenient to use custom integration config with boiler plate code provided.
    /// Overriding this class following the ExampleCustomIntegrationConfig.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class CustomIntegrationConfigBase : MonoBehaviour, ICustomIntegrationConfig
    {
        private void Awake()
        {
            CustomIntegrationConfig.SetupAllConfig(this);
        }

        private void OnDestroy()
        {
            CustomIntegrationConfig.ClearAllConfig(this);
        }

        /// <summary>
        /// Indicates how a camera should be found in the application.
        /// This should be dynamically managed across scenes, if previous camera is destroyed,
        /// Immersive Debugger will call this function again to retrieve camera.
        /// </summary>
        /// <returns>The camera component Immersive Debugger is using to position panels</returns>
        public virtual Camera GetCamera()
        {
            return null;
        }
    }
}
