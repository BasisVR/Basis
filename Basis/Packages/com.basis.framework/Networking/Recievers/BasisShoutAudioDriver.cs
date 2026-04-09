using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
#if !UNITY_SERVER
using OpusSharp.Core;
using Dynamic = OpusSharp.Core.Dynamic;
using Static = OpusSharp.Core.Static;
#endif
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Receivers
{
    /// <summary>
    /// Global driver for shout-mode audio sources.
    /// Shout audio sources are NOT parented to remote players and are NOT
    /// affected by distance culling, LOD, avatar unloading, or spatialization.
    /// Each shouting player gets one non-spatialized (2D) AudioSource parented
    /// to BasisDeviceManagement.Instance so it persists across scene loads.
    /// </summary>
    public static class BasisShoutAudioDriver
    {
        /// <summary>
        /// Per-player shout audio state.
        /// </summary>
        private class ShoutAudioEntry
        {
            public ushort PlayerId;
            public BasisAudioReceiver Receiver;
            public AudioSource AudioSource;
            public BasisRemoteAudioDriver Driver;
        }

        private static readonly Dictionary<ushort, ShoutAudioEntry> _entries = new Dictionary<ushort, ShoutAudioEntry>();

        /// <summary>
        /// Enables shout mode for a player. Creates a non-spatialized audio source
        /// on BasisDeviceManagement.Instance, independent of the remote player hierarchy.
        /// </summary>
        public static void EnableShoutMode(ushort playerId)
        {
#if UNITY_SERVER
            BasisDebug.LogWarning($"Ignoring shout audio enable for player {playerId} on server/headless build.");
            return;
#else
            if (_entries.ContainsKey(playerId))
            {
                return; // already active
            }

            if (BasisDeviceManagement.Instance == null)
            {
                BasisDebug.LogError("BasisDeviceManagement.Instance is null, cannot create shout audio source.");
                return;
            }

            var entry = new ShoutAudioEntry();
            entry.PlayerId = playerId;

            // Create a new BasisAudioReceiver for the shout channel
            entry.Receiver = new BasisAudioReceiver();

            // Initialize the decoder
            BasisAudioReceiver.outputSampleRate = AudioSettings.outputSampleRate;
            BasisAudioReceiver.silentData ??= new float[RemoteOpusSettings.FrameSize];

#if UNITY_IOS && !UNITY_EDITOR
            entry.Receiver.decoder = new Static.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#else
            entry.Receiver.decoder = new Dynamic.OpusDecoder(RemoteOpusSettings.NetworkSampleRate, RemoteOpusSettings.Channels);
#endif

            // Add AudioSource directly to BasisDeviceManagement.Instance
            Transform parent = BasisDeviceManagement.Instance.transform;
            entry.AudioSource = parent.gameObject.AddComponent<AudioSource>();
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
            entry.Driver = parent.gameObject.AddComponent<BasisRemoteAudioDriver>();
            entry.Driver.BasisAudioReceiver = entry.Receiver;

            entry.Receiver.audioSource = entry.AudioSource;
            entry.Receiver.AudioSourceTransform = parent;
            entry.Receiver.DirectionalDampeningMultiplier = 1f;

            // Initialize audio processing buffers BEFORE setting HasAudioSource.
            // Without this, OnAudioFilterRead runs with null scratch buffers = buzzing.
            entry.Receiver.InitializeForPlayback();

            // Now safe to enable - OnAudioFilterRead can process correctly
            entry.Receiver.HasAudioSource = true;

            // Wire up the player's existing viseme driver so lip-sync works during shout mode
            if (BasisNetworkPlayers.RemotePlayers.TryGetValue(playerId, out BasisNetworkReceiver receiver))
            {
                entry.Driver.Initalize(receiver.AudioReceiverModule.visemeDriver);
            }
            else
            {
                entry.Driver.Initalized = true;
            }

            entry.AudioSource.Play();

            _entries[playerId] = entry;
            BasisDebug.Log($"Shout audio enabled for player {playerId}");
#endif
        }

        /// <summary>
        /// Disables shout mode for a player. Destroys their audio components.
        /// </summary>
        public static void DisableShoutMode(ushort playerId)
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

            if (entry.Driver != null)
            {
                // Detach the shared viseme driver before destroying so OnDestroy
                // doesn't clean up the player's viseme driver
                entry.Driver.BasisAudioAndVisemeDriver = null;
                entry.Driver.Initalized = false;
                Object.Destroy(entry.Driver);
            }

            _entries.Remove(playerId);
            BasisDebug.Log($"Shout audio disabled for player {playerId}");
        }

        /// <summary>
        /// Returns true if a player currently has an active shout audio source.
        /// </summary>
        public static bool IsInShoutMode(ushort playerId)
        {
            return _entries.ContainsKey(playerId);
        }

        /// <summary>
        /// Inserts an audio segment into the shout receiver's jitter buffer.
        /// Auto-enables shout mode if not already active.
        /// </summary>
        public static void ReceiveShoutAudio(ushort playerId, AudioSegmentDataMessage audioData)
        {
            if (!_entries.TryGetValue(playerId, out var entry))
            {
                // Auto-enable when we receive shout audio (handles late joiners)
                EnableShoutMode(playerId);
                if (!_entries.TryGetValue(playerId, out entry))
                {
                    return; // failed to create
                }
            }

            entry.Receiver.Insert(audioData);

            // Notify the player's nameplate that audio was received so it shows the talking state
            if (BasisNetworkPlayers.RemotePlayers.TryGetValue(playerId, out BasisNetworkReceiver receiver))
            {
                receiver.Player.AudioReceived?.Invoke();
            }
        }

        /// <summary>
        /// Must be called each frame to drain jitter buffers and decode audio.
        /// </summary>
        public static void DrainAll()
        {
            foreach (var kvp in _entries)
            {
                kvp.Value.Receiver.DrainAndDecode();
            }
        }

        /// <summary>
        /// Cleans up a player's shout state (call on disconnect).
        /// </summary>
        public static void RemovePlayer(ushort playerId)
        {
            DisableShoutMode(playerId);
        }

        /// <summary>
        /// Cleans up all shout audio sources and resets local shout state (call on disconnect from server).
        /// </summary>
        public static void DeInitialize()
        {
            var keys = new List<ushort>(_entries.Keys);
            foreach (var key in keys)
            {
                DisableShoutMode(key);
            }

            // Reset local player shout state
            Basis.Scripts.Networking.Transmitters.BasisAudioTransmission.IsInShoutMode = false;
        }
    }
}
