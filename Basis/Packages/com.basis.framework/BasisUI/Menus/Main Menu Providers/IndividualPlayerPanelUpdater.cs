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

        // Audio debug fields
        public PanelElementDescriptor AudioSourceField;
        public PanelElementDescriptor VolumeChainField;
        public PanelElementDescriptor DecodedBufferField;
        public PanelElementDescriptor EncodedBufferField;
        public PanelElementDescriptor SilenceField;
        public PanelElementDescriptor VisemeField;

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
                UpdateAudioDebugFields();
                return;
            }

            // Look up the receiver for this remote player
            if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(RemotePlayer, out var netPlayer))
            {
                if (DistanceField != null) DistanceField.SetDescription("Player not found");
                if (LodField != null) LodField.SetDescription("Player not found");
                if (RangesField != null) RangesField.SetDescription("Player not found");
                UpdateBufferField();
                UpdateAudioDebugFields();
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
                UpdateAudioDebugFields();
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
            UpdateAudioDebugFields();
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

        private void UpdateAudioDebugFields()
        {
            if (RemotePlayer == null || RemotePlayer.NetworkReceiver == null) return;

            BasisAudioReceiver audio = RemotePlayer.NetworkReceiver.AudioReceiverModule;
            if (audio == null)
            {
                SetAudioDebugAll("No AudioReceiverModule");
                return;
            }

            // Audio Source
            if (AudioSourceField != null && BasisSettingsDefaults.AudioDebugShowSource.RawValue)
            {
                AudioSource src = audio.audioSource;
                if (src != null)
                {
                    AudioSourceField.SetDescription(
                        $"Has Source: {audio.HasAudioSource} | Enabled: {src.enabled} | Playing: {src.isPlaying}\n" +
                        $"Spatial: {src.spatialBlend:F2} ({(src.spatialBlend > 0.5f ? "3D" : "2D")}) | Max Dist: {src.maxDistance:F1}m\n" +
                        $"Rolloff: {src.rolloffMode} | Spatialize: {src.spatialize} | Doppler: {src.dopplerLevel:F2}");
                }
                else
                {
                    AudioSourceField.SetDescription($"Has Source: {audio.HasAudioSource} | AudioSource: NULL");
                }
            }

            // Volume Chain
            if (VolumeChainField != null && BasisSettingsDefaults.AudioDebugShowVolume.RawValue)
            {
                AudioSource src = audio.audioSource;
                float srcVol = src != null ? src.volume : 0f;
                float dampen = audio.DirectionalDampeningMultiplier;
                float listenerVol = AudioListener.volume;
                float effective = srcVol * dampen * listenerVol;

                string dampenNote = dampen < 0.5f ? " (BEHIND)" : dampen < 1f ? " (off-axis)" : "";
                string warning = effective < 0.01f && srcVol > 0f ? "\nWARNING: Near zero!" : "";

                VolumeChainField.SetDescription(
                    $"Source: {srcVol:F2} x Dampen: {dampen:F3}{dampenNote} x Listener: {listenerVol:F2}\n" +
                    $"Effective: {effective:F3}{warning}");
            }

            // Voice Buffer (combined jitter + decoded)
            if (DecodedBufferField != null && BasisSettingsDefaults.AudioDebugShowRingBuffer.RawValue)
            {
                BasisVoiceBuffer buf = audio.VoiceBuffer;
                if (buf != null)
                {
                    int frames = buf.DecodedFrameCount;
                    int cap = buf.DecodedFrameCapacity;
                    int samples = buf.SampleCount;
                    float ms = samples * 1000f / RemoteOpusSettings.NetworkSampleRate;
                    string state = buf.IsEmpty ? "EMPTY" : frames >= cap ? "FULL" : "Streaming";

                    DecodedBufferField.SetDescription(
                        $"Frames: {frames}/{cap} | {ms:F1}ms buffered\n" +
                        $"Real Audio: {(buf.HasRealAudio ? "Yes" : "No")} | State: {state}");
                }
                else
                {
                    DecodedBufferField.SetDescription("Voice Buffer: NULL");
                }
            }

            if (EncodedBufferField != null && BasisSettingsDefaults.AudioDebugShowJitter.RawValue)
            {
                BasisVoiceBuffer buf = audio.VoiceBuffer;
                if (buf != null)
                {
                    int buffered = buf.EncodedBufferedCount;
                    int received = buf.ReceivedSinceStart;
                    int depth = buf.InitialBufferDepth;
                    string status = !buf.Started ? "Not started"
                        : received < depth ? $"FILLING ({received}/{depth})"
                        : "Playing";

                    EncodedBufferField.SetDescription(
                        $"Started: {(buf.Started ? "Yes" : "No")} | Buffered: {buffered} | Received: {received}\n" +
                        $"Init Depth: {depth} | Status: {status}\n" +
                        $"PLC: {audio.PlcCount} | Silence Skipped: {audio.SilenceInjectedCount}");
                }
                else
                {
                    EncodedBufferField.SetDescription("Voice Buffer: NULL");
                }
            }

            // Silence
            if (SilenceField != null && BasisSettingsDefaults.AudioDebugShowSilence.RawValue)
            {
                int units = audio._silentUnits20ms;
                float ms = units * 20f;
                string note = ms > 2000f ? " PROLONGED" : ms > 500f ? " gap" : "";

                SilenceField.SetDescription(
                    $"Silent: {units} units ({ms:F0}ms){note}");
            }

            // Viseme
            if (VisemeField != null && BasisSettingsDefaults.AudioDebugShowViseme.RawValue)
            {
                bool hasDriver = audio.BasisRemoteVisemeAudioDriver != null;
                bool init = hasDriver && audio.BasisRemoteVisemeAudioDriver.Initalized;

                VisemeField.SetDescription(
                    $"Driver: {(hasDriver ? "Active" : "None")} | Initialized: {(init ? "Yes" : "No")}");
            }
        }

        private void SetAudioDebugAll(string message)
        {
            if (AudioSourceField != null) AudioSourceField.SetDescription(message);
            if (VolumeChainField != null) VolumeChainField.SetDescription(message);
            if (DecodedBufferField != null) DecodedBufferField.SetDescription(message);
            if (EncodedBufferField != null) EncodedBufferField.SetDescription(message);
            if (SilenceField != null) SilenceField.SetDescription(message);
            if (VisemeField != null) VisemeField.SetDescription(message);
        }

        private void SetAll(string message)
        {
            if (DebugField != null) DebugField.SetDescription(message);
            if (DistanceField != null) DistanceField.SetDescription(message);
            if (LodField != null) LodField.SetDescription(message);
            if (RangesField != null) RangesField.SetDescription(message);
            if (BufferField != null) BufferField.SetDescription(message);
            SetAudioDebugAll(message);
        }
    }
}
