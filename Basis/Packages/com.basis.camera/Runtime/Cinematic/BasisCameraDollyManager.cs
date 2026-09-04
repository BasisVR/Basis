using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.Cinematics
{
    /// <summary>
    /// Shares dolly tracks across the instance.
    ///
    /// <para>The handheld camera is a local prop — remotes only ever see a PIP stand-in — so there
    /// is no per-camera network identity to hang a track off. This resolves one well-known name
    /// into an id every client agrees on, the same trick the image pickup manager uses, and keys
    /// each track by the player who authored it. That needs no new channel (0-63 are all spoken
    /// for) and no server change.</para>
    ///
    /// <para>Authority is split. The author owns the roster — what points exist, in what order,
    /// and how the track is shared — and anyone may move a point while the track is Shared, which
    /// is what makes a move something two people can build together. A move is applied to the
    /// author's own waypoints and comes back out in their next roster, so the author's track stays
    /// the single answer to "where are the points" and two people reaching for the same one cannot
    /// leave the instance disagreeing.</para>
    /// </summary>
    public static class BasisCameraDollyManager
    {
        private const string FixedNetworkIdentifier = "BasisCameraDollyManager";
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Networking;

        /// <summary>Seconds between unprompted resends, so a late joiner is never left with nothing.</summary>
        private const float KeyframeInterval = 2f;

        /// <summary>Seconds between roster sends while points are being dragged about.</summary>
        private const float RosterInterval = 0.1f;

        public static bool HasNetworkID { get; private set; }
        public static ushort NetworkID { get; private set; }

        /// <summary>
        /// Bumped every time this client leaves an instance. The id resolve is asynchronous, so a reply still
        /// in flight when the connection went away would otherwise arm the next server with the previous
        /// server's id. <see cref="_identityResolveGeneration"/> doubles as the in-flight token, so joins
        /// arriving before the first answer do not each start their own resolve.
        /// </summary>
        private static int _connectionGeneration;
        private static int _identityResolveGeneration = -1;

        private static bool _initialized;

        /// <summary>The local track being shared, or null while nothing is.</summary>
        private static BasisCameraDollyTrack _local;

        /// <summary>Everyone else's tracks, by the player who authored each one.</summary>
        private static readonly Dictionary<ushort, BasisCameraDollyMirror> _mirrors = new();

        private static readonly BasisCameraDollyPacket.Point[] _scratch =
            new BasisCameraDollyPacket.Point[BasisCameraDollyPacket.MaxPoints];

        private static byte[] _sendBuffer = new byte[BasisCameraDollyPacket.RosterSize(BasisCameraDollyPacket.MaxPoints)];
        private static readonly ushort[] _oneRecipient = new ushort[1];

        private static float _nextRoster;
        private static float _nextKeyframe;
        private static int _lastSentCount = -1;
        private static BasisCameraDollySync _lastSentMode = BasisCameraDollySync.LocalOnly;
        private static BasisCameraDollyPacket.Motion _lastSentMotion;
        private static bool _lastSentSpeedColors;
        private static bool _mirrorTickRequested;

        /// <summary>
        /// A move from somebody else has landed on the local track and has not been passed on yet.
        /// Without it a drag by a remote changes nothing the send test can see — the count and the
        /// mode are the same and no local hand is on a point — so the answer would only leave here
        /// at the keyframe rate, and a roster captured before the drag would arrive after it and
        /// pull the point back for as long as two seconds.
        /// </summary>
        private static bool _remoteMoveApplied;

        // ---- Lifecycle -------------------------------------------------------------------

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            BasisNetworkPlayer.OnPlayerLeft -= HandlePlayerLeft;
            BasisNetworkPlayer.OnPlayerLeft += HandlePlayerLeft;
            BasisNetworkPlayer.OnPlayerJoined -= HandlePlayerJoined;
            BasisNetworkPlayer.OnPlayerJoined += HandlePlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined += HandleLocalPlayerJoined;
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;

            if (BasisNetworkConnection.LocalPlayerIsConnected)
            {
                HandleLocalPlayerJoined(null, null);
            }
        }

        public static void Shutdown()
        {
            BasisNetworkPlayer.OnPlayerLeft -= HandlePlayerLeft;
            BasisNetworkPlayer.OnPlayerJoined -= HandlePlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined -= HandleLocalPlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            Application.quitting -= Shutdown;

            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterDirectHandler(NetworkID);
            }
            HasNetworkID = false;
            NetworkID = 0;
            _initialized = false;
            _local = null;

            ClearAllMirrors();
        }

        /// <summary>
        /// Resolving the shared identifier needs a live connection, so it waits for the local player
        /// to be approved rather than running at load. The subscription made in <see cref="Initialize"/>
        /// survives a server switch, so every join lands here.
        /// </summary>
        private static async void HandleLocalPlayerJoined(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                BasisDebug.LogError("Dolly manager cannot start; the local player is not connected.", LogTag);
                return;
            }

            int generation = _connectionGeneration;
            if (HasNetworkID || _identityResolveGeneration == generation) return;
            _identityResolveGeneration = generation;

            BasisIdResolutionResult resolution = await BasisNetworkIdResolver.ResolveAsync(FixedNetworkIdentifier);
            // The id belongs to whichever connection answered. If that connection is gone the answer is the
            // previous server's index, and arming with it leaves us listening on nothing.
            if (!_initialized || HasNetworkID || generation != _connectionGeneration) return;

            if (!resolution.Success)
            {
                _identityResolveGeneration = -1;
                BasisDebug.LogError(
                    $"Dolly manager could not resolve the network identifier '{FixedNetworkIdentifier}'.", LogTag);
                return;
            }

            NetworkID = resolution.Id;
            HasNetworkID = true;
            BasisNetworkGenericMessages.RegisterDirectHandler(NetworkID, OnDirectNetworkMessage);

            BasisNetworkPlayer.OnLocalPlayerLeft -= HandleLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += HandleLocalPlayerLeft;

            _lastSentCount = -1;
            _nextRoster = 0f;
            _nextKeyframe = 0f;
            BasisDebug.Log($"Dolly manager ready (network id {NetworkID}).", LogTag);
        }

        private static void HandleLocalPlayerLeft(BasisNetworkPlayer networkPlayer, BasisLocalPlayer localPlayer)
        {
            _connectionGeneration++;
            _identityResolveGeneration = -1;
            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterDirectHandler(NetworkID);
            }
            HasNetworkID = false;
            NetworkID = 0;
            _lastSentCount = -1;

            ClearAllMirrors();
        }

        /// <summary>
        /// Confirms the id we hold was issued by the connection we are actually on, and resolves a new one if
        /// it was not.
        ///
        /// <see cref="HandleLocalPlayerLeft"/> is the ordinary place the id is released, and it runs off a leave
        /// event a connection that dropped hard never raises. OnPlayerJoined is raised for the local player too,
        /// so checking here catches that case on the way into the next room.
        /// BasisNetworkIdResolver.KnownIdMap is emptied on every teardown and refilled by the server on join, so
        /// an id it does not confirm came from a room we have already left.
        /// </summary>
        private static void EnsureNetworkIdentity()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected) return;

            if (HasNetworkID)
            {
                if (BasisNetworkIdResolver.KnownIdMap.TryGetValue(FixedNetworkIdentifier, out ushort issued)
                    && issued == NetworkID)
                {
                    return;
                }
                HandleLocalPlayerLeft(null, null);
            }

            HandleLocalPlayerJoined(null, null);
        }

        // ---- The local track --------------------------------------------------------------

        /// <summary>
        /// Names the track this client is sharing. Passing null, or a track set to Local Only,
        /// tells everyone else to drop it.
        /// </summary>
        public static void SetLocalTrack(BasisCameraDollyTrack track)
        {
            if (ReferenceEquals(_local, track)) return;

            // The previous track has to be withdrawn explicitly. Silently swapping would leave it
            // standing on every other screen with nothing left to update it.
            if (_local != null) BroadcastWithdrawal();

            _local = track;
            _lastSentCount = -1;
            _nextRoster = 0f;
            _nextKeyframe = 0f;
            _remoteMoveApplied = false;
        }

        /// <summary>
        /// Offers a track for sharing. A roster is keyed by the player who sent it, so a client
        /// shares one track at a time; with several cameras out the first to ask keeps the slot
        /// until it stops sharing, rather than the two of them withdrawing each other every frame.
        /// </summary>
        public static void ClaimLocalTrack(BasisCameraDollyTrack track)
        {
            if (track == null || ReferenceEquals(_local, track)) return;
            if (_local != null && _local.SyncMode != BasisCameraDollySync.LocalOnly) return;

            SetLocalTrack(track);
        }

        /// <summary>Gives the slot up, but only for the track actually holding it.</summary>
        public static void ReleaseLocalTrack(BasisCameraDollyTrack track)
        {
            if (track == null || !ReferenceEquals(_local, track)) return;

            SetLocalTrack(null);
        }

        /// <summary>
        /// Sends the local track when it is due. Called from the camera's own frame, so a client
        /// with no camera out costs nothing at all.
        /// </summary>
        public static void Tick(float time)
        {
            if (!HasNetworkID || _local == null) return;

            if (_local.SyncMode == BasisCameraDollySync.LocalOnly)
            {
                // Only once: a track nobody is sharing should not keep announcing that.
                if (_lastSentCount != -1)
                {
                    BroadcastWithdrawal();
                    _lastSentCount = -1;
                }
                return;
            }

            BasisCameraDollyPacket.Motion motion = LocalMotion();
            bool speedColors = _local.PaintsBySpeed;
            bool changed = _local.Count != _lastSentCount || _local.SyncMode != _lastSentMode
                || !motion.SameAs(_lastSentMotion) || speedColors != _lastSentSpeedColors
                || LocalTrackIsHeld() || _remoteMoveApplied;
            bool due = changed ? time >= _nextRoster : time >= _nextKeyframe;
            if (!due) return;

            BroadcastRoster(null);
            _nextRoster = time + RosterInterval;
            _nextKeyframe = time + KeyframeInterval;
            _lastSentCount = _local.Count;
            _lastSentMode = _local.SyncMode;
            _lastSentMotion = motion;
            _lastSentSpeedColors = speedColors;
            _remoteMoveApplied = false;
        }

        /// <summary>
        /// Whether a point is in somebody's hand. A drag moves points without changing the count, so
        /// without this a track being laid out would only reach the others at the keyframe rate.
        /// </summary>
        private static BasisCameraDollyPacket.Motion LocalMotion() =>
            BasisCameraDollyPacket.Motion.From(_local.Motion, BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);

        private static bool LocalTrackIsHeld()
        {
            for (int Index = 0; Index < _local.Count; Index++)
            {
                BasisCameraDollyWaypoint waypoint = _local.GetWaypoint(Index);
                if (waypoint != null && waypoint.IsGrabbed) return true;
            }
            return false;
        }

        private static void BroadcastRoster(ushort[] recipients)
        {
            int count = Mathf.Min(_local.Count, BasisCameraDollyPacket.MaxPoints);
            for (int Index = 0; Index < count; Index++)
            {
                BasisCameraDollyWaypoint waypoint = _local.GetWaypoint(Index);
                if (waypoint == null) continue;

                _scratch[Index] = new BasisCameraDollyPacket.Point
                {
                    Position = waypoint.Position,
                    Rotation = waypoint.Rotation,
                };
            }

            EnsureBuffer(BasisCameraDollyPacket.RosterSize(count));
            int written = BasisCameraDollyPacket.WriteRoster(_sendBuffer, _local.SyncMode, _local.Looped, _scratch, count,
                LocalMotion(), _local.PaintsBySpeed);
            if (written > 0) Send(written, DeliveryMethod.ReliableOrdered, recipients);
        }

        /// <summary>Tells everyone the track is no longer shared, so their copies come down.</summary>
        private static void BroadcastWithdrawal()
        {
            if (!HasNetworkID) return;

            EnsureBuffer(BasisCameraDollyPacket.RosterSize(0));
            int written = BasisCameraDollyPacket.WriteRoster(_sendBuffer, BasisCameraDollySync.LocalOnly, false, _scratch, 0);
            if (written > 0) Send(written, DeliveryMethod.ReliableOrdered, null);
        }

        /// <summary>
        /// Reports a point somebody is dragging on another player's track. The author applies it;
        /// this client is only asking.
        ///
        /// <para>The frames of a drag are unreliable — the next one is along in a moment and is
        /// worth more than a resend of a stale one. Where the point was let go of is not: it is
        /// the whole answer, nothing follows it to paper over its loss, and dropping it leaves the
        /// author holding a position from part-way through the drag.</para>
        /// </summary>
        public static void SendPointMove(ushort owner, int slot, Vector3 position, Quaternion rotation, bool settled = false)
        {
            if (!HasNetworkID) return;

            EnsureBuffer(BasisCameraDollyPacket.PointMoveSize);
            int written = BasisCameraDollyPacket.WritePointMove(_sendBuffer, owner, slot, position, rotation);
            if (written > 0) Send(written, settled ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, null);
        }

        public static void SendClaim(ushort owner, int slot, bool claimed)
        {
            if (!HasNetworkID) return;

            EnsureBuffer(BasisCameraDollyPacket.ClaimPacketSize);
            int written = BasisCameraDollyPacket.WriteClaim(_sendBuffer, owner, slot, claimed);
            if (written > 0) Send(written, DeliveryMethod.ReliableOrdered, null);
        }

        // ---- Receiving --------------------------------------------------------------------

        private static void OnDirectNetworkMessage(ushort playerId, byte[] buffer, DeliveryMethod delivery)
        {
            int length = buffer?.Length ?? 0;
            if (!BasisCameraDollyPacket.TryReadType(buffer, length, out BasisCameraDollyPacketType type)) return;

            switch (type)
            {
                case BasisCameraDollyPacketType.Roster:
                    ApplyRoster(playerId, buffer, length);
                    break;

                case BasisCameraDollyPacketType.PointMove:
                    ApplyPointMove(playerId, buffer, length);
                    break;

                case BasisCameraDollyPacketType.Claim:
                    ApplyClaim(playerId, buffer, length);
                    break;
            }
        }

        private static void ApplyRoster(ushort playerId, byte[] buffer, int length)
        {
            if (!BasisCameraDollyPacket.TryReadRoster(buffer, length, _scratch,
                out BasisCameraDollySync mode, out bool looped, out int count,
                out BasisCameraDollyPacket.Motion motion, out bool speedColors))
            {
                return;
            }

            if (mode == BasisCameraDollySync.LocalOnly || count == 0)
            {
                DropMirror(playerId);
                return;
            }

            if (!_mirrors.TryGetValue(playerId, out BasisCameraDollyMirror mirror))
            {
                mirror = new BasisCameraDollyMirror(playerId);
                _mirrors[playerId] = mirror;
                UpdateMirrorTickRequest();
            }
            mirror.Apply(mode, looped, _scratch, count, motion, speedColors);
        }

        private static void ApplyPointMove(ushort playerId, byte[] buffer, int length)
        {
            if (!BasisCameraDollyPacket.TryReadPointMove(buffer, length, out ushort owner, out int slot,
                out Vector3 position, out Quaternion rotation))
            {
                return;
            }

            // Only the author applies a move to real waypoints, and only while the track is open
            // to it. A locked track ignores everyone else, which is the whole of what locked means.
            if (TryGetLocalPlayerId(out ushort localId) && owner == localId)
            {
                if (_local == null || _local.SyncMode != BasisCameraDollySync.Networked) return;

                BasisCameraDollyWaypoint waypoint = _local.GetWaypoint(slot);
                if (waypoint == null || waypoint.IsGrabbed) return;

                waypoint.PlaceFromNetwork(position, rotation);
                _remoteMoveApplied = true;
                return;
            }

            // Everyone else moves their copy straight away rather than waiting for the author's
            // next roster, so a point being dragged does not stutter at the roster rate.
            if (_mirrors.TryGetValue(owner, out BasisCameraDollyMirror mirror))
            {
                mirror.MovePoint(slot, position, rotation);
            }
        }

        private static void ApplyClaim(ushort playerId, byte[] buffer, int length)
        {
            if (!BasisCameraDollyPacket.TryReadClaim(buffer, length, out ushort owner, out int slot, out bool claimed))
            {
                return;
            }

            if (_mirrors.TryGetValue(owner, out BasisCameraDollyMirror mirror))
            {
                mirror.SetClaimed(slot, claimed ? playerId : (ushort?)null);
            }
        }

        // ---- Roster upkeep ----------------------------------------------------------------

        private static void HandlePlayerJoined(BasisNetworkPlayer player)
        {
            // The only join signal that survives a network teardown, so this is where a second connection
            // gets its identity back.
            EnsureNetworkIdentity();

            if (!HasNetworkID || _local == null || player == null) return;
            if (_local.SyncMode == BasisCameraDollySync.LocalOnly) return;

            // Straight to the joiner rather than waiting for the keyframe, so a track is there the
            // moment they arrive instead of appearing seconds later.
            _oneRecipient[0] = player.playerId;
            BroadcastRoster(_oneRecipient);
        }

        private static void HandlePlayerLeft(BasisNetworkPlayer player)
        {
            if (player == null) return;
            DropMirror(player.playerId);
        }

        private static void DropMirror(ushort playerId)
        {
            if (_mirrors.TryGetValue(playerId, out BasisCameraDollyMirror mirror))
            {
                mirror.Dispose();
                _mirrors.Remove(playerId);
                UpdateMirrorTickRequest();
            }
        }

        private static void ClearAllMirrors()
        {
            foreach (KeyValuePair<ushort, BasisCameraDollyMirror> pair in _mirrors)
            {
                pair.Value.Dispose();
            }
            _mirrors.Clear();
            UpdateMirrorTickRequest();
        }

        /// <summary>
        /// Somebody else's track has to draw on a client that has no camera of its own out — seeing
        /// where a shot is being laid out is the point of sharing it — so the frame clock drives the
        /// mirrors rather than the handheld camera. The request is held only while there is a track
        /// to draw, so a client with none costs nothing.
        /// </summary>
        private static void UpdateMirrorTickRequest()
        {
            bool wanted = _mirrors.Count > 0;
            if (wanted == _mirrorTickRequested) return;

            _mirrorTickRequested = wanted;
            if (wanted)
            {
                BasisFrameClock.OnTick += TickMirrors;
                BasisFrameClock.AddRequest();
            }
            else
            {
                BasisFrameClock.OnTick -= TickMirrors;
                BasisFrameClock.RemoveRequest();
            }
        }

        /// <summary>Refreshes every mirrored track's markers.</summary>
        public static void TickMirrors()
        {
            float scale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            Quaternion labelFacing = BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.CameraInstance != null
                ? BasisLocalCameraDriver.CameraInstance.transform.rotation
                : Quaternion.identity;

            float time = Time.time;
            foreach (KeyValuePair<ushort, BasisCameraDollyMirror> pair in _mirrors)
            {
                pair.Value.Refresh(scale, labelFacing, time);
            }
        }

        // ---- Plumbing ---------------------------------------------------------------------

        private static bool TryGetLocalPlayerId(out ushort localId)
        {
            BasisNetworkPlayer local = BasisNetworkPlayer.LocalPlayer;
            if (local == null)
            {
                localId = 0;
                return false;
            }

            localId = local.playerId;
            return true;
        }

        private static void EnsureBuffer(int size)
        {
            if (_sendBuffer == null || _sendBuffer.Length < size)
            {
                _sendBuffer = new byte[size];
            }
        }

        private static void Send(int length, DeliveryMethod delivery, ushort[] recipients)
        {
            byte[] payload = _sendBuffer;
            if (length != _sendBuffer.Length)
            {
                payload = new byte[length];
                System.Array.Copy(_sendBuffer, payload, length);
            }
            BasisNetworkGenericMessages.OnNetworkMessageSendDirect(NetworkID, payload, delivery, recipients);
        }

        /// <summary>Whether a track authored by somebody else is currently being shown.</summary>
        public static bool HasMirror(ushort playerId) => _mirrors.ContainsKey(playerId);

        public static int MirrorCount => _mirrors.Count;
    }
}
