using System;
using System.Collections.Generic;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using UnityEngine.AddressableAssets;
using static Basis.Scripts.UI.UI_Panels.BasisDataStoreItemKeys;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Basis.BasisUI
{
    public static class EmbeddedItems
    {
        private const string CatalogAssetPath = "Packages/com.basis.sdk/Settings/EmbeddedItemsCatalog.asset";
        private const string CatalogAddress = "EmbeddedItemsCatalog";

        private static readonly ItemKey[] EmptyKeys = Array.Empty<ItemKey>();
        private static readonly BasisBounds DefaultBounds = new BasisBounds(Vector3.one, Vector3.zero);

        private static readonly Dictionary<string, EmbeddedItemDefinition> DefinitionByUrl = new Dictionary<string, EmbeddedItemDefinition>(StringComparer.Ordinal);

        private static EmbeddedItemsCatalogAsset _catalog;
        private static ItemKey[] _embeddedKeys = EmptyKeys;
        private static bool _loaded;

        public static EmbeddedItemsCatalogAsset Catalog
        {
            get
            {
                EnsureLoaded();
                return _catalog;
            }
        }

        public static ItemKey[] HardcodedKeys
        {
            get
            {
                EnsureLoaded();
                return _embeddedKeys;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

#if UNITY_EDITOR
            _catalog = AssetDatabase.LoadAssetAtPath<EmbeddedItemsCatalogAsset>(CatalogAssetPath);
#else
            if (AddressableAssets.AddressExists(CatalogAddress))
            {
                _catalog = Addressables.LoadAssetAsync<EmbeddedItemsCatalogAsset>(CatalogAddress).WaitForCompletion();
            }
#endif

            RebuildCache();
        }

        private static void RebuildCache()
        {
            DefinitionByUrl.Clear();

            if (_catalog == null || _catalog.Entries == null || _catalog.Entries.Count == 0)
            {
                _embeddedKeys = EmptyKeys;
                return;
            }

            var keys = new List<ItemKey>(_catalog.Entries.Count);

            foreach (EmbeddedItemDefinition entry in _catalog.Entries)
            {
                if (entry?.Key == null || string.IsNullOrEmpty(entry.Key.Url))
                {
                    continue;
                }

                if (!entry.Key.EmbeddedSettings.IsEmbedded)
                {
                    continue;
                }

                keys.Add(entry.Key);
                DefinitionByUrl[entry.Key.Url] = entry;
            }

            _embeddedKeys = keys.Count > 0 ? keys.ToArray() : EmptyKeys;
        }

        public static bool TryGetDefinition(ItemKey item, out EmbeddedItemDefinition definition)
        {
            EnsureLoaded();

            if (item == null || string.IsNullOrEmpty(item.Url))
            {
                definition = null;
                return false;
            }

            return DefinitionByUrl.TryGetValue(item.Url, out definition);
        }

        public static string GetAddressableSpriteForEmbeddedItem(ItemKey item)
        {
            if (!IsEmbeddedItem(item))
            {
                BasisDebug.LogError($"GetSpriteForEmbeddedItem() was invoked for item = {item?.Url} it is not embedded. Returning NULL.");
                return null;
            }

            if (TryGetDefinition(item, out EmbeddedItemDefinition definition) &&
                definition.HasCustomIconAddress &&
                !string.IsNullOrEmpty(definition.IconAddress))
            {
                return definition.IconAddress;
            }

            return AddressableAssets.Sprites.Items;
        }

        public static Sprite GetSpriteForEmbeddedItem(ItemKey item)
        {
            if (!IsEmbeddedItem(item))
            {
                BasisDebug.LogError($"GetSpriteForEmbeddedItem() was invoked for item = {item?.Url} it is not embedded. Returning NULL.");
                return null;
            }

            return AddressableAssets.GetSprite(GetAddressableSpriteForEmbeddedItem(item));
        }

        public static BasisBounds GetBoundsForEmbeddedItem(ItemKey item)
        {
            if (!IsEmbeddedItem(item))
            {
                BasisDebug.LogError($"GetBoundsForEmbeddedItem() was invoked for item = {item?.Url} it is not embedded. returning default BasisBounds({DefaultBounds})");
                return DefaultBounds;
            }

            if (TryGetDefinition(item, out EmbeddedItemDefinition definition) && definition.HasCustomBounds)
            {
                return definition.Bounds;
            }

            return DefaultBounds;
        }

        internal static Vector3 GetOffsetForEmbeddedItem(ItemKey item, Vector3 playerPosReference, Vector3 playerPosForwardReference)
        {
            if (!IsEmbeddedItem(item))
            {
                BasisDebug.LogError($"GetOffsetForEmbeddedItem() was invoked for item = {item?.Url} it is not embedded. returning playerPosReference + playerPosForwardReference * 0.5f!");
                return playerPosReference + playerPosForwardReference * 0.5f;
            }

            if (TryGetDefinition(item, out EmbeddedItemDefinition definition) && definition.HasCustomSpawnOffset)
            {
                return ApplyRelativeOffset(playerPosReference, playerPosForwardReference, definition.SpawnOffsetFromPlayerReference);
            }

            return playerPosReference + playerPosForwardReference * 0.5f;
        }

        private static bool IsEmbeddedItem(ItemKey item)
        {
            return item != null && item.EmbeddedSettings.IsEmbedded;
        }

        private static Vector3 ApplyRelativeOffset(Vector3 playerPosReference, Vector3 playerPosForwardReference, Vector3 relativeOffset)
        {
            Vector3 forward = playerPosForwardReference;
            if (forward.sqrMagnitude <= Mathf.Epsilon)
            {
                forward = Vector3.forward;
            }
            else
            {
                forward.Normalize();
            }

            Vector3 right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude <= Mathf.Epsilon)
            {
                right = Vector3.right;
            }
            else
            {
                right.Normalize();
            }

            return playerPosReference
                   + (right * relativeOffset.x)
                   + (Vector3.up * relativeOffset.y)
                   + (forward * relativeOffset.z);
        }
    }
}
