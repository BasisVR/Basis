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


namespace Meta.XR.ImmersiveDebugger
{
    public enum DebugGizmoType
    {
        None = 0,
        /// <summary>
        /// Accepting type: Pose<br/>
        /// Drawing an Axis from a Pose data.
        /// </summary>
        Axis = 1,
        /// <summary>
        /// Accepting type: Vector3<br/>
        /// Drawing a Point from a Vector3 data.
        /// </summary>
        Point,
        /// <summary>
        /// Accepting type: Tuple&lt;Vector3 start, Vector3 end&gt;<br/>
        /// Drawing a Line from two Vector3 data representing start/end of the line.
        /// </summary>
        Line,
        /// <summary>
        /// Accepting type: Vector3[]<br/>
        /// Drawing Lines from list of Vector3 data representing connected points of the lines.
        /// </summary>
        Lines,
        /// <summary>
        /// Accepting type: Tuple&lt;Pose pivot, float width, float height&gt;<br/>
        /// Drawing a Plane from the pivot, width and height.
        /// </summary>
        Plane,
        /// <summary>
        /// Accepting type: Tuple&lt;Vector3 center, float size&gt;<br/>
        /// Drawing a regular Cube from the center and size (width/height/depth are all the same).
        /// </summary>
        Cube,
        /// <summary>
        /// Accepting type: Tuple&lt;Pose pivot, float width, float height, float depth&gt;<br/>
        /// Drawing a box from the top-centered pivot and its width, height, depth lengths.<br/>
        /// Pivot is at the center of the top surface (like Scene Volume).
        /// </summary>
        TopCenterBox,
        /// <summary>
        /// Accepting type: Tuple&lt;Pose pivot, float width, float height, float depth&gt;<br/>
        /// Drawing a normal box from the pivot and its width, height, depth lengths.<br/>
        /// Pivot is at the mass center of the box.
        /// </summary>
        Box,
    }
}
