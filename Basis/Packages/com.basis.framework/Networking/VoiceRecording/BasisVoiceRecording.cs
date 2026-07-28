using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.VoiceRecording
{
    /// <summary>Consent decision carried by a voice-record consent message.</summary>
    public enum BasisVoiceConsentState : byte
    {
        Denied = 0,
        Granted = 1,
        Revoked = 2,
    }

    /// <summary>What a consent grant authorizes. Consent is scoped so a world-initiated route
    /// request cannot silently unlock trusted disk/PCM recording.</summary>
    public enum BasisVoiceConsentPurpose : byte
    {
        /// <summary>Capture the voice to PCM / clip / disk (trusted local API only).</summary>
        Record = 0,
        /// <summary>Re-emit the voice from a world object (issue #911; the only Cilbox-reachable purpose).</summary>
        Route = 1,
    }

    /// <summary>
    /// Consent-gated voice capture and re-routing for remote players. A local caller asks a
    /// remote player for permission (<see cref="StartRecording"/> / <see cref="RequestPlayFromTransform"/>);
    /// once the recordee grants it, this taps the decoded voice this client already receives
    /// and exposes it as live PCM, an in-memory clip, a .wav file, and/or a spatialized 3D
    /// source (issue #911). The recordee controls consent, sees who is recording them, and can
    /// revoke at any time; recording auto-stops when either party leaves.
    ///
    /// This is the trusted, full-surface API. Untrusted world scripts (Cilbox) must go through
    /// the restricted shim, never this class directly — this exposes raw PCM and disk output.
    /// </summary>
    public static class BasisVoiceRecording
    {
        public const int DefaultConsentTimeoutMs = 15000;
        private const float RequestRateLimitSeconds = 3f;
        private const int MaxObjectSourcesPerPlayer = 8;
        private const int MaxObjectSourcesTotal = 32;
        private const float GlobalPromptCooldownSeconds = 2f;

        // Recorder side.
        private static readonly Dictionary<ushort, Capture> _captures = new Dictionary<ushort, Capture>();
        private static readonly Dictionary<int, PendingRequest> _pending = new Dictionary<int, PendingRequest>();
        private static readonly HashSet<ushort> _grantedRecord = new HashSet<ushort>();
        private static readonly HashSet<ushort> _grantedRoute = new HashSet<ushort>();

        // Recordee side.
        private static readonly HashSet<ushort> _recordersOfMe = new HashSet<ushort>();
        private static readonly Dictionary<ushort, float> _lastRequestTime = new Dictionary<ushort, float>();
        private static float _lastPromptTime = -999f;

        private static readonly float[] _tickScratch = new float[RemoteOpusSettings.MaxFrameSize * 4];

        /// <summary>A recordee granted us consent (recorder side).</summary>
        public static event Action<ushort> OnConsentGranted;
        /// <summary>A recordee denied our request (recorder side).</summary>
        public static event Action<ushort> OnConsentDenied;
        /// <summary>A recordee revoked previously-granted consent (recorder side).</summary>
        public static event Action<ushort> OnConsentRevoked;
        public static event Action<ushort, BasisRecording> OnRecordingStarted;
        public static event Action<ushort, BasisRecording> OnRecordingStopped;
        /// <summary>Decoded mono 48 kHz PCM for an active recording, raised on the main thread.
        /// The float[] is a shared reusable buffer — copy it if you retain it past the callback.</summary>
        public static event Action<ushort, float[], int> OnSamples;
        /// <summary>The set of players allowed to record the local user changed.</summary>
        public static event Action OnRecordersOfMeChanged;

        /// <summary>Players currently permitted to record the local user.</summary>
        public static IReadOnlyCollection<ushort> RecordersOfMe => _recordersOfMe;

        /// <summary>Whether the recordee has granted us permission to record their voice.</summary>
        public static bool HasConsent(ushort playerId) => _grantedRecord.Contains(playerId);
        /// <summary>Whether the recordee has granted us permission to re-emit their voice from an object.</summary>
        public static bool HasRouteConsent(ushort playerId) => _grantedRoute.Contains(playerId);
        public static bool IsRecording(ushort playerId) => _captures.TryGetValue(playerId, out Capture c) && c.Recording != null;

        // ==================== Recorder API ====================

        /// <summary>
        /// Asks <paramref name="target"/> for permission and, once granted, begins capturing
        /// their voice per <paramref name="options"/>. Returns null if denied, timed out, or
        /// the player can't be resolved.
        /// </summary>
        public static async Task<BasisRecordingHandle> StartRecording(BasisRemotePlayer target, BasisRecordingOptions options, int timeoutMs = DefaultConsentTimeoutMs)
        {
            if (!TryGetPlayerId(target, out ushort id))
            {
                return null;
            }
            if (!_grantedRecord.Contains(id) && !await RequestConsent(id, BasisVoiceConsentPurpose.Record, timeoutMs))
            {
                return null;
            }
            return BeginCapture(id, target.UUID, options);
        }

        public static void StopRecording(BasisRecordingHandle handle)
        {
            if (handle == null || !_captures.TryGetValue(handle.PlayerId, out Capture cap) || cap.Recording != handle.Recording)
            {
                return;
            }
            StopRecordingInternal(handle.PlayerId, cap);
        }

        /// <summary>Stops any active recording of <paramref name="playerId"/> regardless of who started it.</summary>
        public static void StopRecording(ushort playerId)
        {
            if (_captures.TryGetValue(playerId, out Capture cap) && cap.Recording != null)
            {
                StopRecordingInternal(playerId, cap);
            }
        }

        private static void StopRecordingInternal(ushort playerId, Capture cap)
        {
            BasisRecording rec = cap.Recording;
            cap.Recording = null;
            cap.RaiseLiveSamples = false;
            cap.RefreshTaps();
            rec.FinalizeRecording();
            OnRecordingStopped?.Invoke(playerId, rec);
            RemoveCaptureIfEmpty(playerId, cap);
        }

        /// <summary>
        /// Routes <paramref name="target"/>'s voice to a 3D source at <paramref name="anchor"/>
        /// (issue #911). Requires consent already held; use <see cref="RequestPlayFromTransform"/>
        /// to request it first. The returned source never yields PCM to the caller.
        /// </summary>
        public static BasisVoiceObjectSource PlayFromTransform(BasisRemotePlayer target, Transform anchor, float minDistance = 1f, float maxDistance = 20f)
        {
            if (anchor == null || !TryGetPlayerId(target, out ushort id) || !_grantedRoute.Contains(id))
            {
                return null;
            }
            if (CountObjectSources() >= MaxObjectSourcesTotal)
            {
                return null;
            }
            if (_captures.TryGetValue(id, out Capture existingCap) && existingCap.ObjectSourceCount >= MaxObjectSourcesPerPlayer)
            {
                return null;
            }
            BasisVoiceObjectSource source = new BasisVoiceObjectSource(id);
            if (!source.Initialize(anchor, minDistance, maxDistance))
            {
                source.Dispose();
                return null;
            }
            if (!_captures.TryGetValue(id, out Capture cap))
            {
                cap = new Capture(id);
                _captures[id] = cap;
            }
            cap.AddObjectSource(source);
            cap.RefreshTaps();
            return source;
        }

        /// <summary>Requests consent if not already held, then routes the voice to <paramref name="anchor"/>.</summary>
        public static async Task<BasisVoiceObjectSource> RequestPlayFromTransform(BasisRemotePlayer target, Transform anchor, float minDistance = 1f, float maxDistance = 20f, int timeoutMs = DefaultConsentTimeoutMs)
        {
            if (anchor == null || !TryGetPlayerId(target, out ushort id))
            {
                return null;
            }
            if (!_grantedRoute.Contains(id) && !await RequestConsent(id, BasisVoiceConsentPurpose.Route, timeoutMs))
            {
                return null;
            }
            return PlayFromTransform(target, anchor, minDistance, maxDistance);
        }

        public static void StopPlayFromTransform(BasisVoiceObjectSource source)
        {
            if (source == null || !_captures.TryGetValue(source.PlayerId, out Capture cap))
            {
                return;
            }
            cap.RemoveObjectSource(source);
            source.Dispose();
            cap.RefreshTaps();
            RemoveCaptureIfEmpty(source.PlayerId, cap);
        }

        // ==================== Recordee API ====================

        /// <summary>Revokes a previously-granted permission so <paramref name="recorderId"/> stops recording us.</summary>
        public static void RevokeConsentToRecorder(ushort recorderId)
        {
            if (_recordersOfMe.Remove(recorderId))
            {
                BasisNetworkHandleVoiceRecord.SendConsent(recorderId, (byte)BasisVoiceConsentState.Revoked, (byte)BasisVoiceConsentPurpose.Record);
                OnRecordersOfMeChanged?.Invoke();
            }
        }

        // ==================== Consent transport callbacks ====================

        internal static void HandleIncomingRequest(ushort recorderId, byte purposeByte)
        {
            BasisVoiceConsentPurpose purpose = (BasisVoiceConsentPurpose)purposeByte;
            if (IsRateLimited(recorderId))
            {
                return;
            }
            string uuid = ResolveUuid(recorderId);
            switch (BasisRecordingConsent.GetPolicy(uuid))
            {
                case BasisRecordingConsentPolicy.AlwaysAllow:
                    GrantToRecorder(recorderId, purpose);
                    break;
                case BasisRecordingConsentPolicy.AlwaysDeny:
                    BasisNetworkHandleVoiceRecord.SendConsent(recorderId, (byte)BasisVoiceConsentState.Denied, purposeByte);
                    break;
                default:
                    ShowPrompt(recorderId, uuid, purpose);
                    break;
            }
        }

        internal static void HandleIncomingConsent(ushort responderId, byte stateByte, byte purposeByte)
        {
            BasisVoiceConsentState state = (BasisVoiceConsentState)stateByte;
            BasisVoiceConsentPurpose purpose = (BasisVoiceConsentPurpose)purposeByte;
            HashSet<ushort> set = purpose == BasisVoiceConsentPurpose.Route ? _grantedRoute : _grantedRecord;
            switch (state)
            {
                case BasisVoiceConsentState.Granted:
                    set.Add(responderId);
                    ResolvePending(PendingKey(responderId, purpose), true);
                    OnConsentGranted?.Invoke(responderId);
                    break;
                case BasisVoiceConsentState.Denied:
                    set.Remove(responderId);
                    ResolvePending(PendingKey(responderId, purpose), false);
                    OnConsentDenied?.Invoke(responderId);
                    break;
                case BasisVoiceConsentState.Revoked:
                    _grantedRecord.Remove(responderId);
                    _grantedRoute.Remove(responderId);
                    ResolvePending(PendingKey(responderId, BasisVoiceConsentPurpose.Record), false);
                    ResolvePending(PendingKey(responderId, BasisVoiceConsentPurpose.Route), false);
                    StopAllForPlayer(responderId);
                    OnConsentRevoked?.Invoke(responderId);
                    break;
            }
        }

        // ==================== Lifecycle hooks ====================

        /// <summary>Drains active captures and resolves consent timeouts. Called once per frame.</summary>
        internal static void Tick()
        {
            if (_pending.Count > 0)
            {
                CheckPendingTimeouts();
            }
            if (_captures.Count == 0)
            {
                return;
            }
            Capture[] snapshot = new Capture[_captures.Count];
            _captures.Values.CopyTo(snapshot, 0);
            for (int i = 0; i < snapshot.Length; i++)
            {
                Capture cap = snapshot[i];
                if (cap.Recording != null && cap.DecodedSink != null)
                {
                    int n;
                    while ((n = cap.DecodedSink.Drain(_tickScratch, _tickScratch.Length)) > 0)
                    {
                        cap.Recording.Append(_tickScratch, n);
                        if (cap.RaiseLiveSamples)
                        {
                            OnSamples?.Invoke(cap.PlayerId, _tickScratch, n);
                        }
                    }
                }
                cap.TickObjectSources();
                if (cap.Recording == null && cap.ObjectSourceCount == 0)
                {
                    cap.DetachTaps();
                    _captures.Remove(cap.PlayerId);
                }
            }
        }

        /// <summary>Called when a shout source is (re)created so an active tap follows the shout receiver.</summary>
        internal static void OnShoutReceiverCreated(ushort playerId, BasisAudioReceiver shoutReceiver)
        {
            if (_captures.TryGetValue(playerId, out Capture cap))
            {
                cap.RefreshTaps();
            }
        }

        internal static void OnPlayerLeft(ushort playerId)
        {
            StopAllForPlayer(playerId);
            _grantedRecord.Remove(playerId);
            _grantedRoute.Remove(playerId);
            ResolvePending(PendingKey(playerId, BasisVoiceConsentPurpose.Record), false);
            ResolvePending(PendingKey(playerId, BasisVoiceConsentPurpose.Route), false);
            _lastRequestTime.Remove(playerId);
            if (_recordersOfMe.Remove(playerId))
            {
                OnRecordersOfMeChanged?.Invoke();
            }
        }

        /// <summary>Tears down all captures and consent state on disconnect.</summary>
        internal static void DeInitialize()
        {
            ushort[] ids = new ushort[_captures.Count];
            _captures.Keys.CopyTo(ids, 0);
            for (int i = 0; i < ids.Length; i++)
            {
                StopAllForPlayer(ids[i]);
            }
            _captures.Clear();
            _grantedRecord.Clear();
            _grantedRoute.Clear();
            _recordersOfMe.Clear();
            _lastRequestTime.Clear();
            foreach (PendingRequest p in _pending.Values)
            {
                p.Tcs.TrySetResult(false);
            }
            _pending.Clear();
        }

        // ==================== Internals ====================

        private static int PendingKey(ushort id, BasisVoiceConsentPurpose purpose)
        {
            return (id << 1) | ((int)purpose & 1);
        }

        private static Task<bool> RequestConsent(ushort id, BasisVoiceConsentPurpose purpose, int timeoutMs)
        {
            int key = PendingKey(id, purpose);
            if (_pending.TryGetValue(key, out PendingRequest existing))
            {
                return existing.Tcs.Task;
            }
            PendingRequest pending = new PendingRequest
            {
                Tcs = new TaskCompletionSource<bool>(),
                Deadline = Time.realtimeSinceStartup + Mathf.Max(1, timeoutMs) / 1000f,
            };
            _pending[key] = pending;
            BasisNetworkHandleVoiceRecord.SendRecordRequest(id, (byte)purpose);
            return pending.Tcs.Task;
        }

        private static void ResolvePending(int key, bool result)
        {
            if (_pending.TryGetValue(key, out PendingRequest pending))
            {
                _pending.Remove(key);
                pending.Tcs.TrySetResult(result);
            }
        }

        private static void CheckPendingTimeouts()
        {
            float now = Time.realtimeSinceStartup;
            List<int> expired = null;
            foreach (KeyValuePair<int, PendingRequest> kv in _pending)
            {
                if (now >= kv.Value.Deadline)
                {
                    (expired ??= new List<int>()).Add(kv.Key);
                }
            }
            if (expired == null)
            {
                return;
            }
            for (int i = 0; i < expired.Count; i++)
            {
                if (_pending.TryGetValue(expired[i], out PendingRequest pending))
                {
                    _pending.Remove(expired[i]);
                    pending.Tcs.TrySetResult(false);
                }
            }
        }

        private static BasisRecordingHandle BeginCapture(ushort id, string uuid, BasisRecordingOptions options)
        {
            if (!_captures.TryGetValue(id, out Capture cap))
            {
                cap = new Capture(id);
                _captures[id] = cap;
            }
            if (cap.Recording != null)
            {
                cap.Recording.FinalizeRecording();
            }
            BasisRecording rec = new BasisRecording(uuid, id, RemoteOpusSettings.NetworkSampleRate, options);
            cap.Recording = rec;
            cap.RaiseLiveSamples = options.LiveSamples;
            cap.EnsureDecodedSink();
            cap.RefreshTaps();
            OnRecordingStarted?.Invoke(id, rec);
            return new BasisRecordingHandle(id, rec);
        }

        private static void StopAllForPlayer(ushort id)
        {
            if (!_captures.TryGetValue(id, out Capture cap))
            {
                return;
            }
            if (cap.Recording != null)
            {
                BasisRecording rec = cap.Recording;
                cap.Recording = null;
                rec.FinalizeRecording();
                OnRecordingStopped?.Invoke(id, rec);
            }
            cap.DisposeObjectSources();
            cap.DetachTaps();
            _captures.Remove(id);
        }

        private static void RemoveCaptureIfEmpty(ushort id, Capture cap)
        {
            if (cap.Recording == null && cap.ObjectSourceCount == 0)
            {
                cap.DetachTaps();
                _captures.Remove(id);
            }
        }

        private static int CountObjectSources()
        {
            int total = 0;
            foreach (Capture cap in _captures.Values)
            {
                total += cap.ObjectSourceCount;
            }
            return total;
        }

        private static void GrantToRecorder(ushort recorderId, BasisVoiceConsentPurpose purpose)
        {
            _recordersOfMe.Add(recorderId);
            BasisNetworkHandleVoiceRecord.SendConsent(recorderId, (byte)BasisVoiceConsentState.Granted, (byte)purpose);
            OnRecordersOfMeChanged?.Invoke();
        }

        private static void ShowPrompt(ushort recorderId, string uuid, BasisVoiceConsentPurpose purpose)
        {
            byte purposeByte = (byte)purpose;
            if (Time.realtimeSinceStartup - _lastPromptTime < GlobalPromptCooldownSeconds)
            {
                BasisNetworkHandleVoiceRecord.SendConsent(recorderId, (byte)BasisVoiceConsentState.Denied, purposeByte);
                return;
            }
            _lastPromptTime = Time.realtimeSinceStartup;
            string displayName = ResolveDisplayName(recorderId);
            string bodyKey = purpose == BasisVoiceConsentPurpose.Route
                ? "menu.voiceRecording.consent.body.route"
                : "menu.voiceRecording.consent.body";
            string title = BasisLocalization.Get("menu.voiceRecording.consent.title");
            string body = BasisLocalization.Get(bodyKey, displayName);
            string accept = BasisLocalization.Get("menu.voiceRecording.consent.allow");
            string deny = BasisLocalization.Get("menu.voiceRecording.consent.deny");

            void Decide(bool allowed)
            {
                if (allowed)
                {
                    GrantToRecorder(recorderId, purpose);
                }
                else
                {
                    BasisNetworkHandleVoiceRecord.SendConsent(recorderId, (byte)BasisVoiceConsentState.Denied, purposeByte);
                }
            }

            if (BasisNotificationCenter.RouteToNotifications)
            {
                BasisMenuDialoguePanel.CreateNew(title, body, accept, deny, Decide, divertible: true);
                return;
            }
            if (!BasisMainMenu.Instance)
            {
                BasisMainMenu.Open();
            }
            BasisMainMenu.Instance.OpenDialogue(title, body, accept, deny, Decide, divertible: true);
        }

        private static bool IsRateLimited(ushort recorderId)
        {
            float now = Time.realtimeSinceStartup;
            if (_lastRequestTime.TryGetValue(recorderId, out float last) && now - last < RequestRateLimitSeconds)
            {
                return true;
            }
            _lastRequestTime[recorderId] = now;
            return false;
        }

        private static bool TryGetPlayerId(BasisRemotePlayer target, out ushort id)
        {
            id = 0;
            if (target == null)
            {
                return false;
            }
            if (BasisNetworkPlayers.PlayerToNetworkedPlayer(target, out BasisNetworkPlayer np) && np != null)
            {
                id = np.playerId;
                return true;
            }
            return false;
        }

        private static string ResolveUuid(ushort id)
        {
            if (BasisNetworkPlayers.Players.TryGetValue(id, out BasisNetworkPlayer np) && np.Player != null)
            {
                return np.Player.UUID;
            }
            return null;
        }

        private static string ResolveDisplayName(ushort id)
        {
            if (BasisNetworkPlayers.Players.TryGetValue(id, out BasisNetworkPlayer np) && np.Player != null)
            {
                return np.Player.DisplayName;
            }
            return id.ToString();
        }

        private sealed class PendingRequest
        {
            public TaskCompletionSource<bool> Tcs;
            public float Deadline;
        }

        /// <summary>
        /// Per-recordee capture state. Owns the decoded-PCM sink (recording) and any 3D
        /// re-emit sources, and installs the tap delegates onto the player's receiver(s).
        /// All fields are touched on the main thread except the lock-free sink and the
        /// volatile object-source snapshot, which the decode/insert threads read.
        /// </summary>
        private sealed class Capture
        {
            public readonly ushort PlayerId;
            public BasisRecording Recording;
            public bool RaiseLiveSamples;
            public BasisVoiceTapSink DecodedSink;

            private readonly List<BasisVoiceObjectSource> _objectSources = new List<BasisVoiceObjectSource>();
            private volatile BasisVoiceObjectSource[] _objectSnapshot = Array.Empty<BasisVoiceObjectSource>();

            public int ObjectSourceCount => _objectSources.Count;

            public Capture(ushort playerId)
            {
                PlayerId = playerId;
            }

            public void EnsureDecodedSink()
            {
                DecodedSink ??= new BasisVoiceTapSink(RemoteOpusSettings.NetworkSampleRate * 4);
            }

            public void AddObjectSource(BasisVoiceObjectSource source)
            {
                _objectSources.Add(source);
                RebuildSnapshot();
            }

            public void RemoveObjectSource(BasisVoiceObjectSource source)
            {
                if (_objectSources.Remove(source))
                {
                    RebuildSnapshot();
                }
            }

            public void DisposeObjectSources()
            {
                for (int i = 0; i < _objectSources.Count; i++)
                {
                    _objectSources[i].Dispose();
                }
                _objectSources.Clear();
                RebuildSnapshot();
            }

            private void RebuildSnapshot()
            {
                _objectSnapshot = _objectSources.Count == 0
                    ? Array.Empty<BasisVoiceObjectSource>()
                    : _objectSources.ToArray();
            }

            public void TickObjectSources()
            {
                bool removed = false;
                for (int i = _objectSources.Count - 1; i >= 0; i--)
                {
                    BasisVoiceObjectSource src = _objectSources[i];
                    src.Tick();
                    if (src.AnchorLost)
                    {
                        _objectSources.RemoveAt(i);
                        src.Dispose();
                        removed = true;
                    }
                }
                if (removed)
                {
                    RebuildSnapshot();
                    RefreshTaps();
                }
            }

            /// <summary>Installs the current tap delegates on the normal and shout receivers.</summary>
            public void RefreshTaps()
            {
                Action<float[], int> decoded = (Recording != null && DecodedSink != null) ? WriteDecoded : null;
                Action<AudioSegmentDataMessage> encoded = _objectSources.Count > 0 ? WriteEncoded : (Action<AudioSegmentDataMessage>)null;

                if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(PlayerId, out BasisNetworkReceiver recv)
                    && recv != null && recv.AudioReceiverModule != null)
                {
                    recv.AudioReceiverModule.OnDecodedFrame = decoded;
                    recv.AudioReceiverModule.OnEncodedFrame = encoded;
                }
                if (BasisShoutAudioDriver.TryGetReceiver(PlayerId, out BasisAudioReceiver shoutReceiver) && shoutReceiver != null)
                {
                    shoutReceiver.OnDecodedFrame = decoded;
                    shoutReceiver.OnEncodedFrame = encoded;
                }
            }

            public void DetachTaps()
            {
                if (BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(PlayerId, out BasisNetworkReceiver recv)
                    && recv != null && recv.AudioReceiverModule != null)
                {
                    recv.AudioReceiverModule.OnDecodedFrame = null;
                    recv.AudioReceiverModule.OnEncodedFrame = null;
                }
                if (BasisShoutAudioDriver.TryGetReceiver(PlayerId, out BasisAudioReceiver shoutReceiver) && shoutReceiver != null)
                {
                    shoutReceiver.OnDecodedFrame = null;
                    shoutReceiver.OnEncodedFrame = null;
                }
            }

            private void WriteDecoded(float[] pcm, int length)
            {
                DecodedSink?.Write(pcm, length);
            }

            private void WriteEncoded(AudioSegmentDataMessage msg)
            {
                BasisVoiceObjectSource[] sources = _objectSnapshot;
                for (int i = 0; i < sources.Length; i++)
                {
                    sources[i].FeedEncoded(msg);
                }
            }
        }
    }
}
