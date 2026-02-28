

using System;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;

namespace Basis.BasisUI
{
    public class EmbeddedItems
    {
        /// <summary>
        /// This is where you can define embedded items 
        /// for the moment the addressable assets exist for:
        /// Personal Mirror
        /// Photo Camera
        /// 
        /// These are defined here for the menu only so we can spawn them locally within the LibraryProvider.cs
        /// You can also define embedded items from a BEEUrl if desired
        /// </summary>
        public static ItemKey[] HardcodedKeys = new ItemKey[]
        {
            // Example entry (uncomment and edit):
            new ItemKey {
                Mode = BundledContentHolder.Mode.Prop,
                Url = "Personal Mirror",
                Pass = "",
                EmbeddedSettings = EmbeddedSettings.Addressable,
                PlacementType = BundledContentHolder.PlacementType.SpawnInFrontOfPlayer,
                PinnedSettings = PinnedSettings.Embedded,
            },
            new ItemKey {
                Mode = BundledContentHolder.Mode.Prop,
                Url = "Photo Camera",
                Pass = "",
                EmbeddedSettings = EmbeddedSettings.Addressable,
                PlacementType = BundledContentHolder.PlacementType.SpawnInFrontOfPlayer,
                PinnedSettings = PinnedSettings.Embedded,
            },

            // avatars
            new ItemKey
            {
                Mode = BundledContentHolder.Mode.Avatar,
                Url = "https://cdn.yewnyx.net/basis/2026-02-25/gymcat/c84a7ca0c5ce4912ab5584c2840036de20260225.BEE",
                Pass = "0da01980272efbe421b7ebd5d6831d657ed96f5e5317d2f88a6dfe9e7ef5ead8",
                EmbeddedSettings = EmbeddedSettings.BEEUrl,
            },
            new ItemKey
            {
                Mode = BundledContentHolder.Mode.Avatar,
                Url = "https://cdn.yewnyx.net/basis/2026-02-25/space/c74771ec2dd84ffda33d346ada25e39420260225.BEE",
                Pass = "878ac54af853e1a7d864520a34ae68e8b370cedd89a42c09cd016cf9eb41dbae",
                EmbeddedSettings = EmbeddedSettings.BEEUrl,
            },
            new ItemKey
            {
                Mode = BundledContentHolder.Mode.Avatar,
                Url = "https://cdn.yewnyx.net/basis/2026-02-25/space_noet/95d4459279004b39928bc292f11bbac020260225.BEE",
                Pass = "b6e0ac7b08a23fc1cb0b6a0103f26d1f3eff8c2889ffb5c8bdcaa17a538e58b8",
                EmbeddedSettings = EmbeddedSettings.BEEUrl,
            },
            new ItemKey
            {
                Mode = BundledContentHolder.Mode.Avatar,
                Url = "https://cdn.yewnyx.net/basis/2026-02-25/spaceman/f78971c1c553483a9bbe155d60f14d4e20260225.BEE",
                Pass = "091b548e5e036a2d1829b41f829a0dab3f30a41d4d01eb9822b316fedb2934dd",
                EmbeddedSettings = EmbeddedSettings.BEEUrl,
            },
            new ItemKey
            {
                Mode = BundledContentHolder.Mode.Avatar,
                Url = "https://cdn.yewnyx.net/basis/2026-02-25/yun/e60b159bfa9d49b1a7f4480d5999ee0320260225.BEE",
                Pass = "e250499a5a00ebe3ee0c5447ca41677e5a4c6d33d152fd77804a5963f384652e",
                EmbeddedSettings = EmbeddedSettings.BEEUrl,
            },
            new ItemKey
            {
                Mode = BundledContentHolder.Mode.Avatar,
                Url = "https://cdn.yewnyx.net/basis/2026-02-25/yun_noet/77414044b3514656bebdcca7420809d520260225.BEE",
                Pass = "dffe7a0b96cf6a8d23fcb72c03c625466a3f8bd0a14b826a6a017548b54f2105",
                EmbeddedSettings = EmbeddedSettings.BEEUrl,
            },

        };

