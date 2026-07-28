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
        public override int Order => 50; // after static items
        public override bool Hidden => false;

        private PanelButton _boundButton;
        private static bool _registryHooked;

        /// <summary>True while at least one instance spawned from this item is live.</summary>
        public bool IsSpawned => BasisRuntimeSpawnRegistry.CountIgnoreCase(_key.Url) > 0;

        public override void OnButtonCreated(PanelButton button)
        {
            _boundButton = button;
            RefreshIndicator();
        }

        private void RefreshIndicator()
        {
            if (_boundButton == null || _boundButton.ButtonStyling == null) return;
            _boundButton.ButtonStyling.SetIndicatorStyle(Styling.UiStyleButton.SpawnedIndicatorStyle);
            _boundButton.ButtonStyling.ShowIndicator(IsSpawned);
        }

        /// <summary>
        /// One process-wide hook refreshes every pinned button, rather than each provider
        /// subscribing — <see cref="RefreshPinnedProviders"/> replaces providers wholesale and
        /// per-instance subscriptions would leak the discarded ones.
        /// </summary>
        private static void EnsureRegistryHook()
        {
            if (_registryHooked) return;
            _registryHooked = true;
            BasisRuntimeSpawnRegistry.OnRegistryChanged += (_, __) => RefreshAllIndicators();
        }

        private static void RefreshAllIndicators()
        {
            var providers = BasisMenuBase<BasisMainMenu>.Providers;
            for (int Index = 0; Index < providers.Count; Index++)
            {
                if (providers[Index] is PinnedItemProvider pinned) pinned.RefreshIndicator();
            }
        }

        public override async void RunAction()
        {
            if (IsSpawned)
            {
                BasisDebug.Log($"Pinned Provider Despawn for item = {_key.Url}");
                await DespawnAll();
                RefreshIndicator();
                return;
            }

            // load / spawn / do whatever
            BasisDebug.Log($"Pinned Provider Action for item = {_key.Url}");
            await LibraryProvider.LoadSelectedItem(_key, _key.PinnedSettings.NetworkType, !_key.PinnedSettings.IsEphemeral);
            RefreshIndicator();
        }

        private async Task DespawnAll()
        {
            var instances = BasisRuntimeSpawnRegistry.GetInstances(_key.Url);
            var netIds = new List<string>(instances.Count);
            for (int Index = 0; Index < instances.Count; Index++)
            {
                if (instances[Index] != null) netIds.Add(instances[Index].LoadedNetID);
            }

            for (int Index = 0; Index < netIds.Count; Index++)
            {
                await BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(netIds[Index]);
            }
        }

        /// <summary>
        /// Used to Update the Pinned Item provider
        /// </summary>
        public static void RefreshPinnedProviders()
        {
            EnsureRegistryHook();

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