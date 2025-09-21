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

using System.Collections.Generic;
using Meta.XR.Editor.Tags;
using Meta.XR.Editor.UserInterface;
using UnityEditor;

namespace Meta.XR.BuildingBlocks.Editor
{
    [InitializeOnLoad]
    internal static class CustomTagBehaviors
    {
        internal static HashSet<Tag> CollectionTags { get; } = new();

        // Collection tags
        private static Tag ImmersiveExperienceCollection { get; } = new Tag("Immersive Experience");
        private static Tag MixedRealityCollection { get; } = new Tag("Mixed Reality");
        private static Tag MultiplayerTabletop { get; } = new Tag("Tabletop Games");
        internal static Tag AllBuildingBlocksCollection { get; } = new Tag("All Building Blocks");

        // Feature tags
        private static Tag AudioTag { get; }
        private static Tag CoreTag { get; }
        private static Tag InteractionTag { get; }
        private static Tag PlatformTag { get; }
        private static Tag VoiceTag { get; }
        private static Tag AvatarsTag { get; }
        private static Tag PassthroughTag { get; }
        private static Tag SceneTag { get; }
        private static Tag SpatialAnchorTag { get; }
        private static Tag HapticsTag { get; }
        private static Tag MultiplayerFeatureTag { get; }

        // Usage tags
        private static Tag UtilityTag { get; }
        private static Tag PrototypeTag => Utils.PrototypingTag;
        private static Tag ExperimentalTag => Utils.ExperimentalTag;

        static CustomTagBehaviors()
        {
            // Collection tags
            SetDefaultCollectionTagBehavior(ImmersiveExperienceCollection,
                "Our list of fundational blocks designed to empower you to create engaging and dynamic VR applications. " +
                "Serving as the ideal starting point for building fully immersive environments and virtual interactions with both hands and controllers.",
                Styles.Contents.ImmersiveExperienceCollectionThumb);

            SetDefaultCollectionTagBehavior(MixedRealityCollection,
                "Seamlessly merge digital and physical worlds with features such as passthrough, " +
                "occlusion, and object placement. Explore this collection to create experiences where virtual and real elements coexist and interact.",
                Styles.Contents.MixedRealityCollectionThumb);

            SetDefaultCollectionTagBehavior(MultiplayerTabletop,
                "For engaging and shared experiences centered around a virtual tabletop, this collection " +
                "provides the necessary tools to enable social interactions and recreate the fun and camaraderie" +
                " of traditional tabletop gaming in a digital space.",
                Styles.Contents.TableTopCollectionThumb);

            SetDefaultCollectionTagBehavior(AllBuildingBlocksCollection,
                "Browse the entire catalog of Building Blocks and drag and drop any Meta Quest functionalities directly to your scene.",
                Styles.Contents.AllBlocksThumb);

            // Feature tags
            AudioTag = MakeFeatureTag("Audio", "sdk_icons/mono/audio_icon.png");
            CoreTag = MakeFeatureTag("Core", "sdk_icons/mono/core_icon.png");
            InteractionTag = MakeFeatureTag("Interaction", "sdk_icons/mono/interaction_icon.png");
            PlatformTag = MakeFeatureTag("Platform", "sdk_icons/mono/platform_icon.png");
            VoiceTag = MakeFeatureTag("Voice", "sdk_icons/mono/voice_icon.png");
            HapticsTag = MakeFeatureTag("Haptics", "sdk_icons/mono/haptics_icon.png");

            AvatarsTag = MakeFeatureTag("Avatars", "sdk_icons/mono/avatar_icon.png");
            PassthroughTag = MakeFeatureTag("Passthrough", "sdk_icons/mono/passthrough_icon.png");
            SceneTag = MakeFeatureTag("Scene", "sdk_icons/mono/scene_icon.png");
            SpatialAnchorTag = MakeFeatureTag("Spatial Anchor", "sdk_icons/mono/anchor_icon.png");
            MultiplayerFeatureTag = MakeFeatureTag("Multiplayer", "sdk_icons/mono/multiplayer_icon.png");

            // Usage tags
            UtilityTag = new Tag("Utility")
            {
                Behavior =
                {
                    Color = XR.Editor.UserInterface.Styles.Colors.UtilityColor,
                    Icon = TextureContent.CreateContent("ovr_icon_utilities.png", Utils.BuildingBlocksIcons),
                    Order = 100,
                    CanFilterBy = false,
                    ToggleableVisibility = true
                }
            };
        }

        private static Tag MakeFeatureTag(string tag, string iconPath)
        {
            return new Tag(tag)
            {
                Behavior =
                {
                    Order = -1,
                    Show = true,
                    CanFilterBy = true,
                    Icon = iconPath != null ? TextureContent.CreateContent(iconPath, TextureContent.Categories.Generic) : null
                }
            };
        }

        private static void SetDefaultCollectionTagBehavior(Tag tag, string description, TextureContent thumbnail)
        {
            CollectionTags.Add(tag);
            _ = new CollectionTagBehavior(tag)
            {
                Order = CollectionTagBehavior.DefaultSettings.Order,
                Color = CollectionTagBehavior.DefaultSettings.Color,
                Show = CollectionTagBehavior.DefaultSettings.Show,
                CanFilterBy = CollectionTagBehavior.DefaultSettings.CanFilterBy,
                ShowOverlay = CollectionTagBehavior.DefaultSettings.ShowOverlay,
                Description = description,
                Thumbnail = thumbnail,
                Automated = true
            };
        }
    }
}
