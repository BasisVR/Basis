using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using Unity.Mathematics;
using UnityEngine;
public class BasisSeatSync : BasisNetworkBehaviour
{
    private BasisNetworkReceiver _currentRemoteRec;
    private ushort _currentUserId = ushort.MaxValue;
    private Vector3 _lastPinPosition;
    private Quaternion _lastPinRotation = Quaternion.identity;
    private bool _hasLastPinPosition;
    private uint _seatGen;
    private bool _applyingNetworkState;

    [Header("Seat")]
    public BasisSeat Seat;

    [Header("Runtime")]
    [System.NonSerialized] public PlayerID LinkedPlayer = new PlayerID();
    public class PlayerID
    {
        public bool hasPlayerId = false;
        public ushort ThePlayerID;
    }
    public void Awake()
    {
        LinkedPlayer = new PlayerID();
        LinkedPlayer.hasPlayerId = false;
        LinkedPlayer.ThePlayerID = 0;
        BasisLocalPlayer.AfterRemoteSyncInterpolated.AddAction(20, ProvideRemotePlayerTarget);
    }
    public Action<IBasisPlayer> OnNetworkPlayerEnterSeat;
    public Action<IBasisPlayer> OnNetworkPlayerExitSeat;
    /// <summary>Returns true if the local player is currently the recorded occupant.</summary>
    public bool IsLocallyEntered()
    {
        return LinkedPlayer.hasPlayerId && GetLocalPlayerIdSafe(out ushort id) && LinkedPlayer.ThePlayerID == id;
    }

    /// <summary>Returns whether a user occupies the seat and outputs their ID (0 if none).</summary>
    public bool HasUser(out ushort id)
    {
        if (LinkedPlayer.hasPlayerId)
        {
            id = LinkedPlayer.ThePlayerID;
            return true;
        }
        id = 0;
        return false;
    }

    public static bool GetLocalPlayerIdSafe(out ushort ID)
    {
        var localPlayer = BasisNetworkPlayer.LocalPlayer;
        if (localPlayer != null)
        {
            ID = localPlayer.playerId;
            return true;
        }
        ID = 0;
        return false;
    }

    public override void OnNetworkReady()
    {
        if (Seat == null)
        {
            gameObject.TryGetComponent(out Seat);
        }
        if (Seat != null)
        {
            Seat.OnInteractStartEvent.AddListener(OnInteractStartEvent);
            Seat.OnInteractEndEvent.AddListener(OnInteractEndEvent);
            Seat.OnOccupantYawChanged += OnOccupantYawChanged;
            Seat.OnOccupantBodyChanged += OnOccupantBodyChanged;
        }
        else
        {
            BasisDebug.LogError($"[{nameof(BasisSeatSync)}] No BasisSeat found on {name}.", BasisDebug.LogTag.Networking);
        }
    }

    public override void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (IsLocallyEntered())
        {
            // Broadcast our current state only to the new player (includes our ID).
            byte[] data = CreateSeatPacket(true);
            SendCustomNetworkEvent(data, DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
        }
    }

    public override void OnPlayerLeft(BasisNetworkPlayer player)
    {
        if (HasUser(out ushort id) && player.playerId == id)
        {
            // If the local player was the occupant, ensure we stand locally.
            if (GetLocalPlayerIdSafe(out ushort localId) && localId == id)
            {
                Stand();
            }
            SetSeatStateLocal(false, player.playerId);
        }
    }

    public const float YawSendIntervalSeconds = 0.05f;

    private bool _yawDirty;
    private float _lastYawSendTime = float.NegativeInfinity;

    private void OnOccupantYawChanged(float yaw)
    {
        if (IsLocallyEntered())
        {
            _yawDirty = true;
        }
    }

    private void OnOccupantBodyChanged()
    {
        if (IsLocallyEntered())
        {
            _yawDirty = true;
        }
    }

    public const float HipsSendPositionDelta = 0.005f;
    public const float HipsSendAngleDelta = 1f;
    public const float HipsKeyframeSeconds = 1f;
    public const float RemoteBodySmoothRate = 20f;

    private Vector3 _lastSentHipsOffset;
    private Quaternion _lastSentHipsRotation = Quaternion.identity;
    private float _lastHipsSendTime = float.NegativeInfinity;
    private float _lastSentSoleHeight;
    private bool _lastSentHipsFree;

