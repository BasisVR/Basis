using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using UnityEngine;

/// <summary>
/// Loads "load on boot" content (see <see cref="BasisPreloadContentStore"/>) at startup: the first
/// preloaded world becomes the initial world and any preloaded props respawn at the placed transform
/// they were saved with. Skipped when the app is auto-joining a server (the server provides the world).
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisBootContentLoader
{
    private static bool _attempted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (BasisLocalPlayer.PlayerReady)
        {
            TriggerLoad();
        }
        else
        {
            BasisLocalPlayer.OnLocalPlayerInitialized += TriggerLoad;
        }
    }

    private static void TriggerLoad()
    {
        BasisLocalPlayer.OnLocalPlayerInitialized -= TriggerLoad;
        if (_attempted) return;
        _attempted = true;
        _ = LoadPreloadContent();
    }

    private static async Task LoadPreloadContent()
    {
        try
        {
            if (ShouldSkipForServerSession())
            {
                return;
            }

            IReadOnlyList<BasisPreloadContentStore.PreloadEntry> entries = await BasisPreloadContentStore.GetAllAsync();
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            await BasisLoadHandler.EnsureInitializationComplete();

            List<BasisDataStoreItemKeys.ItemKey> items = entries.Select(ToItemKey).ToList();

            try
            {
                await CachedMetaData.PreloadMetaForItems(items);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Boot content meta preload failed: {ex}");
            }

            // Worlds first so the player is in the initial world before props spawn at their origin.
            bool loadedWorld = false;
            foreach (BasisPreloadContentStore.PreloadEntry entry in entries)
            {
                if (entry.Mode != BundledContentHolder.Mode.World) continue;

                if (loadedWorld)
                {
                    BasisDebug.LogWarning($"Skipping extra boot world {entry.Url}; only one initial world is loaded.");
                    continue;
                }

                try
                {
                    await ContentLoader.LoadWorld(ToItemKey(entry), BundledContentHolder.NetworkType.Local);
                    loadedWorld = true;
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Failed to load boot world {entry.Url}: {ex}");
                }
            }

            foreach (BasisPreloadContentStore.PreloadEntry entry in entries)
            {
                if (entry.Mode != BundledContentHolder.Mode.Prop) continue;

                try
                {
                    await LoadBootProp(entry);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Failed to load boot prop {entry.Url}: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Boot content load failed: {ex}");
        }
    }

    private static async Task LoadBootProp(BasisPreloadContentStore.PreloadEntry entry)
    {
        BasisDataStoreItemKeys.ItemKey item = ToItemKey(entry);

        // Restore the placed transform when we saved one — spawn directly so interactive
        // placement (raycast) is skipped and the prop lands exactly where it was put.
        if (entry.HasTransform &&
            CachedMetaData.TryGetMeta(entry.Url, out CachedMetaData.CachedContent cached) &&
            cached?.BasisBundleConnector != null)
        {
            await ContentLoader.HandleLoadGameObjectWithBundle(
                item,
                cached,
                BundledContentHolder.NetworkType.Local,
                entry.Position,
                entry.Rotation,
                entry.Scale,
                BasisDeviceManagement.Instance.transform,
                admin: false,
                modifyScale: true);
            return;
        }

        // No saved transform — fall back to spawning at the player origin, pinned so a prop that
        // asks to arrive in a hand or under interactive placement cannot hijack the boot restore.
        item.PlacementOverride = BasisPropSpawnPlacement.AtPlayerOrigin;
        await ContentLoader.LoadProp(item, BundledContentHolder.NetworkType.Local);
    }

    private static BasisDataStoreItemKeys.ItemKey ToItemKey(BasisPreloadContentStore.PreloadEntry entry)
    {
        return new BasisDataStoreItemKeys.ItemKey
        {
            Url = entry.Url,
            Pass = entry.Pass,
            Mode = entry.Mode,
            PlacementType = entry.PlacementType,
            PlacementOverride = entry.PlacementOverride,
        };
    }

    private static bool ShouldSkipForServerSession()
    {
        if (BasisNetworkConnection.LocalPlayerIsConnected) return true;
        if (BasisConnectionService.AutoConnectAttempted) return true;

        try
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (!string.IsNullOrEmpty(arg) && arg.StartsWith("--connection=", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }
}
