using System.Threading;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    /// <summary>
    /// Nameplate displayed above a remote PIP camera feed.
    /// Shows the player's display name using the same font and visual style as avatar nameplates.
    /// </summary>
    public class BasisCameraNamePlate : MonoBehaviour
    {
        /// <summary>
        /// The MeshFilter for the baked nameplate mesh.
        /// </summary>
        public MeshFilter BackgroundFilter;

        /// <summary>
        /// The MeshRenderer for the baked nameplate mesh.
        /// </summary>
        public MeshRenderer BackgroundRenderer;

        /// <summary>
        /// The player ID this nameplate belongs to.
        /// </summary>
        public ushort PlayerID;
        public BasisRemotePlayer RemotePlayer;

        /// <summary>
        /// Whether this nameplate is currently active and visible.
        /// </summary>
        private int _isActive = 1;
        private bool _lastAppliedActive = true;
        public bool IsActive
        {
            get => Volatile.Read(ref _isActive) == 1;
            private set => Volatile.Write(ref _isActive, value ? 1 : 0);
        }

        /// <summary>
        /// The current display name being shown.
        /// </summary>
        public string DisplayName;

        private BasisNamePlateVisual visual;

        public void Initialize(ushort playerId, string displayName, Transform parentTransform, BasisRemotePlayer remotePlayer = null)
        {
            PlayerID = playerId;
            RemotePlayer = remotePlayer;
            DisplayName = displayName;
            gameObject.name = $"CameraNamePlate_{playerId}";
            ApplyScale();

            BackgroundFilter = gameObject.AddComponent<MeshFilter>();
            BackgroundRenderer = gameObject.AddComponent<MeshRenderer>();
            visual = new BasisNamePlateVisual("CameraNamePlateCombinedMesh");
            visual.Attach(BackgroundFilter, BackgroundRenderer);
            visual.SetDisplayName(displayName);

            // Parent under BasisDeviceManagement for lifetime management
            if (parentTransform != null)
            {
                transform.SetParent(parentTransform, false);
            }

            // Register with the driver
            BasisCameraNamePlateDriver.Register(this);

            QueueMeshRefreshIfNeeded();
            RefreshActiveState();
        }

        public void UpdateDisplayName(string newName)
        {
            if (DisplayName == newName) return;
            DisplayName = newName;
            visual?.SetDisplayName(newName);
        }

        public void OnPlayerLeft()
        {
            BasisCameraNamePlateDriver.Unregister(this);
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        public void QueueMeshRefreshIfNeeded()
        {
            visual?.QueueMeshRefreshIfNeeded();
        }

        public void SetActive(bool active)
        {
            IsActive = active;
            RefreshActiveState();
        }

        public void ApplyScale()
        {
            transform.localScale = Vector3.one * 0.02f * BasisRemoteNamePlateDriver.NamePlateSize * BasisCameraNamePlateDriver.NamePlateScale;
            visual?.ApplyMaterial();
        }

        public void RefreshActiveState()
        {
            bool active = BasisCameraNamePlateDriver.ShouldPlateBeActive(this);
            if (_lastAppliedActive == active && gameObject.activeSelf == active)
            {
                return;
            }

            _lastAppliedActive = active;
            gameObject.SetActive(active);
        }

        private void OnDestroy()
        {
            BasisCameraNamePlateDriver.Unregister(this);
            visual?.Dispose();
            visual = null;
        }
    }
}
