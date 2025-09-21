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

// The Open XR Meta package does not compile for anything other than Android and Editor
#if OPEN_XR_META_2_1_OR_NEWER && (UNITY_ANDROID || UNITY_EDITOR)
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.XR;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.API;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.NativeTypes;
using Object = UnityEngine.Object;

namespace Meta.XR.EnvironmentDepth
{
    internal class DepthProviderOpenXR : IDepthProvider
    {
        private readonly XRDisplaySubsystem _displaySubsystem;
        private readonly MetaOpenXROcclusionSubsystem _occlusionSubsystem;
        private Dictionary<IntPtr, (uint textureId, RenderTexture renderTexture)> _depthTextures;
        private IntPtr? _prevNativeTexture;

        public DepthProviderOpenXR(XRDisplaySubsystem displaySubsystem, OpenXRLoader loader)
        {
            _displaySubsystem = displaySubsystem;
            _occlusionSubsystem = loader.GetLoadedSubsystem<XROcclusionSubsystem>() as MetaOpenXROcclusionSubsystem;
            if (_occlusionSubsystem == null)
            {
                Debug.LogError("Please enable 'Meta Quest: Occlusion' feature in 'Project Settings / XR Plug-in Management / OpenXR / OpenXR Feature Groups / Meta Quest'.");
            }
        }

        bool IDepthProvider.IsSupported => _occlusionSubsystem != null && _displaySubsystem != null;

        bool IDepthProvider.RemoveHands { set => SetHandRemovalEnabled(value); }

        void IDepthProvider.SetDepthEnabled(bool isEnabled, bool removeHands)
        {
            Assert.IsNotNull(_occlusionSubsystem);
            if (isEnabled)
            {
                _occlusionSubsystem.Start();
                if (removeHands)
                {
                    SetHandRemovalEnabled(true);
                }
            }
            else
            {
                if (_occlusionSubsystem.isHandRemovalSupported == Supported.Supported && _occlusionSubsystem.isHandRemovalEnabled)
                {
                    SetHandRemovalEnabled(false);
                }
                _occlusionSubsystem.Stop();

                if (_depthTextures != null)
                {
                    foreach (var depthTextureData in _depthTextures)
                    {
                        RenderTexture renderTexture = depthTextureData.Value.renderTexture;
                        if (renderTexture != null)
                        {
                            Object.Destroy(renderTexture);
                        }
                    }
                    _depthTextures = null;
                }
            }
        }

        private void SetHandRemovalEnabled(bool isEnabled)
        {
            Assert.IsNotNull(_occlusionSubsystem, nameof(_occlusionSubsystem));
            if (_occlusionSubsystem.isHandRemovalSupported == Supported.Supported)
            {
                var setHandRemovalResult = _occlusionSubsystem.TrySetHandRemovalEnabled(isEnabled);
                if (setHandRemovalResult != XrResult.Success)
                {
                    Debug.LogWarning($"MetaOpenXROcclusionSubsystem.TrySetHandRemovalEnabled() failed with result: {setHandRemovalResult}");
                }
            }
            else
            {
                Debug.LogWarning("Hand Removal is not supported.");
            }
        }