        public static string GetAddressableSpriteForEmbeddedItem(ItemKey item)
        {
            if (!item.EmbeddedSettings.IsEmbedded)
            {
                BasisDebug.LogError($"GetSpriteForEmbeddedItem() was invoked for item = {item.Url} it is not embedded. Returning NULL.");
                return null;
            }

            switch (item.Url)
            {
                case "Photo Camera":
                    return AddressableAssets.Sprites.Camera;
                case "Personal Mirror":
                    return AddressableAssets.Sprites.Mirror;
                default:
                    BasisDebug.Log($"GetSpriteForEmbeddedItem() item = {item.Url} does not have a specified icon defined. please define it, here.");
                    return AddressableAssets.Sprites.Items;
            }
        }

        /// <summary>
        /// returns the sprite of a item key that is embedded
        /// </summary>
        public static Sprite GetSpriteForEmbeddedItem(ItemKey item)
        {
            if (!item.EmbeddedSettings.IsEmbedded)
            {
                BasisDebug.LogError($"GetSpriteForEmbeddedItem() was invoked for item = {item.Url} it is not embedded. Returning NULL.");
                return null;
            }

            switch (item.Url)
            {
                case "Photo Camera":
                    return AddressableAssets.GetSprite(GetAddressableSpriteForEmbeddedItem(item));
                case "Personal Mirror":
                    return AddressableAssets.GetSprite(GetAddressableSpriteForEmbeddedItem(item));
                default:
                    BasisDebug.Log($"GetSpriteForEmbeddedItem() item = {item.Url} does not have a specified icon defined. please define it, here.");
                    break;
            }

            return AddressableAssets.GetSprite(GetAddressableSpriteForEmbeddedItem(item));
        }


        /// <summary>
        /// returns returns the bounds for the embedded item must be defined
        /// </summary>
        public static BasisBounds GetBoundsForEmbeddedItem(ItemKey item)
        {
            BasisBounds defaultBounds = new BasisBounds(Vector3.one, Vector3.zero);
            if (!item.EmbeddedSettings.IsEmbedded)
            {
                BasisDebug.LogError($"GetBoundsForEmbeddedItem() was invoked for item = {item.Url} it is not embedded. returning default BasisBounds({defaultBounds})");
                return defaultBounds;
            }

            switch (item.Url)
            {
                case "Photo Camera":
                    return new BasisBounds(new Vector3(0.25f, 0.15f, 0.1f), Vector3.zero);
                case "Personal Mirror":
                    return new BasisBounds(new Vector3(0.5f, 0.75f, 0.1f), Vector3.zero);
                default:
                    BasisDebug.Log($"GetBoundsForEmbeddedItem() item = {item.Url} does not have specified bounds defined. please define it, here.");
                    break;
            }

            return defaultBounds;
        }

        /// <summary>
        /// returns the offset for an embedded item when spawned with BundledContentHolder.PlacementType.SpawnInFrontOfPlayer
        /// </summary>
        internal static Vector3 GetOffsetForEmbeddedItem(ItemKey item, Vector3 playerPosReference, Vector3 playerPosForwardReference)
        {
            if (!item.EmbeddedSettings.IsEmbedded)
            {
                BasisDebug.LogError($"GetOffsetForEmbeddedItem() was invoked for item = {item.Url} it is not embedded. returning playerPosReference + playerPosForwardReference * 0.5f!");
                return playerPosReference + playerPosForwardReference * 0.5f;
            }

            switch (item.Url)
            {
                case "Photo Camera":
                    return playerPosReference + playerPosForwardReference * 0.5f;
                case "Personal Mirror":
                    return playerPosReference + playerPosForwardReference * 0.5f;
                default:
                    BasisDebug.Log($"GetOffsetForEmbeddedItem() item = {item.Url} does not have specified offset defined. please define it, here.");
                    break;
            }

            return playerPosReference;
        }
    }
}
