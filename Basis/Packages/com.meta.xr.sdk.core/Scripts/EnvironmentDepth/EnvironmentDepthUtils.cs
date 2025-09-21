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

namespace Meta.XR.EnvironmentDepth
{
    internal static class EnvironmentDepthUtils
    {
        private static readonly Vector3 _scalingVector3 = new(1, 1, -1);

        internal static Vector4 ComputeNdcToLinearDepthParameters(float near, float far)
        {
            float invDepthFactor;
            float depthOffset;
            if (far < near || float.IsInfinity(far))
            {
                // Inf far plane:
                invDepthFactor = -2.0f * near;
                depthOffset = -1.0f;
            }
            else
            {
                // Finite far plane:
                invDepthFactor = -2.0f * far * near / (far - near);
                depthOffset = -(far + near) / (far - near);
            }

            return new Vector4(invDepthFactor, depthOffset, 0, 0);
        }

        internal static Matrix4x4 CalculateReprojection(DepthFrameDesc frameDesc)
        {
            CalculateDepthCameraMatrices(frameDesc, out var proj, out var view);
            return proj * view;
        }

        internal static void CalculateDepthCameraMatrices(DepthFrameDesc frameDesc, out Matrix4x4 projMatrix, out Matrix4x4 viewMatrix)
        {
            float left = frameDesc.fovLeftAngleTangent;
            float right = frameDesc.fovRightAngleTangent;
            float bottom = frameDesc.fovDownAngleTangent;
            float top = frameDesc.fovTopAngleTangent;
            float near = frameDesc.nearZ;
            float far = frameDesc.farZ;

            float x = 2.0F / (right + left);
            float y = 2.0F / (top + bottom);
            float a = (right - left) / (right + left);
            float b = (top - bottom) / (top + bottom);
            float c;
            float d;
            if (float.IsInfinity(far))
            {
                c = -1.0F;
                d = -2.0f * near;
            }
            else
            {
                c = -(far + near) / (far - near);
                d = -(2.0F * far * near) / (far - near);
            }
            float e = -1.0F;
            projMatrix = new Matrix4x4
            {
                m00 = x,
                m01 = 0,
                m02 = a,
                m03 = 0,
                m10 = 0,
                m11 = y,
                m12 = b,
                m13 = 0,
                m20 = 0,
                m21 = 0,
                m22 = c,
                m23 = d,
                m30 = 0,
                m31 = 0,
                m32 = e,
                m33 = 0

            };

            viewMatrix = Matrix4x4.TRS(frameDesc.createPoseLocation, frameDesc.createPoseRotation, _scalingVector3).inverse;
        }
    }
}
