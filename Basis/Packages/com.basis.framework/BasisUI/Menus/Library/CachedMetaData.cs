using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;
using static Basis.BasisUI.LibraryProvider;

namespace Basis.BasisUI
{
    /// <summary>
    /// this class handles cached metadata for items in the library, such as the name, thumbnail, and other info that can be retrieved from the BEE file without fully loading the content. 
    /// This allows for faster filtering and sorting in the library UI without needing to load each item first.
    /// </summary>
    public static class CachedMetaData
    {
        // Represents a cached metadata entry for an item
        public class CachedContent
        {
            public string Name;
            public DateTime? Created;
            public string AssetBundleDescription;
            public Sprite CachedSprite;
            public string DateOfCreation;
            public string UniqueVersion;

            public BasisLoadableBundle BasisLoadableBundle;
            public BasisBundleConnector BasisBundleConnector;
        }

        private static readonly Dictionary<string, CachedContent> _metaCache = new();

        public static bool TryGetMeta(string url, out CachedContent meta)
        {
            return _metaCache.TryGetValue(url ?? string.Empty, out meta);
        }

        public static void SetMetaData(string url, CachedContent meta)
        {
            if (string.IsNullOrEmpty(url) || meta == null) return;
            _metaCache[url] = meta;
        }

        public static bool ContainsMetaData(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return _metaCache.ContainsKey(url);
        }

        public static void ClearMetaDataCache()
        {
            _metaCache.Clear();
        }

        public static Sprite CreateSpriteFromMetaData(CachedContent meta)
        {
            if (meta == null) return null;

            if (meta.CachedSprite != null)
                return meta.CachedSprite;

            if (string.IsNullOrEmpty(meta.BasisBundleConnector.ImageBase64))
                return null;

            var tex = BasisTextureCompression.FromPngBytes(meta.BasisBundleConnector.ImageBase64);
            if (tex == null)
                return null;

            meta.CachedSprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f)
            );

            return meta.CachedSprite;
        }

        public static async Task<BasisLoadableBundleWrapper> CreateWrapperAndPerformMetaOnlyLoad(BasisDataStoreItemKeys.ItemKey item)
        {
            // make a new wrapper to load the metadata into
            BasisLoadableBundleWrapper newWrapper = CreateNewWrapperFromItem(item);

            // new report and CancellationSource source
            BasisProgressReport Report = new BasisProgressReport();
            CancellationTokenSource CancellationSource = new CancellationTokenSource();

            // perform the action to download the file or grab it from disc?
            await BasisBeeManagement.HandleMetaOnlyLoad(newWrapper.basisTrackedBundleWrapper, Report, CancellationSource.Token);

            // grab the wrapper from disc, we can pass in our wrapper
            return await LoadWrapperFromDisc(item, newWrapper);
        }

        public static async Task<CachedContent> CacheNewItem(BasisDataStoreItemKeys.ItemKey item)
        {
            // grab the wrapper from disc, we can pass in our wrapper
            BasisLoadableBundleWrapper wrapper = await CreateWrapperAndPerformMetaOnlyLoad(item);//on disc call? 

            if (wrapper == null)
            {
                BasisDebug.LogError("Missing Wrapper!, was the data provided correct?");
                return null;
            }

            var connector = wrapper.BasisLoadableBundle.BasisBundleConnector; //wrapper.LoadableBundle.BasisBundleConnector;

            CachedContent cached = new CachedContent
            {
                Name = connector?.BasisBundleDescription?.AssetBundleName ?? string.Empty,
                AssetBundleDescription = connector?.BasisBundleDescription?.AssetBundleDescription,
                DateOfCreation = connector?.DateOfCreation,
                UniqueVersion = connector?.UniqueVersion,
                BasisBundleConnector = connector,
                BasisLoadableBundle = wrapper.BasisLoadableBundle,
            };

            // might as well cache the sprite now
            cached.CachedSprite = CreateSpriteFromMetaData(cached);

            string dateStrCache = connector?.DateOfCreation;
            if (!string.IsNullOrEmpty(dateStrCache) && DateTime.TryParse(dateStrCache, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
            {
                cached.Created = parsedDate;
            }

            return cached;
        }


        public static async Task PreloadMetaDataForItem(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null) return;

            var urlKey = item.Url ?? string.Empty;
            if (ContainsMetaData(urlKey)) return;

            try
            {
                CachedContent cached = null;

                if (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable)
                {
                    switch (item.Mode)
                    {
                        case BundledContentHolder.Mode.Avatar:
                        case BundledContentHolder.Mode.Prop:
                            cached = new CachedContent
                            {
                                Name = item.Url,
                                AssetBundleDescription = "Embedded Item",
                                CachedSprite = EmbeddedItems.GetSpriteForEmbeddedItem(item),
                                DateOfCreation = string.Empty,
                                UniqueVersion = string.Empty,
                                BasisBundleConnector = new BasisBundleConnector()
                                {
                                    BasisBundleDescription = new BasisBundleDescription()
                                    {
                                        AssetBundleName = item.Url,
                                        AssetBundleDescription = "Embedded Item"
                                    }
                                },
                                BasisLoadableBundle = null,
                            };
                            break;
                        case BundledContentHolder.Mode.World:
                            BasisDebug.LogWarning($"CachedMetaData cannot determine {item.Url} with item.Mode = {item.Mode}");
                            break;
                        default:
                            BasisDebug.LogWarning($"CachedMetaData cannot determine what to do with embedded item = {item.Url} with item.Mode = {item.Mode}");
                            break;
                    }
                }
                else
                {
                    cached = await CacheNewItem(item);
                }

                if (cached == null || cached.BasisBundleConnector == null)
                {
                    BasisDebug.LogError($"Item '{urlKey}' has corrupt or invalid data. Removing from library.");
                    BasisStorageManagement.DeleteStoredFile(urlKey);
                    await BasisDataStoreItemKeys.RemoveKey(item);
                    return;
                }

                SetMetaData(urlKey, cached);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to load metadata for '{item?.Url}'. Removing from library. Error: {ex.Message}");
                try
                {
                    BasisStorageManagement.DeleteStoredFile(item?.Url);
                    await BasisDataStoreItemKeys.RemoveKey(item);
                }
                catch (Exception cleanupEx)
                {
                    BasisDebug.LogError(cleanupEx);
                }
            }
        }
        [HideInCallstack]
        public static void LogError(Exception ex)
        {
            BasisDebug.LogError(ex);
        }
        private static readonly SemaphoreSlim _preloadGate = new SemaphoreSlim(4);

        public static async Task PreloadMetaForItems(IEnumerable<BasisDataStoreItemKeys.ItemKey> items)
        {
            if (items == null) return;

            try
            {
                await Task.WhenAll(items.Select(async item =>
                {
                    await _preloadGate.WaitAsync();
                    try
                    {
                        await PreloadMetaDataForItem(item);
                    }
                    finally
                    {
                        _preloadGate.Release();
                    }
                }));
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
        }
    }
}