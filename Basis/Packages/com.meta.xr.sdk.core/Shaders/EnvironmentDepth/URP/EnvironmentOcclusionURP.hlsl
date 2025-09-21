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

#ifndef META_DEPTH_ENVIRONMENT_OCCLUSION_URP_INCLUDED
#define META_DEPTH_ENVIRONMENT_OCCLUSION_URP_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Redefining macro to avoid shader warnings.
#ifdef PREFER_HALF
#undef  PREFER_HALF
#endif
#define PREFER_HALF 0

#define SHADER_HINT_NICE_QUALITY 1

TEXTURE2D_X_FLOAT(_EnvironmentDepthTexture);

SAMPLER(sampler_EnvironmentDepthTexture);
float4 _EnvironmentDepthTexture_TexelSize;

TEXTURE2D_ARRAY_FLOAT(_PreprocessedEnvironmentDepthTexture);
SAMPLER(sampler_PreprocessedEnvironmentDepthTexture);
float4 _PreprocessedEnvironmentDepthTexture_TexelSize;

float SampleEnvironmentDepth(const float2 reprojectedUV) {
  return SAMPLE_TEXTURE2D_X(_EnvironmentDepthTexture, sampler_EnvironmentDepthTexture, reprojectedUV).r;
}

#define META_DEPTH_CONVERT_OBJECT_TO_WORLD(objectPos) TransformObjectToWorld(objectPos).xyz

float DepthConvertDepthToLinear(float zspace) {
  return LinearEyeDepth(zspace, _ZBufferParams);
}

float4 SamplePreprocessedDepth(float2 uv, float slice) {
  return _PreprocessedEnvironmentDepthTexture.Sample(sampler_PreprocessedEnvironmentDepthTexture, float3(uv.x, uv.y, slice));
}

#include "../EnvironmentOcclusion.cginc"

#endif