    private bool _remoteHipsFree;
    private Vector3 _remoteHipsOffset;
    private Quaternion _remoteHipsRotation = Quaternion.identity;
    private Vector3 _smoothedHipsOffset;
    private Quaternion _smoothedHipsRotation = Quaternion.identity;
    private float _smoothedYaw;
    private bool _hasSmoothedBody;

    private void FlushOccupantBody(float now)
    {
        if (Seat == null || IsLocallyEntered() == false)
        {
            return;
        }
        if (_lastSentSoleHeight != BasisLocalAvatarDriver.TposeSoleHeight)
        {
            _yawDirty = true;
        }
        bool hipsFree = LocalHipsFree();
        if (_lastSentHipsFree != hipsFree)
        {
            _yawDirty = true;
        }
        var driver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalSeatDriver : null;
        if (!hipsFree || driver == null || now - _lastHipsSendTime < YawSendIntervalSeconds)
        {
            return;
        }
        bool keyframe = now - _lastHipsSendTime >= HipsKeyframeSeconds;
        bool moved = (driver.HipsOffsetFromPin - _lastSentHipsOffset).sqrMagnitude > HipsSendPositionDelta * HipsSendPositionDelta
            || Quaternion.Angle(driver.HipsRotationFromPin, _lastSentHipsRotation) > HipsSendAngleDelta;
        if (keyframe || moved)
        {
            _lastHipsSendTime = now;
            SendCustomNetworkEvent(CreateSeatPacket(true), keyframe ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
        }
    }

    public bool FlushOccupantYaw(float now)
    {
        if (_yawDirty == false || Seat == null || IsLocallyEntered() == false)
        {
            return false;
        }
        if (now - _lastYawSendTime < YawSendIntervalSeconds)
        {
            return false;
        }
        _yawDirty = false;
        _lastYawSendTime = now;
        SendCustomNetworkEvent(CreateSeatPacket(true), DeliveryMethod.ReliableOrdered);
        return true;
    }

    public void ProvideRemotePlayerTarget()
    {
        FlushOccupantBody(Time.unscaledTime);
        FlushOccupantYaw(Time.unscaledTime);

        // If there is no seat, just clear any previous override.
        if (Seat == null)
        {
            ClearCurrentRemote();
            return;
        }

        // If there is no user in this seat, clear override.
        if (!HasUser(out ushort storedId))
        {
            ClearCurrentRemote();
            return;
        }

        // If we can't get local id, or something is wrong, clear override.
        if (!GetLocalPlayerIdSafe(out ushort localId))
        {
            ClearCurrentRemote();
            return;
        }

        // If it's us sitting in that seat, we don't want to override any remote.
        if (localId == storedId)
        {
            ClearCurrentRemote();
            return;
        }

        // Try to get the remote player by id.
        if (!BasisNetworkPlayers.RemotePlayerReceivers.TryGetValue(storedId, out BasisNetworkReceiver rec))
        {
            // ID no longer exists in dictionary (disconnected / removed).
            ClearCurrentRemote();
            return;
        }

        // If the player in the seat changed, clear the old one and store the new one.
        if (_currentUserId != storedId || _currentRemoteRec != rec)
        {
            ClearCurrentRemote(); // turn off override on the previous receiver
            _currentUserId = storedId;
            _currentRemoteRec = rec;
            _hasSmoothedBody = false;
        }

        // Now drive the current remote receiver.
        BasisRemotePlayer remote = rec.RemotePlayer;
        if (remote == null || remote.RemoteAvatarDriver == null || remote.AvatarTransform == null)
        {
            return;
        }
        TickRemoteBodySmoothing();
        if (!Seat.TryCalculateOccupantPose(remote.RemoteAvatarDriver.References, remote.AvatarTransform.localScale, _smoothedYaw, out Vector3 hips, out Quaternion rootRot, out Quaternion hipsRest))
        {
            return;
        }
        Quaternion seatQuat = rootRot * hipsRest;
        rec.OverriddenDestinationOfRoot(true);
        rec.ProvidedDestinationOfRoot(hips + rootRot * _smoothedHipsOffset, rootRot * _smoothedHipsRotation * hipsRest);

        if (_hasLastPinPosition)
        {
            Vector3 pinDelta = hips - _lastPinPosition;
            bool rotated = Mathf.Abs(Quaternion.Dot(seatQuat, _lastPinRotation)) < 0.9999999f;
            if (pinDelta.sqrMagnitude > 0f || rotated)
            {
                Quaternion rotationDelta = seatQuat * Quaternion.Inverse(_lastPinRotation);
                TeleportOccupantJiggleRigs(rec, rotationDelta, _lastPinPosition, pinDelta);
            }
        }
        _lastPinPosition = hips;
        _lastPinRotation = seatQuat;
        _hasLastPinPosition = true;
    }

    private void TickRemoteBodySmoothing()
    {
        Vector3 offset = _remoteHipsFree ? _remoteHipsOffset : Vector3.zero;
        Quaternion rotation = _remoteHipsFree ? _remoteHipsRotation : Quaternion.identity;
        float yaw = Seat.OccupantYawDegrees;
        float blend = 1f - Mathf.Exp(-RemoteBodySmoothRate * Time.deltaTime);
        _smoothedYaw = !_hasSmoothedBody || Seat.OccupantRotationSnapDegrees > 0f ? yaw : BasisSeatFit.WrapDegrees(_smoothedYaw + Mathf.DeltaAngle(_smoothedYaw, yaw) * blend);
        if (!_hasSmoothedBody)
        {
            _smoothedHipsOffset = offset;
            _smoothedHipsRotation = rotation;
            _hasSmoothedBody = true;
            return;
        }
        _smoothedHipsOffset = Vector3.Lerp(_smoothedHipsOffset, offset, blend);
        _smoothedHipsRotation = Quaternion.Slerp(_smoothedHipsRotation, rotation, blend);
    }

    private static void TeleportOccupantJiggleRigs(BasisNetworkReceiver rec, Quaternion rotationDelta, Vector3 pivot, Vector3 positionDelta)
    {
        var jiggleRigs = rec.RemotePlayer?.RemoteAvatarDriver?.JiggleRigs;
        if (jiggleRigs == null)
        {
            return;
        }
        for (int Index = 0; Index < jiggleRigs.Length; Index++)
        {
            var rig = jiggleRigs[Index];
            if (rig != null)
            {
                rig.Teleport(rotationDelta, pivot, positionDelta);
            }
        }
    }

    private void ClearCurrentRemote()
    {
        if (_currentRemoteRec != null)
        {
            if (_hasLastPinPosition)
            {
                _currentRemoteRec.GetLatestNetworkPose(out float3 networkHips, out _, out _);
                Vector3 exitDelta = (Vector3)networkHips - _lastPinPosition;
                if (exitDelta.sqrMagnitude > 0f)
                {
                    TeleportOccupantJiggleRigs(_currentRemoteRec, Quaternion.identity, Vector3.zero, exitDelta);
                }
            }
            // Assuming false turns off the override.
            _currentRemoteRec.OverriddenDestinationOfRoot(false);
            if (LinkedPlayer.hasPlayerId && _currentRemoteRec.Player != null)
            {
                OnNetworkPlayerExitSeat?.Invoke(_currentRemoteRec.Player);
            }
            _currentRemoteRec = null;
        }

        _currentUserId = ushort.MaxValue;
        _hasLastPinPosition = false;
    }

    public override void OnDestroy()
    {
        // Force-eject local player if they're still in this seat
        if (IsLocallyEntered())
        {
            Stand();
        }
        // Clear any remote player position overrides
        ClearCurrentRemote();
        BasisLocalPlayer.AfterRemoteSyncInterpolated.RemoveAction(20, ProvideRemotePlayerTarget);
        if (Seat != null)
        {
            Seat.OnInteractStartEvent.RemoveListener(OnInteractStartEvent);
            Seat.OnInteractEndEvent.RemoveListener(OnInteractEndEvent);
            Seat.OnOccupantYawChanged -= OnOccupantYawChanged;
            Seat.OnOccupantBodyChanged -= OnOccupantBodyChanged;
        }
        base.OnDestroy();
    }

    /// <summary>
    /// Local interaction start: attempt to enter seat if free or already ours.
    /// </summary>
    private void OnInteractStartEvent(BasisInput input)
    {
        if (_applyingNetworkState)
        {
            return;
        }
        if (Seat.LocallyInSeat)//guards against the oninteract from exit click
        {
            if (!GetLocalPlayerIdSafe(out ushort id))
            {
                BasisDebug.LogError("Missing LocalPlayer", BasisDebug.LogTag.Networking);
                return;
            }

            if (HasUser(out ushort current) && current != id)
            {
                _applyingNetworkState = true;
                try
                {
                    StandIfPhysicallySeated();
                }
                finally
                {
                    _applyingNetworkState = false;
                }
                return;
            }

            if (IsLocallyEntered())
            {
                _seatGen++;
                SendCustomNetworkEvent(CreateSeatPacket(true), DeliveryMethod.ReliableOrdered);
                return;
            }

            SetSeatState(true, id);
        }
    }

    /// <summary>
    /// Local interaction end: only the current local occupant may exit.
    /// </summary>
    private void OnInteractEndEvent(BasisInput input)
    {
        if (_applyingNetworkState)
        {
            return;
        }
        if (GetLocalPlayerIdSafe(out ushort id))
        {
            if (IsLocallyEntered())
            {
                SetSeatState(false, id);
            }
            else
            {
                BasisDebug.LogWarning("we dont belong to this seat!", BasisDebug.LogTag.Networking);
                return;
            }
        }
        else
        {
            BasisDebug.LogError("Missing LocalPlayer", BasisDebug.LogTag.Networking);
            return;
        }
    }

    /// <summary>
    /// Set seat state locally and broadcast if it is actually changing.
    /// </summary>
    public void SetSeatState(bool state, ushort id)
    {
        // Idempotency: if nothing changes, don't spam the network.
        if (LinkedPlayer.hasPlayerId == state && LinkedPlayer.ThePlayerID == id)
        {
            return;
        }

        _seatGen++;
        SetSeatStateLocal(state, id);

        // Broadcast new state including occupant ID.
        byte[] data = CreateSeatPacket(state);
        SendCustomNetworkEvent(data, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Apply state received from the network. Packet occupantId is the senderid, this is to lock it into coming from the right person.
    /// </summary>
    public override void OnNetworkMessage(ushort occupantId, byte[] buffer, DeliveryMethod deliveryMethod)
    {
        if (!DeserializeSeatPacket(buffer, out bool occupied, out uint gen, out bool hasGen, out float yaw, out bool hasYaw, out SeatBodyTail body))
        {
            return;
        }
        if (GetLocalPlayerIdSafe(out ushort localId) && occupantId == localId)
        {
            return;
        }
        if (!hasGen)
        {
            gen = _seatGen + 1;
        }

        if (occupied)
        {
            bool accept;
            if (gen > _seatGen)
            {
                accept = true;
            }
            else if (gen == _seatGen)
            {
                if (!HasUser(out ushort current) || current == occupantId)
                {
                    accept = true;
                }
                else
                {
                    accept = occupantId < current;
                }
            }
            else
            {
                accept = false;
            }
            if (!accept)
            {
                return;
            }

            _applyingNetworkState = true;
            try
            {
                _seatGen = gen;
                StandIfPhysicallySeated();
                SetSeatStateLocal(true, occupantId);
                if (hasYaw && Seat != null)
                {
                    Seat.ApplyNetworkedOccupantYaw(yaw);
                }
                if (body.Present)
                {
                    ApplyRemoteBody(body);
                }
            }
            finally
            {
                _applyingNetworkState = false;
            }
        }
        else
        {
            if (gen <= _seatGen)
            {
                return;
            }
            if (!HasUser(out ushort current) || current != occupantId)
            {
                return;
            }

            _applyingNetworkState = true;
            try
            {
                _seatGen = gen;
                StandIfPhysicallySeated();
                SetSeatStateLocal(false, occupantId);
            }
            finally
            {
                _applyingNetworkState = false;
            }
        }
    }
    private void Stand()
    {
        BasisLocalPlayer.Instance?.LocalSeatDriver?.Stand();
    }

    private void StandIfPhysicallySeated()
    {
        if (Seat != null && Seat.LocallyInSeat)
        {
            BasisLocalPlayer.Instance?.LocalSeatDriver?.Stand();
        }
    }

    /// <summary>
    /// </summary>
    public byte[] CreateSeatPacket(bool isInSeat)
    {
        byte[] data = new byte[isInSeat ? BodyPacketLength : 7];
        data[0] = isInSeat ? (byte)1 : (byte)0;
        uint gen = _seatGen;
        data[1] = (byte)gen;
        data[2] = (byte)(gen >> 8);
        data[3] = (byte)(gen >> 16);
        data[4] = (byte)(gen >> 24);
        short yaw = QuantizeYaw(Seat != null && isInSeat ? Seat.OccupantYawDegrees : 0f);
        data[5] = (byte)yaw;
        data[6] = (byte)(yaw >> 8);
        if (isInSeat)
        {
            WriteBodyTail(data);
        }
        return data;
    }

    public const int BodyPacketLength = 30;
    private const int PosePacketShift = 1;
    private const int PosePacketMask = 7;
    private const float MillimetersPerUnit = 1000f;

    private struct SeatBodyTail
    {
        public bool Present;
        public bool HipsFree;
        public BasisSeatPose Pose;
        public float SoleHeight;
        public Vector3 Offset;
        public Vector3 HipsOffset;
        public Quaternion HipsRotation;
    }

    private bool LocalHipsFree()
    {
        var driver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalSeatDriver : null;
        if (driver != null && driver.IsSeated)
        {
            return !driver.HipsPinned;
        }
        return Seat != null && Seat.HipsLock == BasisSeatLockMode.Free;
    }

    private void WriteBodyTail(byte[] data)
    {
        Vector3 hipsOffset = Vector3.zero;
        Quaternion hipsRotation = Quaternion.identity;
        bool hipsFree = LocalHipsFree();
        var driver = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.LocalSeatDriver : null;
        if (hipsFree && driver != null)
        {
            hipsOffset = driver.HipsOffsetFromPin;
            hipsRotation = driver.HipsRotationFromPin;
        }
        _lastSentHipsOffset = hipsOffset;
        _lastSentHipsRotation = hipsRotation;
        _lastSentHipsFree = hipsFree;
        _lastSentSoleHeight = BasisLocalAvatarDriver.TposeSoleHeight;
        data[7] = (byte)((hipsFree ? 1 : 0) | (((int)(Seat != null ? Seat.Pose : BasisSeatPose.Seated) & PosePacketMask) << PosePacketShift));
        WriteShort(data, 8, QuantizeMillimeters(_lastSentSoleHeight));
        WriteVector(data, 10, Seat != null ? Seat.OccupantOffset : Vector3.zero);
        WriteVector(data, 16, hipsOffset);
        WriteShort(data, 22, QuantizeUnit(hipsRotation.x));
        WriteShort(data, 24, QuantizeUnit(hipsRotation.y));
        WriteShort(data, 26, QuantizeUnit(hipsRotation.z));
        WriteShort(data, 28, QuantizeUnit(hipsRotation.w));
    }

    private void ApplyRemoteBody(in SeatBodyTail body)
    {
        _remoteHipsFree = body.HipsFree;
        _remoteHipsOffset = body.HipsOffset;
        _remoteHipsRotation = body.HipsRotation;
        if (Seat != null)
        {
            Seat.NetworkedSoleHeight = body.SoleHeight;
            Seat.ApplyNetworkedOccupantOffset(body.Offset);
            Seat.ApplyNetworkedPose(body.Pose);
        }
    }

    private void ResetRemoteBody()
    {
        _remoteHipsFree = false;
        _remoteHipsOffset = Vector3.zero;
        _remoteHipsRotation = Quaternion.identity;
        _hasSmoothedBody = false;
        if (Seat != null)
        {
            Seat.NetworkedSoleHeight = 0f;
            Seat.ApplyNetworkedOccupantOffset(Vector3.zero);
        }
    }

    private static short QuantizeMillimeters(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? (short)0 : (short)Mathf.Clamp(Mathf.RoundToInt(value * MillimetersPerUnit), short.MinValue, short.MaxValue);
    }

    private static short QuantizeUnit(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? (short)0 : (short)Mathf.Clamp(Mathf.RoundToInt(value * short.MaxValue), -short.MaxValue, short.MaxValue);
    }

    private static void WriteShort(byte[] data, int offset, short value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static short ReadShort(byte[] data, int offset) => (short)(data[offset] | (data[offset + 1] << 8));

    private static void WriteVector(byte[] data, int offset, Vector3 value)
    {
        WriteShort(data, offset, QuantizeMillimeters(value.x));
        WriteShort(data, offset + 2, QuantizeMillimeters(value.y));
        WriteShort(data, offset + 4, QuantizeMillimeters(value.z));
    }

    private static Vector3 ReadVector(byte[] data, int offset)
    {
        return new Vector3(ReadShort(data, offset), ReadShort(data, offset + 2), ReadShort(data, offset + 4)) / MillimetersPerUnit;
    }

    private const float YawQuantizeScale = 32767f / 180f;

    public static short QuantizeYaw(float degrees)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees))
        {
            return 0;
        }
        // Half-open wrap: DeltaAngle maps -180 to +180, which reads back as a 360 degree turn.
        float wrapped = BasisSeatFit.WrapDegrees(degrees);
        if (wrapped >= 180f)
        {
            wrapped -= 360f;
        }
        return (short)Mathf.Clamp(Mathf.RoundToInt(wrapped * YawQuantizeScale), short.MinValue, short.MaxValue);
    }

    public static float DequantizeYaw(short quantized) => quantized / YawQuantizeScale;

    /// <summary>
    /// </summary>
    private void SetSeatStateLocal(bool inSeat, ushort playerId)
    {
        bool changed = LinkedPlayer.hasPlayerId != inSeat || LinkedPlayer.ThePlayerID != playerId;

        LinkedPlayer.hasPlayerId = inSeat;
        LinkedPlayer.ThePlayerID = playerId;

        if (Seat != null)
        {
            Seat.SetSeatOccupied(inSeat);
            if (inSeat == false)
            {
                Seat.ResetOccupantYaw();
                ResetRemoteBody();
            }

            bool resolved = BasisNetworkPlayers.GetPlayerById(playerId, out BasisNetworkPlayer Player);
            Seat.SetOccupantRecord(inSeat && resolved ? Player.Player : null);

            if (changed && resolved)
            {
                if (inSeat)
                {
                    OnNetworkPlayerEnterSeat?.Invoke(Player.Player);
                }
                else
                {
                    OnNetworkPlayerExitSeat?.Invoke(Player.Player);
                }
            }
        }
        else
        {
            BasisDebug.LogError($"[{nameof(BasisSeatSync)}] Tried to set seat state, but Seat is null on {name}.", BasisDebug.LogTag.Networking);
        }
    }

    /// <summary>
    /// </summary>
    private static bool DeserializeSeatPacket(byte[] buffer, out bool occupied, out uint gen, out bool hasGen, out float yaw, out bool hasYaw, out SeatBodyTail body)
    {
        body = new SeatBodyTail { HipsRotation = Quaternion.identity };
        occupied = false;
        gen = 0;
        hasGen = false;
        yaw = 0f;
        hasYaw = false;
        if (buffer == null || buffer.Length < 1)
        {
            return false;
        }
        occupied = buffer[0] != 0;
        if (buffer.Length >= 5)
        {
            gen = (uint)(buffer[1] | (buffer[2] << 8) | (buffer[3] << 16) | (buffer[4] << 24));
            hasGen = true;
        }
        if (buffer.Length >= 7)
        {
            yaw = DequantizeYaw((short)(buffer[5] | (buffer[6] << 8)));
            hasYaw = true;
        }
        if (buffer.Length >= BodyPacketLength)
        {
            Quaternion rotation = new Quaternion(ReadShort(buffer, 22), ReadShort(buffer, 24), ReadShort(buffer, 26), ReadShort(buffer, 28));
            float magnitude = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            body.Present = true;
            body.HipsFree = (buffer[7] & 1) != 0;
            body.Pose = (BasisSeatPose)((buffer[7] >> PosePacketShift) & PosePacketMask);
            body.SoleHeight = ReadShort(buffer, 8) / MillimetersPerUnit;
            body.Offset = ReadVector(buffer, 10);
            body.HipsOffset = ReadVector(buffer, 16);
            body.HipsRotation = magnitude > 0.5f ? new Quaternion(rotation.x / magnitude, rotation.y / magnitude, rotation.z / magnitude, rotation.w / magnitude) : Quaternion.identity;
        }
        return true;
    }
}
