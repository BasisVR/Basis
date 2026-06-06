using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;
using Unity.Mathematics;

namespace Basis.Scripts.UI.NamePlate
{
    /// <summary>
    /// Static driver that manages nameplates for remote PIP camera feeds.
    /// Spawns nameplates when a remote player's camera becomes active,
    /// updates them when the player's name changes, and destroys them
    /// when the camera deactivates or the player disconnects.
    /// </summary>
    public static class BasisCameraNamePlateDriver
    {
        /// <summary>
        /// The Y-offset (in world space) above the PIP camera where the nameplate is positioned.
        /// </summary>
        public static float NamePlateYOffset = 0.22f;

        /// <summary>
        /// Scale multiplier for the nameplate size relative to the PIP camera size.
        /// </summary>
        public static float NamePlateScale = 1.0f;

        /// <summary>
        /// Whether nameplates are enabled for PIP cameras.
        /// </summary>
        public static bool NamePlateEnabled = true;

        private const string NamePlateLayerName = "UI";

        /// <summary>
        /// Whether the driver has been initialized.
        /// </summary>
        private static bool _initialized;

        /// <summary>
        /// Registry of active nameplates keyed by player ID.
        /// </summary>
        private static readonly Dictionary<ushort, BasisCameraNamePlate> activePlates = new();
        private static readonly Dictionary<ushort, int> activePlateIndices = new();
        private static readonly List<BasisCameraNamePlate> activePlateList = new();

        // ===========================
        // Lifecycle
        // ===========================

        /// <summary>
        /// Idempotent. Triggered by <see cref="Basis.Scripts.Device_Management.BasisDeviceManagement"/>
        /// after device init completes. Subscribes to PIP camera events.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            BasisNetworkPIPCameraDriver.OnRemotePIPCreated += OnRemotePIPCreated;
            BasisNetworkPIPCameraDriver.OnRemotePIPDestroyed += OnRemotePIPDestroyed;
        }

