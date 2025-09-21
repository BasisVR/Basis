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

namespace Meta.XR.ImmersiveDebugger
{
    /// <summary>
    /// An example implementation of how a custom integration could be specified
    /// to override camera/controller used by Immersive Debugger.
    /// (use it if you're not using Standard OVRCameraRig as camera and controllers)
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class ExampleCustomIntegrationConfig : CustomIntegrationConfigBase
    {
        /// <summary>
        /// Indicates how a camera should be found in the application.
        /// This should be dynamically managed across scenes. If previous camera is destroyed,
        /// Immersive Debugger will call this function again to retrieve camera.
        /// </summary>
        /// <returns>The camera component Immersive Debugger is using to position panels</returns>
        public override Camera GetCamera()
        {
            return GameObject.Find("MainCamera").GetComponent<Camera>();
        }
    }
}

