using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace Basis
{
    public static class BasisRuntimeSpawnRegistry
    {
        public enum RegistryChangeType : byte
        {
            Added = 0,
            Removed = 1,
            ClearedUrl = 2,
            ClearedAll = 3,
            Modified = 4
        }

        public static event Action<RegistryChangeType, SpawnInstance> OnRegistryChanged;

        private static void RaiseChanged(RegistryChangeType type, SpawnInstance instance)
        {
            OnRegistryChanged?.Invoke(type, instance);
        }

        public enum SpawnMode : byte
        {
            GameObject = 0,
            Scene = 1,
            Avatar = 2,
        }

        public enum SpawnMethod : byte
        {
            Embedded = 0,
            Local = 1,
            Network = 2,
        }

        [Serializable]
        public class SpawnInstance
        {
            public SpawnMode SpawnMode;          // GameObject / Scene / Avatar
            public SpawnMethod SpawnMethod;      // Embedded / Local / Network
            public string InstanceId;       // unique per spawn (GUID)
            public string Url;              // original spawn URL / key
            public string LoadedNetID;      // what you pass to RequestGameObjectUnLoad
            public string UUIDOfCreator; // reference to the creator of the spawn entity
            public bool isProtected; // this determines if the item is admin protected
            public bool Persistent;
            public bool Static; // server-authoritative "static / locked" flag: pickup disabled + frozen (prop) or locked out (vehicle)
            public bool StaticAdminLocked; // admin tier of the static lock: only a moderator (not the creator) can change it
            public DateTime SpawnedUtc;
            public BasisBundleConnector bundleConnector; // metadata for the spawned entity, assume it to be null when not present.
        }

        // LoadedNetID -> spawned thing (runtime references)
        // Note: Unity objects are NOT thread-safe; ConcurrentDictionary is ok for structure,
        // but only touch GameObjects/Scenes on the main thread.
        public static readonly ConcurrentDictionary<string, GameObject> SpawnedGameobjects = new();
        public static readonly ConcurrentDictionary<string, Scene> SpawnedScenes = new();

        // URL -> instances (grouping / querying)
        private static readonly Dictionary<string, List<SpawnInstance>> _map = new();

        // LoadedNetID -> instance (fast removal / lookup)
        private static readonly Dictionary<string, SpawnInstance> _byNetId = new();

        public static bool HasAny(string url)
            => !string.IsNullOrWhiteSpace(url)
               && _map.TryGetValue(url, out var list)
               && list != null
               && list.Count > 0;

        public static IReadOnlyList<SpawnInstance> GetInstances(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return Array.Empty<SpawnInstance>();
            return _map.TryGetValue(url, out var list) && list != null ? list : Array.Empty<SpawnInstance>();
        }

        public static int Count(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;
            return _map.TryGetValue(url, out var list) && list != null ? list.Count : 0;
        }

        public static int CountIgnoreCase(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;
            var key = _map.Keys.FirstOrDefault(k => string.Equals(k, url, StringComparison.OrdinalIgnoreCase));
            return key != null && _map[key] != null ? _map[key].Count : 0;
        }

        // ---- ADD HELPERS (also set the runtime dictionaries) --------------------

        public static void AddGameObject(
            string url,
            string loadedNetId,
            GameObject go,
            string creatorUUID,
            bool admin,
            bool persistent,
            SpawnMethod method,
            BasisBundleConnector basisBundleConnector,
            out SpawnInstance instance)
        {
            if (go == null) throw new ArgumentNullException(nameof(go));
            AddInternal(url, loadedNetId, creatorUUID, admin, persistent, method, SpawnMode.GameObject, basisBundleConnector, out instance);

            // keep runtime ref
            SpawnedGameobjects[loadedNetId] = go;
        }

        public static void AddScene(
            string url,
            string loadedNetId,
            Scene scene,
            string creatorUUID,
            bool admin,
            bool persistent,
            SpawnMethod method,
            BasisBundleConnector basisBundleConnector,
            out SpawnInstance instance)
        {
            if (!scene.IsValid()) throw new ArgumentException("Scene is not valid.", nameof(scene));
            AddInternal(url, loadedNetId, creatorUUID, admin, persistent, method, SpawnMode.Scene, basisBundleConnector, out instance);

            // keep runtime ref
            SpawnedScenes[loadedNetId] = scene;
        }

        // Backwards/compat entry point (no runtime object set)
        public static void Add(
            string url,
            string loadedNetId,
            string creatorUUID,
            bool admin,
            bool persistent,
            SpawnMethod method,
            SpawnMode mode,
            BasisBundleConnector bundleConnector,
            out SpawnInstance instance)
        {
            AddInternal(url, loadedNetId, creatorUUID, admin, persistent, method, mode, bundleConnector, out instance);
        }

        private static void AddInternal(
            string url,
            string loadedNetId,
            string creatorUUID,
            bool admin,
            bool persistent,
            SpawnMethod method,
            SpawnMode mode,
            BasisBundleConnector bundleConnector,
            out SpawnInstance instance)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL cannot be null/empty.", nameof(url));
            if (string.IsNullOrWhiteSpace(loadedNetId)) throw new ArgumentException("LoadedNetID cannot be null/empty.", nameof(loadedNetId));

            if (!_map.TryGetValue(url, out var list) || list == null)
            {
                list = new List<SpawnInstance>();
                _map[url] = list;
            }

            instance = new SpawnInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                Url = url,
                LoadedNetID = loadedNetId,
                UUIDOfCreator = creatorUUID,
                isProtected = admin,
                Persistent = persistent,
                SpawnedUtc = DateTime.UtcNow,
                SpawnMethod = method,
                SpawnMode = mode,
                bundleConnector = bundleConnector
            };

            list.Add(instance);

            // uniqueness expected
            _byNetId[loadedNetId] = instance;

            // raise a changed event that we added something
            RaiseChanged(RegistryChangeType.Added, instance);
        }

        public static async Task<bool> RemoveByLoadedNetId(string loadedNetId)
        {
            if (string.IsNullOrWhiteSpace(loadedNetId)) return false;
            if (!_byNetId.TryGetValue(loadedNetId, out var inst) || inst == null) return false;

            // Despawn first (main thread!)
            switch (inst.SpawnMode)
            {
                case SpawnMode.GameObject:
                case SpawnMode.Avatar: // treat avatar as GO unless you add a separate avatar store
                    {
                        if (SpawnedGameobjects.TryGetValue(loadedNetId, out var go) && go != null)
                        {
                            if (inst.SpawnMethod == SpawnMethod.Embedded)
                            {
                                Addressables.ReleaseInstance(go);
                            }
                            else
                            {
                                GameObject.Destroy(go);
                            }
                        }

                        break;
                    }
                case SpawnMode.Scene:
                    {
                        if (SpawnedScenes.TryGetValue(loadedNetId, out var scene) && scene.IsValid())
                        {
                            /*
                            if (inst.SpawnMethod == SpawnMethod.Embedded)
                            {
                                Addressables.UnloadSceneAsync(scene, true);
                            }
                            else
                            {
                                SceneManager.UnloadSceneAsync(scene);
                            }
                            */
                            if (scene.IsValid() && scene.isLoaded)
                            {
                                await SceneManager.UnloadSceneAsync(scene);
                            }
                        }

                        break;
                    }
                default:
                    break;
            }

            // Now remove bookkeeping + runtime refs
            return RemoveByLoadedNetId_RegistryOnly(loadedNetId);
        }
        private static bool RemoveByLoadedNetId_RegistryOnly(string loadedNetId)
        {
            if (string.IsNullOrWhiteSpace(loadedNetId)) return false;
            if (!_byNetId.TryGetValue(loadedNetId, out var instance) || instance == null) return false;

            // Remove from URL grouping list
            if (!string.IsNullOrWhiteSpace(instance.Url) && _map.TryGetValue(instance.Url, out var list) && list != null)
            {
                if (!list.Remove(instance))
                {
                    int idx = list.FindIndex(x => x != null && x.LoadedNetID == loadedNetId);
                    if (idx >= 0) list.RemoveAt(idx);
                }

                if (list.Count == 0)
                    _map.Remove(instance.Url);
            }

            // Remove from net-id index
            _byNetId.Remove(loadedNetId);

            // Remove runtime refs (no Destroy/Unload here)
            SpawnedGameobjects.TryRemove(loadedNetId, out _);
            SpawnedScenes.TryRemove(loadedNetId, out _);

            // raise an event we removed something
            RaiseChanged(RegistryChangeType.Removed, instance);
            return true;
        }

        public static bool TryGetAny(string url, out SpawnInstance instance)
        {
            instance = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!_map.TryGetValue(url, out var list) || list == null || list.Count == 0) return false;

            instance = list[list.Count - 1]; // most recent
            return instance != null;
        }

        public static bool TryGetByLoadedNetId(string loadedNetId, out SpawnInstance instance)
        {
            instance = null;
            if (string.IsNullOrWhiteSpace(loadedNetId)) return false;
            return _byNetId.TryGetValue(loadedNetId, out instance) && instance != null;
        }

        /// <summary>
        /// Applies the server-authoritative "static / locked" state to a spawned item: updates the
        /// stored record (so the library UI reflects it) and applies the freeze/lock to the live
        /// object via any <see cref="IBasisStaticLockable"/> components (pickup prop or vehicle).
        /// Main-thread only (touches GameObjects).
        /// </summary>
        public static void SetStaticByLoadedNetId(string loadedNetId, bool isStatic, bool adminLocked)
        {
            if (string.IsNullOrWhiteSpace(loadedNetId)) return;

            _byNetId.TryGetValue(loadedNetId, out SpawnInstance instance);
            if (instance != null)
            {
                instance.Static = isStatic;
                instance.StaticAdminLocked = adminLocked;
            }

            // The freeze itself is identical for both lock tiers — only the authority to change it differs.
            if (SpawnedGameobjects.TryGetValue(loadedNetId, out GameObject go) && go != null)
            {
                BasisSpawnedStaticState.Apply(go, isStatic);
            }

            if (instance != null)
            {
                RaiseChanged(RegistryChangeType.Modified, instance);
            }
        }

        /// <summary>
        /// Clears all instances for a specific URL (registry + runtime refs).
        /// Does NOT unload/destroy anything by itself; it only forgets references.
        /// </summary>
        public static void ClearAll(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (_map.TryGetValue(url, out var list) && list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var inst = list[i];
                    if (inst != null && !string.IsNullOrWhiteSpace(inst.LoadedNetID))
                    {
                        _byNetId.Remove(inst.LoadedNetID);
                        SpawnedGameobjects.TryRemove(inst.LoadedNetID, out _);
                        SpawnedScenes.TryRemove(inst.LoadedNetID, out _);
                        
                        // raise event that we cleared the url
                        RaiseChanged(RegistryChangeType.ClearedUrl, inst);
                    }
                }
            }

            _map.Remove(url);
            
            // raise event we cleared cleared all
            RaiseChanged(RegistryChangeType.ClearedAll, null);
        }

        /// <summary>
        /// Clears *everything* in the registry + runtime refs.
        /// Does NOT unload/destroy anything by itself; it only forgets references.
        /// </summary>
        public static void ClearAll()
        {
            _map.Clear();
            _byNetId.Clear();

            SpawnedGameobjects.Clear();
            SpawnedScenes.Clear();
        }

        /// <summary>
        /// Returns all spawn instances across all URLs (flat view).
        /// </summary>
        public static IReadOnlyCollection<SpawnInstance> GetAll()
        {
            return _byNetId.Values;
        }

        public static async Task<int> ClearAllNetworking()
        {
            var toRemove = new List<string>();

            foreach (var kvp in _byNetId)
            {
                var inst = kvp.Value;
                if (inst != null && inst.SpawnMethod == SpawnMethod.Network)
                    toRemove.Add(kvp.Key);
            }

            int nuked = 0;
            for (int i = 0; i < toRemove.Count; i++)
            {
                if (await RemoveByLoadedNetId(toRemove[i]))
                {
                    nuked++;
                }
            }

            return nuked;
        }

        /// <summary>
        /// Number of currently spawned worlds (scenes) and props (game objects). Avatars are excluded.
        /// </summary>
        public static int CountWorldsAndProps()
        {
            int count = 0;
            foreach (var kvp in _byNetId)
            {
                var inst = kvp.Value;
                if (inst != null && (inst.SpawnMode == SpawnMode.Scene || inst.SpawnMode == SpawnMode.GameObject))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Unloads every spawned world (scene) and prop (game object), leaving avatars untouched.
        /// Network-spawned content is unloaded server-authoritatively so remote players see it removed
        /// too; local/embedded content (which the server never tracked) is removed directly on this
        /// client. Returns the number of instances acted on.
        /// </summary>
        public static async Task<int> RemoveAllWorldsAndProps()
        {
            var toRemove = new List<SpawnInstance>();

            foreach (var kvp in _byNetId)
            {
                var inst = kvp.Value;
                if (inst != null && (inst.SpawnMode == SpawnMode.Scene || inst.SpawnMode == SpawnMode.GameObject))
                    toRemove.Add(inst);
            }

            int nuked = 0;
            for (int i = 0; i < toRemove.Count; i++)
            {
                var inst = toRemove[i];
                if (inst.SpawnMethod == SpawnMethod.Network)
                {
                    // Route through the server; it rebroadcasts the unload to every client and our own
                    // UnloadResourceMessage echo performs the local removal.
                    if (inst.SpawnMode == SpawnMode.Scene)
                        BasisNetworkSpawnItem.RequestSceneUnLoad(inst.LoadedNetID);
                    else
                        BasisNetworkSpawnItem.RequestGameObjectUnLoad(inst.LoadedNetID);
                    nuked++;
                }
                else if (await RemoveByLoadedNetId(inst.LoadedNetID))
                {
                    nuked++;
                }
            }

            return nuked;
        }

        /// <summary>
        /// basically we want to ensure there are no existing scenes of local when we network in the desired scene we want
        /// </summary>
        public static async Task RemoveAllLocalScenes()
        {
            BasisDebug.Log($"RemoveAllLocalScenes() -> invoked");

            if (SpawnedScenes.Count == 0)
            {
                BasisDebug.Log($"No existing instances of local scenes found");
                return;
            }

            foreach (var kvp in SpawnedScenes)
            {
                string key = kvp.Key;     // e.g., LoadedNetID or URL
                Scene scene = kvp.Value;  // the actual Unity Scene

                if (!scene.IsValid())
                {
                    BasisDebug.LogWarning($"Stale scene entry detected (Key = {key}). Skipping.");
                    continue;
                }

                BasisDebug.Log($"Attempting removal of local scene instance (Key = {key})");

                bool success = await RemoveByLoadedNetId(key);

                if (success)
                {
                    BasisDebug.Log($"Successfully removed scene instance (Key = {key})");
                }
                else
                {
                    BasisDebug.LogError($"Failed to remove scene instance (Key = {key})");
                }
            }

        }
    }
}
