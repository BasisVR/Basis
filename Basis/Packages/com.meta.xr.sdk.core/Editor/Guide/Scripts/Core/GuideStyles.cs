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

using Meta.XR.Editor.UserInterface;
using UnityEngine;

namespace Meta.XR.Guides.Editor
{
    internal static class GuideStyles
    {
        public static class Contents
        {
            public static readonly TextureContent HeaderIcon =
                TextureContent.CreateContent("meta_icon.png", Utils.GuidedAccountSetupIcons, null);

            public static readonly TextureContent DefaultIcon =
                TextureContent.CreateContent("ovr_bullet.png", Utils.GuidedAccountSetupIcons);

            public static readonly TextureContent StatusIcon =
                TextureContent.CreateContent("ovr_status.png", Utils.GuidedAccountSetupIcons);

            public static readonly TextureContent SuccessIcon =
                TextureContent.CreateContent("ovr_success.png", Utils.GuidedAccountSetupIcons);

            public static readonly TextureContent InfoIcon =
                TextureContent.CreateContent("ovr_info.png", Utils.GuidedAccountSetupIcons);

            public static readonly TextureContent BannerImage =
                TextureContent.CreateContent("ovr_banner.png", Utils.GuidedAccountSetupTextures);

            public static readonly TextureContent MetaCoreSDKHeaderImage =
                TextureContent.CreateContent("ovr_develop.jpg", Utils.GuidedAccountSetupTextures);

            public static readonly TextureContent DeveloperOculusCom =
                TextureContent.CreateContent("ovr_develop_01.jpg", Utils.GuidedAccountSetupTextures);

            public static readonly TextureContent BuildingBlocks =
                TextureContent.CreateContent("ovr_develop_02.jpg", Utils.GuidedAccountSetupTextures);

            public static readonly TextureContent MetaQuestDeveloperHub =
                TextureContent.CreateContent("ovr_develop_03.jpg", Utils.GuidedAccountSetupTextures);
        }

        public static class Constants
        {
            public const int DefaultWidth = 520;
            public const int DefaultHeight = 480;
            public const float ImageWidth = 256f;
            public const float ImageHeight = 150f;
            public const int ImageBorderWidth = 1;
            public const int DefaultHeaderHeight = 84;
            public const float BorderRadius = 4.0f;
            public static Vector4 RoundedBorderVectors = new Vector4(BorderRadius, BorderRadius, BorderRadius, BorderRadius);
        }
    }
}
