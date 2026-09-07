using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
            if (go.TryGetComponent(out BasisContentBase content)) StampUnassignedContent(content, instance);

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
            if (scene.isLoaded)
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    BasisScene basisScene = root.GetComponentInChildren<BasisScene>(true);
                    if (basisScene != null)
                    {
                        StampUnassignedContent(basisScene, instance);
                        break;
                    }
                }
            }

            // keep runtime ref
            SpawnedScenes[loadedNetId] = scene;
        }

        static void StampUnassignedContent(BasisContentBase content, SpawnInstance instance)
        {
            if (content == null || content.TryGetIdentifier(out _)) return;
            content.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Vector3 scale = content.transform.localScale;
            content.AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation
            {
                Mode = (byte)(instance.SpawnMode == SpawnMode.Scene ? 1 : 0),
                LoadedNetID = instance.LoadedNetID,
                UUIDOfCreator = instance.UUIDOfCreator,
                SpawnedByLocalPlayer = instance.SpawnMethod != SpawnMethod.Network || (BasisLocalPlayer.Instance != null && instance.UUIDOfCreator == BasisLocalPlayer.Instance.UUID),
                IsAdminLocked = instance.isProtected,
                Persist = instance.Persistent,
                Static = instance.Static,
                StaticAdminLocked = instance.StaticAdminLocked,
                PositionX = position.x,
                PositionY = position.y,
                PositionZ = position.z,
                QuaternionX = rotation.x,
                QuaternionY = rotation.y,
                QuaternionZ = rotation.z,
                QuaternionW = rotation.w,
                ScaleX = scale.x,
                ScaleY = scale.y,
                ScaleZ = scale.z,
                ModifyScale = false,
            });
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

            // Clients that never got this spawn loaded hold a failure row for it instead of a real
            // instance; the unload broadcast is what tells them it is gone, so clear that first.
            DismissFailedLoadByNetId(loadedNetId);

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

            ClearFailedLoads();
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
            // Networked failures belong to the session we are leaving; their LoadedNetIDs mean
            // nothing to the next server.
            ClearFailedLoads(networkOnly: true);

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

        [Serializable]
        public class PendingLoad
        {
            public SpawnMode SpawnMode;
            public SpawnMethod SpawnMethod;
            public string PendingId;
            public string Url;
            public string LoadedNetID;   // network spawns only; what a failure row needs to ask the server to drop it
            public string UUIDOfCreator;
            public bool isProtected;
            public bool Persistent;
            public DateTime StartedUtc;
            public float Progress;
            public string Stage;
            [NonSerialized] public CancellationTokenSource Cts; // set for cancellable (local/embedded) loads; null for network — those are cancelled via a server unload request instead
        }

        public static event Action OnPendingLoadsChanged;
        public static event Action<PendingLoad> OnPendingLoadProgress;

        private static readonly Dictionary<string, PendingLoad> _pendingLoads = new();
        private static readonly ConcurrentQueue<(string PendingId, float Progress, string Stage)> _pendingProgressQueue = new();
        private static readonly Action _drainPendingProgress = DrainPendingProgress;

        public static IReadOnlyCollection<PendingLoad> GetPendingLoads() => _pendingLoads.Values;

        public static PendingLoad BeginPendingLoad(string url, SpawnMode mode, SpawnMethod method, string creatorUUID, bool admin, bool persistent, string loadedNetId = null, CancellationTokenSource cts = null)
        {
            PendingLoad pending = new PendingLoad
            {
                PendingId = Guid.NewGuid().ToString("N"),
                Url = url,
                LoadedNetID = loadedNetId,
                SpawnMode = mode,
                SpawnMethod = method,
                UUIDOfCreator = creatorUUID,
                isProtected = admin,
                Persistent = persistent,
                StartedUtc = DateTime.UtcNow,
                Progress = 0f,
                Stage = string.Empty,
                Cts = cts
            };
            _pendingLoads[pending.PendingId] = pending;
            OnPendingLoadsChanged?.Invoke();
            return pending;
        }

        public static void EndPendingLoad(string pendingId)
        {
            if (string.IsNullOrEmpty(pendingId)) return;
            if (_pendingLoads.Remove(pendingId))
            {
                OnPendingLoadsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Requests cancellation of an in-flight local/embedded load (the row's own load site observes
        /// its Cts and unwinds via OperationCanceledException, which EndPendingLoad's finally clears —
        /// the row disappears from that, not from this call). No-op for a network pending load: those
        /// have no Cts, since only the server can authoritatively cancel a networked spawn.
        /// </summary>
        public static bool RequestCancelPendingLoad(string pendingId)
        {
            if (!_pendingLoads.TryGetValue(pendingId, out PendingLoad pending) || pending == null || pending.Cts == null)
            {
                return false;
            }
            pending.Cts.Cancel();
            return true;
        }

        public static void ReportPendingLoadProgress(string pendingId, float progress, string stage)
        {
            _pendingProgressQueue.Enqueue((pendingId, progress, stage));
            BasisDeviceManagement.EnqueueOnMainThread(_drainPendingProgress);
        }

        private static void DrainPendingProgress()
        {
            while (_pendingProgressQueue.TryDequeue(out (string PendingId, float Progress, string Stage) tick))
            {
                if (!_pendingLoads.TryGetValue(tick.PendingId, out PendingLoad pending) || pending == null)
                {
                    continue;
                }
                pending.Progress = tick.Progress;
                pending.Stage = tick.Stage;
                OnPendingLoadProgress?.Invoke(pending);
            }
        }

        /// <summary>
        /// Content that started loading and never arrived. Kept because nothing else records it:
        /// a spawn only reaches the registry once its bundle is live, so a bundle that 404s (or
        /// has no build for this platform) leaves no row anywhere — while the server keeps handing
        /// the same spawn to every joiner, and a persistent one comes back every session, with no
        /// UI to remove it from.
        /// </summary>
        [Serializable]
        public class FailedLoad
        {
            public SpawnMode SpawnMode;
            public SpawnMethod SpawnMethod;
            public string FailedId;
            public string Url;
            public string LoadedNetID;   // empty for local/embedded loads
            public string UUIDOfCreator;
            public bool isProtected;
            public bool Persistent;
            public DateTime FailedUtc;
            public string Error;         // untranslated detail for the row's tooltip; may be null
        }

        public static event Action OnFailedLoadsChanged;

        private static readonly Dictionary<string, FailedLoad> _failedLoads = new();

        public static IReadOnlyCollection<FailedLoad> GetFailedLoads() => _failedLoads.Values;

        // One row per spawn, not per attempt: a networked spawn is identified by its LoadedNetID,
        // a local one by its URL — nothing else tells two attempts at the same file apart, and the
        // placement bounds probe loads the same prop once more right before the real spawn.
        private static string FailedLoadKey(string loadedNetId, string url)
            => string.IsNullOrEmpty(loadedNetId) ? "url:" + url : "net:" + loadedNetId;

        /// <summary>
        /// Turns an in-flight load into a failure row. Main-thread only, and a no-op once the
        /// pending record is gone — every success path ends the pending load before it registers
        /// the real instance, so a late call can never resurrect a load that actually worked.
        /// </summary>
        public static void FailPendingLoad(string pendingId, string error)
        {
            if (string.IsNullOrEmpty(pendingId)) return;
            if (!_pendingLoads.TryGetValue(pendingId, out PendingLoad pending) || pending == null) return;

            _pendingLoads.Remove(pendingId);

            FailedLoad failed = new FailedLoad
            {
                FailedId = FailedLoadKey(pending.LoadedNetID, pending.Url),
                Url = pending.Url,
                LoadedNetID = pending.LoadedNetID,
                SpawnMode = pending.SpawnMode,
                SpawnMethod = pending.SpawnMethod,
                UUIDOfCreator = pending.UUIDOfCreator,
                isProtected = pending.isProtected,
                Persistent = pending.Persistent,
                FailedUtc = DateTime.UtcNow,
                Error = error
            };
            _failedLoads[failed.FailedId] = failed;

            OnPendingLoadsChanged?.Invoke();
            OnFailedLoadsChanged?.Invoke();
        }

        public static void DismissFailedLoad(string failedId)
        {
            if (string.IsNullOrEmpty(failedId)) return;
            if (_failedLoads.Remove(failedId))
            {
                OnFailedLoadsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Drops the failure row for a networked spawn. Called from the unload path so the server's
        /// removal broadcast clears the row on every client that failed to load it, not just the one
        /// that asked for the removal.
        /// </summary>
        public static void DismissFailedLoadByNetId(string loadedNetId)
        {
            if (string.IsNullOrEmpty(loadedNetId)) return;
            DismissFailedLoad(FailedLoadKey(loadedNetId, null));
        }

        /// <summary>
        /// Forgets failure rows. <paramref name="networkOnly"/> keeps the local/embedded ones, which
        /// outlive a server session — a boot-loaded prop that failed is still failing after a
        /// disconnect, and its row is the only place to stop it loading again next launch.
        /// </summary>
        public static void ClearFailedLoads(bool networkOnly = false)
        {
            if (_failedLoads.Count == 0) return;

            if (networkOnly)
            {
                List<string> toRemove = new List<string>();
                foreach (KeyValuePair<string, FailedLoad> kvp in _failedLoads)
                {
                    if (kvp.Value != null && kvp.Value.SpawnMethod == SpawnMethod.Network)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                if (toRemove.Count == 0) return;

                for (int Index = 0; Index < toRemove.Count; Index++)
                {
                    _failedLoads.Remove(toRemove[Index]);
                }
            }
            else
            {
                _failedLoads.Clear();
            }

            OnFailedLoadsChanged?.Invoke();
        }
    }
}
