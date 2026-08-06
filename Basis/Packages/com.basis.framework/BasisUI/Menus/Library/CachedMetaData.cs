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

        public readonly struct MetaOnlyLoadOutcome
        {
            public readonly BasisLoadableBundleWrapper Wrapper;
            /// <summary>
            /// The load failed without positive evidence the content is bad, so the item was kept
            /// and nothing downstream may delete it. Named for what it authorizes, not for a guess
            /// at the cause — reading "is this transient?" as "may I delete this?" was the bug.
            /// </summary>
            public readonly bool Deferred;

            public MetaOnlyLoadOutcome(BasisLoadableBundleWrapper wrapper, bool deferred)
            {
                Wrapper = wrapper;
                Deferred = deferred;
            }
        }

        public static async Task<MetaOnlyLoadOutcome> CreateWrapperAndPerformMetaOnlyLoad(BasisDataStoreItemKeys.ItemKey item)
        {
            // make a new wrapper to load the metadata into
            BasisLoadableBundleWrapper newWrapper = CreateNewWrapperFromItem(item);

            // new report and CancellationSource source. Bounded and disposed: a deferred item is no
            // longer deleted after one attempt, so an unreachable host is now retried on every
            // library refresh and would otherwise hold a _preloadGate slot for the OS connect
            // timeout each time. Same budget BasisAvatarFarLOD already uses per attempt.
            BasisProgressReport Report = new BasisProgressReport();
            using CancellationTokenSource CancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // perform the action to download the file or grab it from disc?
            BasisMetaLoadResult metaResult = await BasisBeeManagement.HandleMetaOnlyLoad(newWrapper.basisTrackedBundleWrapper, Report, CancellationSource.Token);

            // Unless the load positively identified unusable bytes, do NOT fall through to
            // LoadWrapperFromDisc — that path auto-removes the key when IsMetaDataOnDisc returns
            // false, which would delete the user's saved item just because we could not fetch its
            // metadata right now.
            if (!metaResult.Loaded && !metaResult.IsCorrupt)
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
            /// <summary>See <see cref="MetaOnlyLoadOutcome.Deferred"/>. Keep the item.</summary>
            public readonly bool Deferred;

            public CacheNewItemResult(CachedContent cached, bool deferred)
            {
                Cached = cached;
                Deferred = deferred;
            }
        }

        public static async Task<CacheNewItemResult> CacheNewItem(BasisDataStoreItemKeys.ItemKey item)
        {
            MetaOnlyLoadOutcome outcome = await CreateWrapperAndPerformMetaOnlyLoad(item);

            if (outcome.Deferred)
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

            try
            {
                CachedContent cached = null;
                bool deferred = false;

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
                        // These two produce no CachedContent, and no reader ever looked at any bytes,
                        // so they must not fall through to the eviction branch below. Not knowing how
                        // to present an embedded item is our gap, not evidence the item is bad.
                        case BundledContentHolder.Mode.World:
                            BasisDebug.LogWarning($"CachedMetaData cannot determine {item.Url} with item.Mode = {item.Mode}");
                            deferred = true;
                            break;
                        default:
                            BasisDebug.LogWarning($"CachedMetaData cannot determine what to do with embedded item = {item.Url} with item.Mode = {item.Mode}");
                            deferred = true;
                            break;
                    }
                }
                else
                {
                    CacheNewItemResult result = await CacheNewItem(item);
                    cached = result.Cached;
                    deferred = result.Deferred;
                }

                if (deferred)
                {
                    // We could not get the metadata, but nothing told us the content is bad —
                    // unreachable host, refused URL, disk fault, cancellation. Leave the item in the
                    // library and try again on the next preload.
                    BasisDebug.LogWarning($"Deferred meta preload for '{urlKey}' — metadata unavailable. Keeping the library entry.");
                    return;
                }

                // Reached only when a load ran and did not defer. Note this branch is narrower than
                // its message suggests and always has been: a corrupt CACHED bee does not arrive
                // here, because LoadWrapperFromDisc finds the file still present and hands back the
                // placeholder wrapper CreateNewWrapperFromItem built, whose connector is non-null.
                // What actually lands here is a load that reported success without producing a
                // connector, or LoadWrapperFromDisc returning null because the meta cache has no
                // record. Evicting a genuinely unreadable cached bee is a separate gap.
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
                // An exception is never positive evidence that the user's content is bad. Every
                // throw reachable from here is ours — a locked or unreadable cache file, a cache
                // path not yet initialised, a cancellation, a null dereference. Deciding to delete
                // the item because the exception text did not happen to contain one of four words
                // is what destroyed people's libraries; the structural checks that CAN prove bad
                // bytes all return a classified failure instead of throwing, so nothing that
                // should be evicted stops being evicted by keeping the item here.
                BasisDebug.LogWarning($"Deferred meta preload for '{item?.Url}' — {ex.GetType().Name}: {ex.Message}. Keeping the library entry.");
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
