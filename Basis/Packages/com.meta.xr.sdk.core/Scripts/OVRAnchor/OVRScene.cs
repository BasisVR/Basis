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

using Meta.XR.Util;
using static OVRPlugin;

/// <summary>
/// Low-level functionality related to Scene Understanding.
/// </summary>
/// <remarks>
/// Scene empowers you to quickly build complex and scene-aware experiences with rich interactions in the user’s
/// physical environment.
///
/// See [Unity Scene Overview](https://developer.oculus.com/documentation/unity/unity-scene-overview) for more details.
/// </remarks>
[Feature(Feature.Scene)]
public static partial class OVRScene
{
    /// <summary>
    /// Requests Space Setup.
    /// </summary>
    /// <remarks>
    /// Requests [Space Setup](https://developer.oculus.com/documentation/unity/unity-scene-overview/#how-does-scene-work).
    /// Space Setup pauses the application and prompts the user to setup their Space. The app resumes when the user
    /// either cancels or completes Space Setup.
    ///
    /// This method is asynchronous. The result of the task indicates whether the operation was successful. `False`
    /// usually indicates an unexpected failure; if the user cancels Space Setup, the operation still completes
    /// successfully.
    /// </remarks>
    /// <returns>A task that can be used to track the asynchronous operation.</returns>
    public static OVRTask<bool> RequestSpaceSetup() => OVRTask
        .Build(RequestSceneCapture(out var requestId), requestId)
        .ToTask(failureValue: false);
}
