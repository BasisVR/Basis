using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.VoiceRecording;
#if !UNITY_SERVER
#endif
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Receivers
{
    /// <summary>
    /// Global driver for announce-mode audio sources.
    /// Announce audio sources are NOT parented to remote players and are NOT
    /// affected by distance culling, LOD, avatar unloading, or spatialization.
    /// Each announcing player gets one non-spatialized (2D) AudioSource parented
    /// to BasisDeviceManagement.Instance so it persists across scene loads.
    /// </summary>
    public static class BasisAnnounceAudioDriver
    {
        /// <summary>
        /// Per-player announce audio state.
        /// </summary>
        private class AnnounceAudioEntry
        {
            public ushort PlayerId;
            public BasisAudioReceiver Receiver;
            public AudioSource AudioSource;
            public BasisRemoteAudioDriver Driver;
            public GameObject Root;

            /// <summary>
            /// The player's own viseme driver, borrowed for the duration of the announce. Held here
            /// so teardown can hand it back without a receiver lookup that may already be gone.
            /// </summary>
            public BasisAudioAndVisemeDriver VisemeDriver;
        }

        private static readonly Dictionary<ushort, AnnounceAudioEntry> _entries = new Dictionary<ushort, AnnounceAudioEntry>();

        /// <summary>
        /// Enables announce mode for a player. Creates a non-spatialized audio source
        /// on BasisDeviceManagement.Instance, independent of the remote player hierarchy.
        /// </summary>
        public static void EnableAnnounceMode(ushort playerId)
        {
#if UNITY_SERVER
            BasisDebug.LogWarning($"Ignoring announce audio enable for player {playerId} on server/headless build.");
            return;
#else
            if (_entries.ContainsKey(playerId))
            {
                return; // already active
            }

            if (BasisDeviceManagement.Instance == null)
            {
                BasisDebug.LogError("BasisDeviceManagement.Instance is null, cannot create announce audio source.");
                return;
            }

            var entry = new AnnounceAudioEntry();
            entry.PlayerId = playerId;

            // Create a new BasisAudioReceiver for the announce channel
            entry.Receiver = new BasisAudioReceiver();

            // Initialize the decoder
            BasisAudioReceiver.outputSampleRate = AudioSettings.outputSampleRate;
            BasisAudioReceiver.silentData ??= new float[RemoteOpusSettings.MaxFrameSize];

#if UNITY_IOS && !UNITY_EDITOR
            entry.Receiver.decoder = new OpusSharp.Core.Static.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#else
            entry.Receiver.decoder = new OpusSharp.Core.Dynamic.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#endif

            // Own GameObject per announcer: OnAudioFilterRead scripts run for every
            // AudioSource on the same GameObject, so shared hosting breaks with
            // multiple simultaneous announcers.
            entry.Root = new GameObject($"Announce Audio {playerId}");
            entry.Root.transform.SetParent(BasisDeviceManagement.Instance.transform, false);
            entry.AudioSource = entry.Root.AddComponent<AudioSource>();
            entry.AudioSource.clip = BasisAudioClipPool.Get(playerId);
            entry.AudioSource.loop = true;

            // Non-spatialized settings: pure 2D audio
            entry.AudioSource.spatialBlend = 0f;
            entry.AudioSource.spatialize = false;
            entry.AudioSource.spatializePostEffects = false;
            entry.AudioSource.dopplerLevel = 0f;
            entry.AudioSource.spread = 0f;
            entry.AudioSource.minDistance = 0f;
            entry.AudioSource.maxDistance = float.MaxValue;
            entry.AudioSource.rolloffMode = AudioRolloffMode.Linear;
            entry.AudioSource.volume = 1f;

            // Wire up the audio driver so OnAudioFilterRead fires
            entry.Driver = entry.Root.AddComponent<BasisRemoteAudioDriver>();
            entry.Driver.BasisAudioReceiver = entry.Receiver;

            // This source, not the player's silent spatial one, feeds lip-sync for the duration
            // of the announce. See BasisRemoteAudioDriver.OwnsVisemeTap.
            entry.Driver.IsAnnounceSource = true;

            entry.Receiver.audioSource = entry.AudioSource;
            entry.Receiver.AudioSourceTransform = entry.Root.transform;
            entry.Receiver.DirectionalDampeningMultiplier = 1f;

            // Initialize audio processing buffers BEFORE setting HasAudioSource.
            // Without this, OnAudioFilterRead runs with null scratch buffers = buzzing.
            entry.Receiver.InitializeForPlayback();

            // Now safe to enable - OnAudioFilterRead can process correctly
            entry.Receiver.HasAudioSource = true;

            // Wire up the player's existing viseme driver so lip-sync works during announce mode
            if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(playerId, out BasisNetworkReceiver receiver))
            {
                BasisAudioAndVisemeDriver viseme = receiver.AudioReceiverModule.visemeDriver;
                entry.VisemeDriver = viseme;

                // Order matters here. By the time an announce starts, the normal path has usually
                // already retired this driver: the viseme distance cutoff drops it out of
                // ActiveDrivers, and going out of hearing range pools the player's spatial
                // AudioSource, whose ResetForPool unregisters the driver and releases its
                // OpenLipSync context outright. So flag it first (SetVisemeRange honours the flag
                // and stops the distance pass fighting us), force it back in range, and only then
                // Initialize — which re-registers it when the pool return had dropped it, and adds
                // it to ActiveDrivers because InVisemeRange is true again by that point.
                viseme.AnnounceActive = true;
                BasisRemoteAudioDriver.SetVisemeRange(viseme, true);
                entry.Driver.Initialize(viseme);
            }
            else
            {
                entry.Driver.Initialized = true;
            }

            entry.AudioSource.Play();

            _entries[playerId] = entry;
            BasisVoiceRecording.OnAnnounceReceiverCreated(playerId, entry.Receiver);
            BasisDebug.Log($"Announce audio enabled for player {playerId}");
#endif
        }

        /// <summary>
        /// Disables announce mode for a player. Destroys their audio components.
        /// </summary>
        public static void DisableAnnounceMode(ushort playerId)
        {
            if (!_entries.TryGetValue(playerId, out var entry))
            {
                return;
            }

            entry.Receiver.HasAudioSource = false;

#if !UNITY_SERVER
            if (entry.Receiver.decoder != null)
            {
                entry.Receiver.decoder.Dispose();
                entry.Receiver.decoder = null;
            }
#endif

            if (entry.AudioSource != null)
            {
                entry.AudioSource.Stop();
                if (entry.AudioSource.clip != null)
                {
                    BasisAudioClipPool.Return(entry.AudioSource.clip);
                }
                Object.Destroy(entry.AudioSource);
            }

            if (entry.VisemeDriver != null)
            {
                // Hand the driver back to the distance rule; the next transmission tick recomputes
                // InVisemeRange and retires it if they really are too far to read.
                entry.VisemeDriver.AnnounceActive = false;

                // If the player's own spatial AudioSource is not currently holding this driver —
                // the out-of-range announcer, whose source was pooled — then the announce path was its
                // only owner and it has to be retired here, or it dangles in the static registry
                // being ticked every frame with nothing left to feed it.
                bool spatialPathOwnsIt =
                    BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(playerId, out BasisNetworkReceiver receiver)
                    && receiver.AudioReceiverModule != null
                    && receiver.AudioReceiverModule.HasAudioSource;

                if (!spatialPathOwnsIt)
                {
                    BasisRemoteAudioDriver.SetVisemeRange(entry.VisemeDriver, false);
                    BasisRemoteAudioDriver.UnregisterDriver(entry.VisemeDriver);
                }
                entry.VisemeDriver = null;
            }

            if (entry.Driver != null)
            {
                // Detach the shared viseme driver before destroying so OnDestroy
                // doesn't clean up the player's viseme driver
                entry.Driver.BasisAudioAndVisemeDriver = null;
                entry.Driver.Initialized = false;
                Object.Destroy(entry.Driver);
            }

            if (entry.Root != null)
            {
                Object.Destroy(entry.Root);
            }

            _entries.Remove(playerId);
            BasisDebug.Log($"Announce audio disabled for player {playerId}");
        }

        /// <summary>
        /// Returns true if a player currently has an active announce audio source.
        /// </summary>
        public static bool IsInAnnounceMode(ushort playerId)
        {
            return _entries.ContainsKey(playerId);
        }

        /// <summary>
        /// Exposes a announcing player's receiver so the voice-recording tap can follow the
        /// announce audio path. Returns false when the player is not currently announcing.
        /// </summary>
        internal static bool TryGetReceiver(ushort playerId, out BasisAudioReceiver receiver)
        {
            if (_entries.TryGetValue(playerId, out AnnounceAudioEntry entry))
            {
                receiver = entry.Receiver;
                return true;
            }
            receiver = null;
            return false;
        }

        /// <summary>
        /// Inserts an audio segment into the announce receiver's jitter buffer.
        /// Auto-enables announce mode if not already active.
        /// </summary>
        public static void ReceiveAnnounceAudio(ushort playerId, AudioSegmentDataMessage audioData)
        {
            if (!_entries.TryGetValue(playerId, out var entry))
            {
                // Auto-enable when we receive announce audio (handles late joiners)
                EnableAnnounceMode(playerId);
                if (!_entries.TryGetValue(playerId, out entry))
                {
                    return; // failed to create
                }
            }

            entry.Receiver.Insert(audioData);

            // Notify the player's nameplate that audio was received so it shows the talking state
            if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(playerId, out BasisNetworkReceiver receiver))
            {
                receiver.Player.AudioReceived?.Invoke();
            }
        }

        private static BasisAudioReceiver[] _computeSnapshot = System.Array.Empty<BasisAudioReceiver>();
        private static int _computeCount;

        public static void PublishComputeSnapshot()
        {
            int count = _entries.Count;
            if (_computeSnapshot.Length < count)
            {
                _computeSnapshot = new BasisAudioReceiver[System.Math.Max(4, count * 2)];
            }
            int index = 0;
            foreach (var kvp in _entries)
            {
                _computeSnapshot[index++] = kvp.Value.Receiver;
            }
            _computeCount = index;
        }

        public static void ComputeAll()
        {
            for (int index = 0; index < _computeCount; index++)
            {
                var receiver = _computeSnapshot[index];
                if (!receiver.IsAudioActive || receiver.VoiceBuffer.DecodedFrameCount == 0)
                {
                    receiver.DrainAndDecodeThreadSafe();
                }
            }
        }

        /// <summary>
        /// Must be called each frame to drain jitter buffers and decode audio.
        /// </summary>
        public static void DrainAll()
        {
            foreach (var kvp in _entries)
            {
                kvp.Value.Receiver.ApplyAudioState();
            }
        }

        /// <summary>
        /// Cleans up a player's announce state (call on disconnect).
        /// </summary>
        public static void RemovePlayer(ushort playerId)
        {
            DisableAnnounceMode(playerId);
        }

        /// <summary>
        /// Cleans up all announce audio sources and resets local announce state (call on disconnect from server).
        /// </summary>
        public static void DeInitialize()
        {
            var keys = new List<ushort>(_entries.Keys);
            foreach (var key in keys)
            {
                DisableAnnounceMode(key);
            }

            // Reset local player announce state
            Basis.Scripts.Networking.Transmitters.BasisAudioTransmission.IsInAnnounceMode = false;
        }
    }
}
