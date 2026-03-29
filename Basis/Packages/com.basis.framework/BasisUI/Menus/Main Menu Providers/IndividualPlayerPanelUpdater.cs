using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using UnityEngine;

namespace Basis.BasisUI
{
    public class IndividualPlayerPanelUpdater : MonoBehaviour
    {
        public BasisRemotePlayer RemotePlayer;
        public PanelElementDescriptor DebugField;
        public PanelElementDescriptor DistanceField;
        public PanelElementDescriptor LodField;
        public PanelElementDescriptor RangesField;
        public PanelElementDescriptor BufferField;

        private float _updateTimer;
        private const float UpdateInterval = 0.2f;

        private void Update()
        {
            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer < UpdateInterval) return;
            _updateTimer = 0f;

            if (RemotePlayer == null)
            {
                SetAll("RemotePlayer is null.");
                return;
            }

            var nm = BasisNetworkManagement.Instance;
            if (nm == null || nm.LocalAccessTransmitter == null)
            {
                SetAll("No LocalAccessTransmitter.");
                return;
            }

            var transmitter = nm.LocalAccessTransmitter;
            var results = transmitter.TransmissionResults;

            // Debug / Transmission field
            if (DebugField != null)
            {
                if (results == null)
                {
                    DebugField.SetDescription("TransmissionResults is null.");
                }
                else
                {
                    DebugField.SetDescription(
                        $"Interval: {results.intervalSeconds:F3}s\n" +
                        $"DefaultInterval: {results.DefaultInterval:F3}s\n" +
                        $"UnclampedInterval: {results.UnClampedInterval:F3}s"
                    );
                }
            }

            // Find this player's index in the receivers snapshot
            if (results == null || results.LengthOfArrays <= 0)
            {
                if (DistanceField != null) DistanceField.SetDescription("No data");
                if (LodField != null) LodField.SetDescription("No data");
                if (RangesField != null) RangesField.SetDescription("No data");
                UpdateBufferField();
                return;
            }

            // Look up the receiver for this remote player
            if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(RemotePlayer, out var netPlayer))
            {
                if (DistanceField != null) DistanceField.SetDescription("Player not found");
                if (LodField != null) LodField.SetDescription("Player not found");
                if (RangesField != null) RangesField.SetDescription("Player not found");
                UpdateBufferField();
                return;
            }

            ushort playerId = netPlayer.playerId;
            var snapshot = BasisNetworkPlayers.ReceiversSnapshot;
            int receiverCount = results.LengthOfArrays;
            int playerIndex = -1;

            for (int i = 0; i < receiverCount && i < snapshot.Length; i++)
            {
                if (snapshot[i] != null && snapshot[i].playerId == playerId)
                {
                    playerIndex = i;
                    break;
                }
            }

            if (playerIndex < 0)
            {
                if (DistanceField != null) DistanceField.SetDescription("Not in snapshot");
                if (LodField != null) LodField.SetDescription("Not in snapshot");
                if (RangesField != null) RangesField.SetDescription("Not in snapshot");
                UpdateBufferField();
                return;
            }

            // Distance
            if (DistanceField != null)
            {
                Vector3 localPos = BasisLocalCameraDriver.Position;
                Vector3 remotePos = RemotePlayer.MouthTransform != null
                    ? RemotePlayer.MouthTransform.position
                    : RemotePlayer.transform.position;
                float dist = Vector3.Distance(localPos, remotePos);
                DistanceField.SetDescription($"{dist:F2}m");
            }

            // LOD level
            if (LodField != null)
            {
                if (results.MeshLodLevel.IsCreated && playerIndex < results.MeshLodLevel.Length)
                {
                    short lod = results.MeshLodLevel[playerIndex];
                    string lodName = lod switch
                    {
                        0 => "LOD 0 (Highest)",
                        1 => "LOD 1",
                        2 => "LOD 2",
                        _ => $"LOD {lod} (Lowest)"
                    };
                    LodField.SetDescription(lodName);
                }
                else
                {
                    LodField.SetDescription("N/A");
                }
            }

            // Ranges
            if (RangesField != null)
            {
                bool inAvatar = RemotePlayer.InAvatarRange;
                bool outOfRange = RemotePlayer.OutOfRangeFromLocal;

                string micRange = "N/A";
                string avatarRange = inAvatar ? "Yes" : "No";
                string hearingRange = outOfRange ? "No" : "Yes";

                if (results.MicrophoneRange.IsCreated && playerIndex < results.MicrophoneRange.Length)
                {
                    micRange = results.MicrophoneRange[playerIndex] ? "Yes" : "No";
                }

                RangesField.SetDescription(
                    $"Avatar: {avatarRange} | Hearing: {hearingRange}\n" +
                    $"Microphone: {micRange}");
            }

            UpdateBufferField();
        }

        private void UpdateBufferField()
        {
            if (BufferField == null) return;

            if (RemotePlayer == null || RemotePlayer.NetworkReceiver == null)
            {
                BufferField.SetDescription("No receiver");
                return;
            }

            var receiver = RemotePlayer.NetworkReceiver;
            int staged = receiver.StagedCount;
            int queued = receiver.PayloadQueue.Count;
            bool dataReady = receiver.IsDataReady;
            bool hasCurrent = receiver.HasCurrentBuffer;
            bool hasNext = receiver.HasNextBuffer;

            BufferField.SetDescription(
                $"Queued: {queued} | Staged: {staged}\n" +
                $"Current: {(hasCurrent ? "Yes" : "No")} | Next: {(hasNext ? "Yes" : "No")}\n" +
                $"Data Ready: {(dataReady ? "Yes" : "No")}");
        }

        private void SetAll(string message)
        {
            if (DebugField != null) DebugField.SetDescription(message);
            if (DistanceField != null) DistanceField.SetDescription(message);
            if (LodField != null) LodField.SetDescription(message);
            if (RangesField != null) RangesField.SetDescription(message);
            if (BufferField != null) BufferField.SetDescription(message);
        }
    }
}