        bool IDepthProvider.TryGetUpdatedDepthTexture(out RenderTexture depthTexture, DepthFrameDesc[] frameDescriptors)
        {
            depthTexture = null;
            if (_depthTextures == null)
            {
                if (!_occlusionSubsystem.TryGetSwapchainTextureDescriptors(out var swapchainDescriptors))
                {
                    Debug.LogError("TryGetSwapchainTextureDescriptors() failed.");
                    return false;
                }

                var depthTextures = new Dictionary<IntPtr, (uint, RenderTexture)>(swapchainDescriptors.Length);
                foreach (var descriptors in swapchainDescriptors)
                {
                    Assert.AreEqual(1, descriptors.Length, nameof(descriptors));
                    var descriptor = descriptors[0];
                    Assert.AreNotEqual(IntPtr.Zero, descriptor.nativeTexture);
                    if (!UnityXRDisplay.CreateTexture(ToUnityXRRenderTextureDesc(descriptor), out var textureId))
                    {
                        Debug.LogError("UnityXRDisplay.CreateTexture() failed.");
                        return false;
                    }
                    depthTextures.Add(descriptor.nativeTexture, (textureId, null));
                }
                _depthTextures = depthTextures;
            }

            if (!_occlusionSubsystem.running ||
                !_displaySubsystem.running ||
                !_occlusionSubsystem.TryGetFrame(Allocator.Temp, out var frame) ||
                !frame.TryGetFovs(out var fovs) ||
                !frame.TryGetPoses(out var poses) ||
                !frame.TryGetNearFarPlanes(out var nearFarPlanes))
            {
                return false;
            }

            var textureDescriptors = _occlusionSubsystem.GetTextureDescriptors(Allocator.Temp);
            Assert.AreEqual(1, textureDescriptors.Length, nameof(textureDescriptors));
            var nativeTexture = textureDescriptors[0].nativeTexture;
            Assert.AreNotEqual(IntPtr.Zero, nativeTexture);
            if (_prevNativeTexture == nativeTexture)
            {
                return false;
            }
            _prevNativeTexture = nativeTexture;

            if (!_depthTextures.TryGetValue(nativeTexture, out var depthTextureData))
            {
                Debug.LogError($"Unknown native texture received from MetaOpenXROcclusionSubsystem.GetTextureDescriptors(): {nativeTexture}.");
                return false;
            }
            depthTexture = depthTextureData.renderTexture;
            if (depthTexture == null)
            {
                Assert.IsNotNull(_displaySubsystem, nameof(_displaySubsystem));
                depthTexture = _displaySubsystem.GetRenderTexture(depthTextureData.textureId);
                if (depthTexture == null)
                {
                    // Can fail if MetaOpenXROcclusionSubsystem is started/stopped quickly.
                    Debug.Log("XRDisplaySubsystem.GetRenderTexture() failed.");
                    return false;
                }
                _depthTextures[nativeTexture] = (depthTextureData.textureId, depthTexture);
            }

            for (int i = 0; i < frameDescriptors.Length; i++)
            {
                frameDescriptors[i] = new DepthFrameDesc
                {
                    createPoseLocation = poses[i].position,
                    createPoseRotation = poses[i].rotation,
                    fovLeftAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleLeft)),
                    fovRightAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleRight)),
                    fovTopAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleUp)),
                    fovDownAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleDown)),
                    nearZ = nearFarPlanes.nearZ,
                    farZ = nearFarPlanes.farZ
                };
            }
            return true;
        }

        /// see UnityEngine.XR.ARFoundation.UpdatableRenderTexture.ToUnityXRRenderTextureDesc
        private static UnityXRRenderTextureDesc ToUnityXRRenderTextureDesc(XRTextureDescriptor descriptor)
        {
            Assert.AreEqual(XRTextureType.DepthRenderTexture, descriptor.textureType);
            return new UnityXRRenderTextureDesc
            {
                shadingRateFormat = UnityXRShadingRateFormat.kUnityXRShadingRateFormatNone,
                shadingRate = new UnityXRTextureData(),
                width = (uint)descriptor.width,
                height = (uint)descriptor.height,
                textureArrayLength = (uint)descriptor.depth,
                flags = 0,
                colorFormat = UnityXRRenderTextureFormat.kUnityXRRenderTextureFormatNone,
                depthFormat = ToUnityXRDepthTextureFormat(descriptor.format),
                depth = new UnityXRTextureData { nativePtr = descriptor.nativeTexture }
            };
        }

        /// see UnityEngine.XR.ARFoundation.UpdatableRenderTexture.ToUnityXRDepthTextureFormat
        private static UnityXRDepthTextureFormat ToUnityXRDepthTextureFormat(TextureFormat textureFormat)
        {
            switch (textureFormat)
            {
                case TextureFormat.RFloat:
                    return UnityXRDepthTextureFormat.kUnityXRDepthTextureFormat24bitOrGreater;
                case TextureFormat.R16:
                case TextureFormat.RHalf:
                    return UnityXRDepthTextureFormat.kUnityXRDepthTextureFormat16bit;
                default:
                    throw new NotSupportedException(
                        $"Attempted to convert unsupported TextureFormat {textureFormat} to UnityXRDepthTextureFormat");
            }
        }
    }
}
#endif
