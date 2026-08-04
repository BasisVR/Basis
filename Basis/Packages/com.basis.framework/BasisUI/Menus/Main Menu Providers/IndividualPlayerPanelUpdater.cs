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
        [System.NonSerialized] public BasisRemotePlayer RemotePlayer;
        public PanelElementDescriptor PanelDescriptor;
        public PanelElementDescriptor DebugField;
        public PanelElementDescriptor DistanceField;
        public PanelElementDescriptor LodField;
        public PanelElementDescriptor RangesField;
        public PanelElementDescriptor BufferField;
        public PanelElementDescriptor DirectConnPingField;
        [System.NonSerialized] public BasisPanelTint.Handle DirectConnPingTint;
        public RectTransform DirectConnRebuildFrom;
        public RectTransform DirectConnRebuildStopAt;
        private bool _pingVisible;
        private BasisPanelSeverity _pingSeverity;

        /// <summary>
        /// Direct-link round trip at which the ping card warns, then reads hot. Same thresholds the
        /// network settings tab grades the server ping with, so a P2P link and the server relay are
        /// judged by the same bar.
        /// </summary>
        private const int PingCautionRtt = 120;
        private const int PingHotRtt = 250;

        // Audio debug fields
        public PanelElementDescriptor AudioSourceField;
        public PanelElementDescriptor VolumeChainField;
        public PanelElementDescriptor DecodedBufferField;
        public PanelElementDescriptor EncodedBufferField;
        public PanelElementDescriptor SilenceField;
        public PanelElementDescriptor VisemeField;

        // Avatar data chain debug fields
        public PanelElementDescriptor AvatarReceiveField;
        public PanelElementDescriptor AvatarStagingField;
        public PanelElementDescriptor AvatarInterpField;
        public PanelElementDescriptor AvatarMetaField;

        private byte _lastHighestSequence;
        private bool _lastHighestSeqValid;

        private float _updateTimer;
        private const float UpdateInterval = 0.2f;

        private bool _richTextDisabled;
        private bool _layoutFrozen;
        private int _updatesSincePopulated;

        private void DisableRichTextOnce()
        {
            if (_richTextDisabled) return;
            _richTextDisabled = true;
            // These debug panels only display plain-text stats — skip tag scanning.
            DebugField?.DisableRichText();
            DistanceField?.DisableRichText();
            LodField?.DisableRichText();
            RangesField?.DisableRichText();
            BufferField?.DisableRichText();
            DirectConnPingField?.DisableRichText();
            AudioSourceField?.DisableRichText();
            VolumeChainField?.DisableRichText();
            DecodedBufferField?.DisableRichText();
            EncodedBufferField?.DisableRichText();
            SilenceField?.DisableRichText();
            VisemeField?.DisableRichText();
            AvatarReceiveField?.DisableRichText();
            AvatarStagingField?.DisableRichText();
            AvatarInterpField?.DisableRichText();
            AvatarMetaField?.DisableRichText();
        }

        /// <summary>
        /// Freezes one field, but only once it is on screen. A field on a tab the user has not
        /// opened is inactive with no measured height, so freezing there pins it to the fallback
        /// minimum and clips the real content later. Returns false to ask for another attempt.
        /// </summary>
        private static bool FreezeField(PanelElementDescriptor field, float minHeight)
        {
            if (field == null) return true;
            if (!field.gameObject.activeInHierarchy) return false;
            field.FreezeLayoutSize(minHeight);
            return true;
        }

        private void FreezeLayoutOnce()
        {
            if (_layoutFrozen) return;

            // Freeze each field's natural layout size once content has settled so
            // the per-frame SetDescription calls don't cascade layout rebuilds up
            // through the individual player panel's parent LayoutGroups.
            bool frozen = true;
            frozen &= FreezeField(DebugField, 130f);
            frozen &= FreezeField(DistanceField, 90f);
            frozen &= FreezeField(LodField, 90f);
            frozen &= FreezeField(RangesField, 110f);
            frozen &= FreezeField(BufferField, 130f);
            // DirectConnPingField intentionally not frozen — it toggles SetActive
            // based on P2P connection state, and freezing pins height even when hidden.
            frozen &= FreezeField(AudioSourceField, 140f);
            frozen &= FreezeField(VolumeChainField, 130f);
            frozen &= FreezeField(DecodedBufferField, 120f);
            frozen &= FreezeField(EncodedBufferField, 140f);
            frozen &= FreezeField(SilenceField, 90f);
            frozen &= FreezeField(VisemeField, 90f);
            frozen &= FreezeField(AvatarReceiveField, 120f);
            frozen &= FreezeField(AvatarStagingField, 140f);
            frozen &= FreezeField(AvatarInterpField, 140f);
            frozen &= FreezeField(AvatarMetaField, 170f);
            _layoutFrozen = frozen;
        }

        private void Update()
        {
            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer < UpdateInterval) return;
            _updateTimer = 0f;

            DisableRichTextOnce();

            // P2P ping is independent of the receiver/transmitter pipeline — refresh
            // it on every tick, before any of the early-returns below kick us out.
            UpdateDirectConnPingField();

            if (RemotePlayer == null)
            {
                SetAll("RemotePlayer is null.");
                return;
            }

            if (!BasisNetworkManagement.IsInitialized || BasisNetworkManagement.LocalAccessTransmitter == null)
            {
                SetAll("No LocalAccessTransmitter.");
                return;
            }

            var transmitter = BasisNetworkManagement.LocalAccessTransmitter;
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
                if (DistanceField != null) DistanceField.SetDescription(BasisLocalization.Get("menu.players.panel.noData"));
                if (LodField != null) LodField.SetDescription(BasisLocalization.Get("menu.players.panel.noData"));
                if (RangesField != null) RangesField.SetDescription(BasisLocalization.Get("menu.players.panel.noData"));
                UpdateBufferField();
                UpdateAudioDebugFields();
                UpdateAvatarDataDebugFields();
                return;
            }

            // Look up the receiver for this remote player
            if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(RemotePlayer, out var netPlayer))
            {
                if (DistanceField != null) DistanceField.SetDescription(BasisLocalization.Get("menu.players.panel.playerNotFound"));
                if (LodField != null) LodField.SetDescription(BasisLocalization.Get("menu.players.panel.playerNotFound"));
                if (RangesField != null) RangesField.SetDescription(BasisLocalization.Get("menu.players.panel.playerNotFound"));
                UpdateBufferField();
                UpdateAudioDebugFields();
                UpdateAvatarDataDebugFields();
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
                if (DistanceField != null) DistanceField.SetDescription(BasisLocalization.Get("menu.players.panel.notInSnapshot"));
                if (LodField != null) LodField.SetDescription(BasisLocalization.Get("menu.players.panel.notInSnapshot"));
                if (RangesField != null) RangesField.SetDescription(BasisLocalization.Get("menu.players.panel.notInSnapshot"));
                UpdateBufferField();
                UpdateAudioDebugFields();
                UpdateAvatarDataDebugFields();
                return;
            }

            // Distance
            if (DistanceField != null)
            {
                Vector3 localPos = BasisLocalCameraDriver.Position;
                Vector3 remotePos = RemotePlayer.MouthTransform != null
                    ? RemotePlayer.MouthTransform.position
                    : RemotePlayer.Transform.position;
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
            UpdateAvatarDataDebugFields();

            // Only count ticks that reached the full-data path — early-returns
            // above set fields to "N/A"/"No data" which would lock at a too-small
            // size and clip later real content.
            if (!_layoutFrozen)
            {
                _updatesSincePopulated++;
                if (_updatesSincePopulated >= 2)
                {
                    FreezeLayoutOnce();
                }
            }
        }

        private void UpdateDirectConnPingField()
        {
            if (DirectConnPingField == null) return;

            bool shouldShow = false;
            int rttMs = 0;
            if (RemotePlayer != null &&
                BasisNetworkPlayers.PlayerToNetworkedPlayer(RemotePlayer, out var net))
            {
                shouldShow = BasisP2PManager.TryGetP2PRoundTripTime(net.playerId, out rttMs);
            }

            // Toggle visibility first: a tween started on a disabled object never ticks, so the
            // grade below has to run against a card that is already on screen.
            if (shouldShow != _pingVisible)
            {
                _pingVisible = shouldShow;
                DirectConnPingField.SetActive(shouldShow);
                if (!shouldShow)
                {
                    _pingSeverity = BasisPanelSeverity.None;
                    BasisPanelTint.Apply(DirectConnPingTint, BasisPanelSeverity.None, false);
                }

                // Rebuilding the panel root measures this group before the group has resized
                // itself, so the row is revealed but nothing below it moves.
                if (DirectConnRebuildFrom != null)
                {
                    PanelElementDescriptor.RebuildLayoutChain(DirectConnRebuildFrom, DirectConnRebuildStopAt);
                }
                else
                {
                    PanelDescriptor?.ForceRebuild();
                }
            }

            if (shouldShow)
            {
                DirectConnPingField.SetDescription(
                    BasisLocalization.Get("menu.individualPlayer.directConnection.ping.value", rttMs));

                _pingSeverity = BasisPanelTint.Grade(rttMs, PingCautionRtt, PingHotRtt, _pingSeverity);
                BasisPanelTint.Apply(DirectConnPingTint, _pingSeverity);
            }
        }

        private void UpdateBufferField()
        {
            if (BufferField == null) return;

            if (RemotePlayer == null || RemotePlayer.NetworkReceiver == null)
            {
                BufferField.SetDescription(BasisLocalization.Get("menu.players.panel.noReceiver"));
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
                // The cone and the talker's mouth directivity are part filter now, so
                // fold in the shelves' speech-weighted equivalent or "Effective" lies.
                float tone = BasisVoiceAcoustics.ShelfBroadbandGain(
                    audio.DirectivityShelfDb, audio.ConeShelfDb);
                float effective = srcVol * dampen * tone * listenerVol;

                string dampenNote = dampen < 0.5f ? " (BEHIND)" : dampen < 1f ? " (off-axis)" : "";
                string warning = effective < 0.01f && srcVol > 0f ? "\nWARNING: Near zero!" : "";
                string normalize = audio.NormalizeLoudness
                    ? $"\nNormalize: {audio.NormalizerGainDb:+0.0;-0.0;0.0} dB"
                    : "\nNormalize: off";

                VolumeChainField.SetDescription(
                    $"Source: {srcVol:F2} x Dampen: {dampen:F3}{dampenNote} x Listener: {listenerVol:F2}\n" +
                    $"Tone: {tone:F3} (mouth {audio.DirectivityShelfDb:0.0} dB, cone {audio.ConeShelfDb:0.0} dB)\n" +
                    $"Effective: {effective:F3}{warning}{normalize}");
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
                bool init = hasDriver && audio.BasisRemoteVisemeAudioDriver.Initialized;

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

        private void UpdateAvatarDataDebugFields()
        {
            if (RemotePlayer == null || RemotePlayer.NetworkReceiver == null)
            {
                SetAvatarDataDebugAll("No receiver");
                return;
            }

            var receiver = RemotePlayer.NetworkReceiver;

            if (AvatarReceiveField != null && BasisSettingsDefaults.AvatarDataDebugShowReceive.RawValue)
            {
                byte highest = receiver.HighestSequence;
                int seen = receiver.SeenPackets;
                int queued = receiver.PayloadQueue.Count;
                int newThisTick = _lastHighestSeqValid ? unchecked((byte)(highest - _lastHighestSequence)) : 0;
                _lastHighestSequence = highest;
                _lastHighestSeqValid = true;
                string state = seen == 0 ? "None" : seen == 1 ? "Initial" : "Streaming";

                AvatarReceiveField.SetDescription(
                    $"State: {state} | Queued: {queued}\n" +
                    $"Highest Seq: {highest} | New this tick: +{newThisTick}");
            }

            if (AvatarStagingField != null && BasisSettingsDefaults.AvatarDataDebugShowStaging.RawValue)
            {
                var cur = receiver.Current;
                var nxt = receiver.Next;
                bool hasCur = receiver.HasCurrentBuffer && cur != null;
                bool hasNxt = receiver.HasNextBuffer && nxt != null;
                double windowMs = (hasCur && hasNxt) ? (nxt.ServerTimeSeconds - cur.ServerTimeSeconds) * 1000.0 : 0.0;

                AvatarStagingField.SetDescription(
                    $"Staged: {receiver.StagedCount} | Data Ready: {(receiver.IsDataReady ? "Yes" : "No")}\n" +
                    $"Cur Seq: {(hasCur ? cur.Sequence.ToString() : "-")} | Next Seq: {(hasNxt ? nxt.Sequence.ToString() : "-")}\n" +
                    $"Window: {windowMs:F1}ms");
            }

            if (AvatarInterpField != null && BasisSettingsDefaults.AvatarDataDebugShowInterp.RawValue)
            {
                AvatarInterpField.SetDescription(
                    $"t: {receiver.InterpolationTimeDebug:F3} | Rate: {receiver.LastPlaybackRate:F3}x (0.85-1.35)\n" +
                    $"Jitter Depth: {receiver.DynamicJitterDepth:F2} (min {BasisNetworkReceiver.MinJitterDepth} / max {BasisNetworkReceiver.MaxJitterDepth})\n" +
                    $"Buffer Holds: {(receiver.HasBufferHolds ? "Yes" : "No")}");
            }

            if (AvatarMetaField != null && BasisSettingsDefaults.AvatarDataDebugShowMeta.RawValue)
            {
                receiver.GetLatestNetworkPose(out var pos, out _, out var scale);
                float[] eom = receiver.EyesAndMouth;
                float mouth = eom != null && eom.Length > 4 ? eom[4] : 0f;

                AvatarMetaField.SetDescription(
                    $"Scale: {receiver.ApplyingScale.x:F2} | Human: {receiver.CachedHumanScaleDebug:F2} | Net: {scale.x:F2}\n" +
                    $"Hips: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})\n" +
                    $"LOD: {RemotePlayer.CurrentLodLevel} | PoseSkip: {RemotePlayer.PoseSkipCounter}\n" +
                    $"InAvatar: {(RemotePlayer.InAvatarRange ? "Y" : "N")} | OutOfRange: {(RemotePlayer.OutOfRangeFromLocal ? "Y" : "N")} | Mouth: {mouth:F2}");
            }
        }

        private void SetAvatarDataDebugAll(string message)
        {
            if (AvatarReceiveField != null) AvatarReceiveField.SetDescription(message);
            if (AvatarStagingField != null) AvatarStagingField.SetDescription(message);
            if (AvatarInterpField != null) AvatarInterpField.SetDescription(message);
            if (AvatarMetaField != null) AvatarMetaField.SetDescription(message);
        }

        private void SetAll(string message)
        {
            if (DebugField != null) DebugField.SetDescription(message);
            if (DistanceField != null) DistanceField.SetDescription(message);
            if (LodField != null) LodField.SetDescription(message);
            if (RangesField != null) RangesField.SetDescription(message);
            if (BufferField != null) BufferField.SetDescription(message);
            SetAudioDebugAll(message);
            SetAvatarDataDebugAll(message);
        }
    }
}
