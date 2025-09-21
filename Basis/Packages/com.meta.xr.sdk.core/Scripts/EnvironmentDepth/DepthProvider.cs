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

#if XR_OCULUS_4_2_0_OR_NEWER
#if UNITY_2022_3_OR_NEWER && !UNITY_2023_1
#define IS_SUPPORTED_DEPTH_API_UNITY_VERSION
#endif
using Unity.XR.Oculus;
using UnityEngine;
using UnityEngine.XR;

namespace Meta.XR.EnvironmentDepth
{
    internal class DepthProvider : IDepthProvider
    {
        private readonly XRDisplaySubsystem _displaySubsystem;
        private uint? _prevTextureId;

        public DepthProvider(XRDisplaySubsystem displaySubsystem)
        {
            _displaySubsystem = displaySubsystem;
#if !IS_SUPPORTED_DEPTH_API_UNITY_VERSION
            Debug.LogError("DepthAPI requires at least Unity 2022.3.0f");
#endif
        }

        bool IDepthProvider.IsSupported => Utils.GetEnvironmentDepthSupported();

        bool IDepthProvider.RemoveHands { set => Utils.SetEnvironmentDepthHandRemoval(value); }

        void IDepthProvider.SetDepthEnabled(bool isEnabled, bool removeHands)
        {
            if (isEnabled)
            {
                Utils.SetupEnvironmentDepth(new Utils.EnvironmentDepthCreateParams { removeHands = removeHands });
                Utils.SetEnvironmentDepthRendering(true);
            }
            else
            {
                Utils.ShutdownEnvironmentDepth();
                Utils.SetEnvironmentDepthRendering(false);
            }
        }

        bool IDepthProvider.TryGetUpdatedDepthTexture(out RenderTexture depthTexture, DepthFrameDesc[] frameDescriptors)
        {
            depthTexture = null;
            uint textureId = 0;
            if (!_displaySubsystem.running || !Utils.GetEnvironmentDepthTextureId(ref textureId))
            {
                return false;
            }

            if (_prevTextureId == textureId)
            {
                return false;
            }
            _prevTextureId = textureId;

#if IS_SUPPORTED_DEPTH_API_UNITY_VERSION
            depthTexture = _displaySubsystem.GetRenderTexture(textureId);
#endif
            for (int i = 0; i < frameDescriptors.Length; i++)
            {
                frameDescriptors[i] = GetFrameDesc(i);
            }
            return true;
        }

        private static DepthFrameDesc GetFrameDesc(int eye)
        {
            var d = Utils.GetEnvironmentDepthFrameDesc(eye);
            return new DepthFrameDesc
            {
                createPoseLocation = d.createPoseLocation,
                createPoseRotation = new Quaternion(d.createPoseRotation.x, d.createPoseRotation.y, d.createPoseRotation.z, d.createPoseRotation.w),
                fovLeftAngleTangent = d.fovLeftAngle,
                fovRightAngleTangent = d.fovRightAngle,
                fovTopAngleTangent = d.fovTopAngle,
                fovDownAngleTangent = d.fovDownAngle,
                nearZ = d.nearZ,
                farZ = d.farZ
            };
        }
    }
}
#endif // XR_OCULUS_4_2_0_OR_NEWER
