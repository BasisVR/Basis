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

        public readonly struct MetaOnlyLoadOutcome
        {
            public readonly BasisLoadableBundleWrapper Wrapper;
            public readonly bool IsTransient;

            public MetaOnlyLoadOutcome(BasisLoadableBundleWrapper wrapper, bool isTransient)
            {
                Wrapper = wrapper;
                IsTransient = isTransient;
            }
        }

        public static async Task<MetaOnlyLoadOutcome> CreateWrapperAndPerformMetaOnlyLoad(BasisDataStoreItemKeys.ItemKey item)
        {
            // make a new wrapper to load the metadata into
            BasisLoadableBundleWrapper newWrapper = CreateNewWrapperFromItem(item);

            // new report and CancellationSource source
            BasisProgressReport Report = new BasisProgressReport();
            CancellationTokenSource CancellationSource = new CancellationTokenSource();

            // perform the action to download the file or grab it from disc?
            BasisMetaLoadResult metaResult = await BasisBeeManagement.HandleMetaOnlyLoad(newWrapper.basisTrackedBundleWrapper, Report, CancellationSource.Token);

            // On transient (network/SSL/cancel) failure, do NOT fall through to LoadWrapperFromDisc —
            // that path auto-removes the key when IsMetaDataOnDisc returns false, which would delete
            // the user's cached item just because the remote is unreachable right now.
            if (!metaResult.Loaded && metaResult.IsTransient)
            {
                return new MetaOnlyLoadOutcome(null, true);
            }

            // grab the wrapper from disc, we can pass in our wrapper
            BasisLoadableBundleWrapper loaded = await LoadWrapperFromDisc(item, newWrapper);
            return new MetaOnlyLoadOutcome(loaded, false);
        }

        public readonly struct CacheNewItemResult
        {
            public readonly CachedContent Cached;
            public readonly bool IsTransient;

            public CacheNewItemResult(CachedContent cached, bool isTransient)
            {
                Cached = cached;
                IsTransient = isTransient;
            }
        }

        public static async Task<CacheNewItemResult> CacheNewItem(BasisDataStoreItemKeys.ItemKey item)
        {
            MetaOnlyLoadOutcome outcome = await CreateWrapperAndPerformMetaOnlyLoad(item);

            if (outcome.IsTransient)
            {
                return new CacheNewItemResult(null, true);
            }

            BasisLoadableBundleWrapper wrapper = outcome.Wrapper;
            if (wrapper == null)
            {
                BasisDebug.LogError("Missing Wrapper!, was the data provided correct?");
                return new CacheNewItemResult(null, false);
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

            return new CacheNewItemResult(cached, false);
        }


        public static async Task PreloadMetaDataForItem(BasisDataStoreItemKeys.ItemKey item)
        {
            if (item == null) return;

            var urlKey = item.Url ?? string.Empty;
            if (ContainsMetaData(urlKey)) return;

            try
            {
                CachedContent cached = null;
                bool isTransient = false;

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
                    CacheNewItemResult result = await CacheNewItem(item);
                    cached = result.Cached;
                    isTransient = result.IsTransient;
                }

                if (isTransient)
                {
                    // Remote unreachable (SSL / DNS / cancel). Leave the item in the library and try again later.
                    BasisDebug.LogWarning($"Deferred meta preload for '{urlKey}' — remote unreachable. Keeping cached item intact.");
                    return;
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
                string exMessage = ex.Message + " " + (ex.InnerException?.Message ?? string.Empty);
                if (BasisMetaLoadResult.LooksLikeTransientError(exMessage))
                {
                    BasisDebug.LogWarning($"Deferred meta preload for '{item?.Url}' — transient error: {ex.Message}");
                    return;
                }

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
