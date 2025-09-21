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
using UnityEngine.Events;

namespace Meta.XR.BuildingBlocks
{
    /// <summary>
    /// A helper class to receive Room Mesh load completion event.
    /// </summary>
    /// <remarks>
    /// This Unity component is part of Scene Mesh Building Blocks. Subscribe to this event to be notified when Room Mesh is loaded.
    /// The <see cref="MeshFilter"/> in the event payload contains the loaded mesh data.
    /// For more information on Scene Mesh, see [Scene Mesh](https://developer.oculus.com/documentation/unity/unity-scene-build-mixed-reality/#scene-mesh) in Using the Scene Model.
    /// Scene Mesh documentation</a>.
    /// </remarks>
    public class RoomMeshEvent : MonoBehaviour
    {
        /// <summary>
        /// An event to trigger when Room Mesh loads successfully.
        /// </summary>
        /// <remarks>
        /// In the event payload, <paramref>MeshFilter</paramref>, is the component that will contain the mesh data.
        /// </remarks>
        public UnityEvent<MeshFilter> OnRoomMeshLoadCompleted;
    }
}
