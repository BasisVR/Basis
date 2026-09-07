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
            public bool OwnsSprite;
            public string DateOfCreation;
            public string UniqueVersion;
            public string ContentGroupId;

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

        /// <summary>
        /// Drops one item's parsed connector so the next preload rebuilds it from disk. Used after
        /// the cached bee behind a url is invalidated — the entry here still describes the OLD
        /// bytes (name, thumbnail, creation date), so leaving it would keep showing the previous
        /// version on the card even though the payload was refreshed.
        /// </summary>
        public static void RemoveMetaData(string url)
        {
            if (string.IsNullOrEmpty(url) || !_metaCache.TryGetValue(url, out CachedContent meta))
            {
                return;
            }

            // Same ownership rule as ClearMetaDataCache: only sprites this cache created are ours
            // to destroy — an embedded item's sprite is owned by the addressable that supplied it.
            if (meta != null && meta.OwnsSprite && meta.CachedSprite != null)
            {
                Texture2D texture = meta.CachedSprite.texture;
                UnityEngine.Object.Destroy(meta.CachedSprite);
                if (texture != null) UnityEngine.Object.Destroy(texture);
                meta.CachedSprite = null;
                meta.OwnsSprite = false;
            }

            _metaCache.Remove(url);
        }

        public static void ClearMetaDataCache()
        {
            foreach (CachedContent meta in _metaCache.Values)
            {
                if (meta == null || !meta.OwnsSprite || meta.CachedSprite == null) continue;
                Texture2D texture = meta.CachedSprite.texture;
                UnityEngine.Object.Destroy(meta.CachedSprite);
                if (texture != null) UnityEngine.Object.Destroy(texture);
                meta.CachedSprite = null;
                meta.OwnsSprite = false;
            }
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
            meta.OwnsSprite = true;

            return meta.CachedSprite;
        }

        /// <summary>
        /// As <see cref="CreateSpriteFromMetaData"/> but the PNG decode/downscale runs off the main
        /// thread. <paramref name="urlKey"/> guards against the cache entry being cleared or
        /// replaced while the decode is in flight — a stale decode is discarded, never cached.
        /// </summary>
        public static async Task<Sprite> CreateSpriteFromMetaDataAsync(CachedContent meta, string urlKey)
        {
            if (meta == null) return null;

            if (meta.CachedSprite != null)
                return meta.CachedSprite;

            if (string.IsNullOrEmpty(meta.BasisBundleConnector.ImageBase64))
                return null;

            var tex = await BasisTextureCompression.FromPngBytesAsync(meta.BasisBundleConnector.ImageBase64);
            if (tex == null)
                return null;

            bool stillLive = _metaCache.TryGetValue(urlKey ?? string.Empty, out CachedContent live) && ReferenceEquals(live, meta);
            if (!stillLive || meta.CachedSprite != null)
            {
                UnityEngine.Object.Destroy(tex);
                return meta.CachedSprite;
            }

            meta.CachedSprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f)
            );
            meta.OwnsSprite = true;

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

            // Local BEE files are read straight from disk and never written to the on-disc meta cache,
            // so the LoadWrapperFromDisc lookup below would treat the missing cache entry as a corrupt
            // item and remove it. The connector is already populated by the meta-only load above.
            if (BasisIOManagement.TryResolveLocalBeePath(item.Url, out _))
            {
                return new MetaOnlyLoadOutcome(metaResult.Loaded ? newWrapper : null, false);
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
                ContentGroupId = connector?.BasisBundleDescription?.ContentGroupId,
                BasisBundleConnector = connector,
                BasisLoadableBundle = wrapper.BasisLoadableBundle,
            };

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

            if (!item.EmbeddedSettings.IsEmbedded && string.IsNullOrEmpty(item.Pass))
            {
                BasisDebug.LogError($"Item '{urlKey}' has no unlock password so it can never load. Removing from library.");
                await BasisDataStoreItemKeys.RemoveKey(item);
                return;
            }

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
                            string displayName = EmbeddedItems.GetDisplayNameForEmbeddedItem(item);
                            cached = new CachedContent
                            {
                                Name = displayName,
                                AssetBundleDescription = "Embedded Item",
                                CachedSprite = EmbeddedItems.GetSpriteForEmbeddedItem(item),
                                DateOfCreation = string.Empty,
                                UniqueVersion = string.Empty,
                                BasisBundleConnector = new BasisBundleConnector()
                                {
                                    BasisBundleDescription = new BasisBundleDescription()
                                    {
                                        AssetBundleName = displayName,
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
