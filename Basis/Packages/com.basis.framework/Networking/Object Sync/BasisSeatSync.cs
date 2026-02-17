using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;
using System;
using UnityEngine;
public class BasisSeatSync : BasisNetworkBehaviour
{
    private BasisNetworkReceiver _currentRemoteRec;
    private ushort _currentUserId = ushort.MaxValue;

    [Header("Seat")]
    public BasisSeat Seat;

    [Header("Runtime")]
    public PlayerID LinkedPlayer = null;
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
        BasisLocalPlayer.JustBeforeNetworkApply.AddAction(20, ProvideRemotePlayerTarget);
    }
    public Action<BasisPlayer> OnNetworkPlayerEnterSeat;
    public Action<BasisPlayer> OnNetworkPlayerExitSeat;
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
            Seat.OnInteractStartEvent += OnInteractStartEvent;
            Seat.OnInteractEndEvent += OnInteractEndEvent;
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

    public void ProvideRemotePlayerTarget()
    {
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
        if (!BasisNetworkPlayers.RemotePlayers.TryGetValue(storedId, out BasisNetworkReceiver rec))
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
        }

        // Now drive the current remote receiver.
        Seat.CalculateSeatPositionRotation(rec.RemotePlayer, out Quaternion seatQuat, out Vector3 hips);
        rec.OverridenDestinationOfRoot(true);
        rec.ProvidedDestinationOfRoot(hips, seatQuat);
    }

    private void ClearCurrentRemote()
    {
        if (_currentRemoteRec != null)
        {
            // Assuming false turns off the override.
            _currentRemoteRec.OverridenDestinationOfRoot(false);
            if (_currentRemoteRec.Player != null)
            {
                OnNetworkPlayerExitSeat?.Invoke(_currentRemoteRec.Player);
            }
            _currentRemoteRec = null;
        }

        _currentUserId = ushort.MaxValue;
    }

    public override void OnDestroy()
    {
        BasisLocalPlayer.JustBeforeNetworkApply.RemoveAction(20, ProvideRemotePlayerTarget);
        if (Seat != null)
        {
            Seat.OnInteractStartEvent -= OnInteractStartEvent;
            Seat.OnInteractEndEvent -= OnInteractEndEvent;
        }
        base.OnDestroy();
    }

    /// <summary>
    /// Local interaction start: attempt to enter seat if free or already ours.
    /// </summary>
    private void OnInteractStartEvent(BasisInput input)
    {
        if (Seat.LocallyInSeat)//guards against the oninteract from exit click
        {
            if (!GetLocalPlayerIdSafe(out ushort id))
            {
                BasisDebug.LogError("Missing LocalPlayer", BasisDebug.LogTag.Networking);
                return;
            }

            // If someone else is already in the seat, do nothing.
            if (HasUser(out ushort current) && current != id)
            {
                return;
            }

            // If we're already the occupant, do nothing.
            if (IsLocallyEntered())
            {
                BasisDebug.Log("We are already the Recipient Standing and then sitting again.");
                Stand();
            }

            SetSeatState(true, id);
        }
    }

    /// <summary>
    /// Local interaction end: only the current local occupant may exit.
    /// </summary>
    private void OnInteractEndEvent(BasisInput input)
    {
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
        if (!DeserializeSeatPacket(buffer, out bool occupied))
        {
            return;
        }

        // If remote says "occupied by X", and we think we're seated but X != local, stand locally.
        if (occupied)
        {
            if (IsLocallyEntered() && GetLocalPlayerIdSafe(out ushort localId) && occupantId != localId)
            {
                Stand();
            }
        }
        else
        {
            // Remote says "unoccupied"; if we think we're seated, stand.
            if (IsLocallyEntered())
            {
                Stand();
            }
        }

        // Apply without rebroadcasting.
        SetSeatStateLocal(occupied, occupantId);
    }
    private void Stand()
    {
        BasisLocalPlayer.Instance?.LocalSeatDriver?.Stand();
    }

    /// <summary>
    /// Create a seat packet: [occupied(byte)].
    /// </summary>
    public byte[] CreateSeatPacket(bool isInSeat)
    {
        byte[] data = new byte[1];
        data[0] = isInSeat ? (byte)1 : (byte)0;
        return data;
    }

    /// <summary>
    /// Set local state and update the Seat component (no networking here).
    /// </summary>
    private void SetSeatStateLocal(bool inSeat, ushort playerId)
    {
        LinkedPlayer.hasPlayerId = inSeat;
        LinkedPlayer.ThePlayerID = playerId;

        if (Seat != null)
        {
            Seat.SetSeatOccupied(inSeat);
            if (BasisNetworkPlayers.GetPlayerById(playerId, out BasisNetworkPlayer Player))
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
    /// Parse a seat packet. Returns false on malformed data.
    /// </summary>
    private static bool DeserializeSeatPacket(byte[] buffer, out bool occupied)
    {
        occupied = false;
        if (buffer == null || buffer.Length < 1)
        {
            return false;
        }
        occupied = buffer[0] != 0;
        return true;
    }
}
