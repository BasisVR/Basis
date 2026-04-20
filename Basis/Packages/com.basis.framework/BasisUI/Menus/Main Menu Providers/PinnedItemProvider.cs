using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Basis.Scripts.UI.UI_Panels;

namespace Basis.BasisUI
{
    public class PinnedItemProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        private BasisDataStoreItemKeys.ItemKey _key;
        private readonly string _title;
        private readonly string _iconAddress;

        public PinnedItemProvider(BasisDataStoreItemKeys.ItemKey item, CachedMetaData.CachedContent cachedItemData)
        {
            _key = item;
            _title = LibraryProviderStrUtil.TitleToCase(cachedItemData.BasisBundleConnector.BasisBundleDescription.AssetBundleName);
            _iconAddress = (item.EmbeddedSettings.IsEmbedded && item.EmbeddedSettings.SourceType == BasisDataStoreItemKeys.EmbeddedSource.Addressable) ? EmbeddedItems.GetAddressableSpriteForEmbeddedItem(item) : AddressableAssets.Sprites.Items;
        }

        public override string Title => _title; // or a nicer name
        public override string IconAddress => _iconAddress;
        public override int Order => 10; // after static items
        public override bool Hidden => false;

        public override async void RunAction()
        {
            // load / spawn / do whatever
            BasisDebug.Log($"Pinned Provider Action for item = {_key.Url}");
            await LibraryProvider.LoadSelectedItem(_key, _key.PinnedSettings.NetworkType, !_key.PinnedSettings.IsEphemeral);
        }

        /// <summary>
        /// Used to Update the Pinned Item provider
        /// </summary>
        public static void RefreshPinnedProviders()
        {
            var keys = BasisDataStoreItemKeys.DisplayKeys();

            var existing = new List<BasisMenuActionProvider<BasisMainMenu>>(
                BasisMenuBase<BasisMainMenu>.Providers);

            // Remove old pinned providers
            foreach (var provider in existing)
            {
                if (provider is PinnedItemProvider)
                {
                    BasisMenuBase<BasisMainMenu>.RemoveProvider(provider);
                }
            }

            // Add new ones
            foreach (var key in keys)
            {
                if (key.PinnedSettings.IsPinned)
                {
                    if (CachedMetaData.TryGetMeta(key.Url, out CachedMetaData.CachedContent cachedMeta))
                    {
                        var provider = new PinnedItemProvider(key, cachedMeta);
                        BasisMenuBase<BasisMainMenu>.AddProvider(provider);
                    }
                    else
                    {
                        BasisDebug.LogError($" Unable to build pinned provider for item = {key.Url} failed to get item from cache");
                    }
                }
            }
        }

    }
}