        /// <summary>
        /// Shutdown the driver. Unsubscribes from events and destroys all nameplates.
        /// </summary>
        public static void Shutdown()
        {
            if (!_initialized) return;
            _initialized = false;

            BasisNetworkPIPCameraDriver.OnRemotePIPCreated -= OnRemotePIPCreated;
            BasisNetworkPIPCameraDriver.OnRemotePIPDestroyed -= OnRemotePIPDestroyed;

            foreach (var kvp in activePlates)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                    UnityEngine.Object.Destroy(kvp.Value.gameObject);
            }
            activePlates.Clear();
            activePlateIndices.Clear();
            activePlateList.Clear();
        }

        // ===========================
        // Registration
        // ===========================

        public static void Register(BasisCameraNamePlate plate)
        {
            if (plate == null || plate.PlayerID == 0) return;
            if (activePlates.TryGetValue(plate.PlayerID, out BasisCameraNamePlate existing))
            {
                if (ReferenceEquals(existing, plate))
                {
                    return;
                }
                if (existing != null)
                {
                    existing.OnPlayerLeft();
                }
            }

            activePlates[plate.PlayerID] = plate;
            activePlateIndices[plate.PlayerID] = activePlateList.Count;
            activePlateList.Add(plate);
        }

        public static void Unregister(BasisCameraNamePlate plate)
        {
            if (plate == null || plate.PlayerID == 0) return;
            if (!activePlates.TryGetValue(plate.PlayerID, out BasisCameraNamePlate registered)) return;
            if (!ReferenceEquals(registered, plate)) return;

            activePlates.Remove(plate.PlayerID);

            if (!activePlateIndices.TryGetValue(plate.PlayerID, out int index)) return;

            int lastIndex = activePlateList.Count - 1;
            BasisCameraNamePlate last = activePlateList[lastIndex];
            activePlateList[index] = last;
            activePlateList.RemoveAt(lastIndex);
            activePlateIndices.Remove(plate.PlayerID);

            if (index != lastIndex && last != null)
            {
                activePlateIndices[last.PlayerID] = index;
            }
        }

        // ===========================
        // Per-player operations
        // ===========================

        /// <summary>
        /// Create a nameplate for a PIP camera when its camera becomes active.
        /// </summary>
        public static bool ShouldPlateBeActive(BasisCameraNamePlate plate)
        {
            if (!NamePlateEnabled) return false;
            if (!BasisRemoteNamePlateDriver.NamePlateEnabled) return false;
            if (BasisRemoteNamePlateDriver.NamePlateMenuOnly && BasisMainMenu.Instance == null) return false;
            if (!plate.IsActive) return false;
            if (plate.RemotePlayer != null && plate.RemotePlayer.IsEffectivelyBlocked) return false;
            return true;
        }

        public static void CreateForPlayer(ushort playerId, string displayName)
        {
            CreateForPlayer(playerId, displayName, null);
        }

        public static void CreateForPlayer(ushort playerId, string displayName, BasisRemotePlayer remotePlayer)
        {
            if (playerId == 0) return;
            if (activePlates.ContainsKey(playerId)) return;

            GameObject plateGO = new GameObject($"CameraNamePlate_{playerId}");
            int namePlateLayer = LayerMask.NameToLayer(NamePlateLayerName);
            if (namePlateLayer >= 0)
            {
                plateGO.layer = namePlateLayer;
            }
            plateGO.transform.SetParent(BasisDeviceManagement.Instance.transform, false);
            plateGO.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            var plate = plateGO.AddComponent<BasisCameraNamePlate>();
            plate.Initialize(playerId, displayName, null, remotePlayer);
        }

        /// <summary>
        /// Destroy the nameplate for a player's camera (camera deactivated or player left).
        /// </summary>
        public static void DestroyForPlayer(ushort playerId)
        {
            if (activePlates.TryGetValue(playerId, out var plate) && plate != null)
            {
                plate.OnPlayerLeft();
            }
        }

        /// <summary>
        /// Update the display name for an existing nameplate.
        /// </summary>
        public static void UpdateDisplayName(ushort playerId, string newName)
        {
            if (activePlates.TryGetValue(playerId, out var plate) && plate != null)
            {
                plate.UpdateDisplayName(newName);
            }
        }

        /// <summary>
        /// Activate or deactivate a nameplate (camera toggled on/off).
        /// </summary>
        public static void SetPlateActive(ushort playerId, bool active)
        {
            if (activePlates.TryGetValue(playerId, out var plate) && plate != null)
            {
                plate.SetActive(active);
            }
        }

        // ===========================
        // Per-frame updates
        // ===========================

        /// <summary>
        /// Called from BasisEventDriver per-frame to update all nameplate positions
        /// and refresh meshes for plates whose names have changed.
        /// </summary>
        public static void UpdateAllPlatePositions()
        {
            for (int index = activePlateList.Count - 1; index >= 0; index--)
            {
                BasisCameraNamePlate plate = activePlateList[index];
                if (plate == null)
                {
                    continue;
                }

                plate.RefreshActiveState();
                RefreshPlayerMetadata(plate);
                plate.RefreshMeshIfNeeded();

                if (ShouldPlateBeActive(plate))
                {
                    UpdatePlatePosition(plate);
                }
            }
        }

        /// <summary>
        /// Refresh meshes for all plates that need it.
        /// Call when names change.
        /// </summary>
        public static void RefreshAllMeshes()
        {
            for (int index = 0; index < activePlateList.Count; index++)
            {
                BasisCameraNamePlate plate = activePlateList[index];
                if (plate != null)
                {
                    plate.RefreshMeshIfNeeded();
                }
            }
        }

        public static void ApplyNamePlateSettingsFromUI()
        {
            for (int index = 0; index < activePlateList.Count; index++)
            {
                BasisCameraNamePlate plate = activePlateList[index];
                if (plate == null) continue;

                plate.ApplyScale();
                plate.RefreshActiveState();
            }
        }

        // ===========================
        // Positioning
        // ===========================

        private static void UpdatePlatePosition(ushort playerId)
        {
            if (!activePlates.TryGetValue(playerId, out var plate) || plate == null) return;

            UpdatePlatePosition(plate);
        }

        private static void UpdatePlatePosition(BasisCameraNamePlate plate)
        {
            ushort playerId = plate.PlayerID;
            if (BasisNetworkPIPCameraDriver.TryGetPIPPosition(playerId, out Vector3 pipPos))
            {
                Vector3 position = pipPos + (Vector3.up * NamePlateYOffset);
                float3 toCam = (float3)(BasisLocalCameraDriver.Position - position);
                float2 xz = new float2(toCam.x, toCam.z);
                float yaw = math.lengthsq(xz) > 1e-12f ? math.atan2(xz.x, xz.y) : 0f;
                quaternion billboardRotation = quaternion.RotateY(yaw);
                Quaternion rotation = new Quaternion(
                    billboardRotation.value.x,
                    billboardRotation.value.y,
                    billboardRotation.value.z,
                    billboardRotation.value.w
                );

                plate.transform.SetPositionAndRotation(position, rotation);
            }
        }

        private static void RefreshPlayerMetadata(BasisCameraNamePlate plate)
        {
            if (plate.RemotePlayer != null && !string.IsNullOrEmpty(plate.RemotePlayer.DisplayName))
            {
                if (plate.DisplayName != plate.RemotePlayer.DisplayName)
                {
                    plate.UpdateDisplayName(plate.RemotePlayer.DisplayName);
                }
                return;
            }

            if (!BasisNetworkPlayer.GetPlayerById(plate.PlayerID, out var networkPlayer) || networkPlayer == null)
            {
                return;
            }

            if (!networkPlayer.TryGetPlayer(out var player) || player == null)
            {
                return;
            }

            if (player is BasisRemotePlayer remotePlayer)
            {
                plate.RemotePlayer = remotePlayer;
            }

            if (!string.IsNullOrEmpty(player.DisplayName) && plate.DisplayName != player.DisplayName)
            {
                plate.UpdateDisplayName(player.DisplayName);
            }
        }

        // ===========================
        // Event handlers
        // ===========================

        private static void OnRemotePIPCreated(ushort playerId, float3 position, quaternion rotation)
        {
            if (BasisNetworkPlayer.GetPlayerById(playerId, out var networkPlayer) && networkPlayer != null)
            {
                networkPlayer.TryGetPlayer(out var player);
                string displayName = player != null ? player.DisplayName : $"Player {playerId}";
                CreateForPlayer(playerId, displayName, player as BasisRemotePlayer);
            }
            else
            {
                CreateForPlayer(playerId, $"Player {playerId}");
            }
        }

        private static void OnRemotePIPDestroyed(ushort playerId)
        {
            DestroyForPlayer(playerId);
        }
    }
}
