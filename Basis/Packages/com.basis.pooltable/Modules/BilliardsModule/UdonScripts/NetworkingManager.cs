using Basis;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using Basis.Shims;
using System;
using UnityEngine;

[Cilboxable]
public class NetworkingManager : MonoBehaviour
{
    private const int MAX_PLAYERS = 4;
    private const int MAX_BALLS = 16;
    private const byte MessageState = 0;
    private const byte MessagePrepareShoot = 1;
    private const byte MessageSlot = 2;
    internal GameStateData SyncState = new GameStateData();
    public class GameStateData
    {
        public int[] playerIDsSynced = { -1, -1, -1, -1 };

        public Vector3[] ballsPSynced = new Vector3[MAX_BALLS];
        public Vector3 cueBallVSynced;
        public Vector3 cueBallWSynced;

        public ushort stateIdSynced;
        public ushort ballsPocketedSynced;

        public byte teamIdSynced;
        public int timerStartSynced;
        public byte foulStateSynced;

        public bool isTableOpenSynced;
        public byte teamColorSynced;
        public byte winningTeamSynced;
        public byte gameStateSynced;
        public byte turnStateSynced;
        public byte gameModeSynced;
        public byte timerSynced;
        public byte tableModelSynced;
        public byte physicsSynced;

        public bool teamsSynced;
        public bool noGuidelineSynced;
        public bool noLockingSynced;

        public byte[] fourBallScoresSynced = new byte[2];
        public byte fourBallCueBallSynced;

        public byte isUrgentSynced;
        public bool colorTurnSynced;

        private const int FixedLength = 61;
        private const int SyncedLength = FixedLength + MAX_PLAYERS * 4 + MAX_BALLS * 12 + 2;

        public byte[] ToBytes(byte prefix)
        {
            int[] players = playerIDsSynced;
            Vector3[] balls = ballsPSynced;
            byte[] scores = fourBallScoresSynced;
            byte[] data = new byte[1 + FixedLength + players.Length * 4 + balls.Length * 12 + scores.Length];
            data[0] = prefix;

            int o = putInt32(data, 1, players.Length);
            o = BasisBinaryShim.WriteInt32s(data, o, players);

            o = putInt32(data, o, balls.Length);
            o = BasisBinaryShim.WriteVector3s(data, o, balls);

            o = BasisBinaryShim.WriteVector3(data, o, cueBallVSynced);
            o = BasisBinaryShim.WriteVector3(data, o, cueBallWSynced);

            o = putUInt16(data, o, stateIdSynced);
            o = putUInt16(data, o, ballsPocketedSynced);

            data[o++] = teamIdSynced;
            o = putInt32(data, o, timerStartSynced);
            data[o++] = foulStateSynced;

            data[o++] = isTableOpenSynced ? (byte)1 : (byte)0;
            data[o++] = teamColorSynced;
            data[o++] = winningTeamSynced;
            data[o++] = gameStateSynced;
            data[o++] = turnStateSynced;
            data[o++] = gameModeSynced;
            data[o++] = timerSynced;
            data[o++] = tableModelSynced;
            data[o++] = physicsSynced;

            data[o++] = teamsSynced ? (byte)1 : (byte)0;
            data[o++] = noGuidelineSynced ? (byte)1 : (byte)0;
            data[o++] = noLockingSynced ? (byte)1 : (byte)0;

            o = putInt32(data, o, scores.Length);
            for (int i = 0; i < scores.Length; i++)
                data[o++] = scores[i];
            data[o++] = fourBallCueBallSynced;

            data[o++] = isUrgentSynced;
            data[o] = colorTurnSynced ? (byte)1 : (byte)0;
            return data;
        }

        public static GameStateData FromBytes(byte[] data, int offset)
        {
            if (data == null || offset < 0 || data.Length - offset < SyncedLength) return null;

            int o = offset;
            if (getInt32(data, o) != MAX_PLAYERS) return null;
            GameStateData state = new GameStateData();
            o = BasisBinaryShim.ReadInt32s(data, o + 4, state.playerIDsSynced);

            if (getInt32(data, o) != MAX_BALLS) return null;
            o = BasisBinaryShim.ReadVector3s(data, o + 4, state.ballsPSynced);

            state.cueBallVSynced = BasisBinaryShim.ReadVector3(data, o);
            state.cueBallWSynced = BasisBinaryShim.ReadVector3(data, o + 12);
            o += 24;

            state.stateIdSynced = getUInt16(data, o);
            state.ballsPocketedSynced = getUInt16(data, o + 2);

            state.teamIdSynced = data[o + 4];
            state.timerStartSynced = getInt32(data, o + 5);
            state.foulStateSynced = data[o + 9];

            state.isTableOpenSynced = data[o + 10] != 0;
            state.teamColorSynced = data[o + 11];
            state.winningTeamSynced = data[o + 12];
            state.gameStateSynced = data[o + 13];
            state.turnStateSynced = data[o + 14];
            state.gameModeSynced = data[o + 15];
            state.timerSynced = data[o + 16];
            state.tableModelSynced = data[o + 17];
            state.physicsSynced = data[o + 18];

            state.teamsSynced = data[o + 19] != 0;
            state.noGuidelineSynced = data[o + 20] != 0;
            state.noLockingSynced = data[o + 21] != 0;

            if (getInt32(data, o + 22) != 2) return null;
            state.fourBallScoresSynced[0] = data[o + 26];
            state.fourBallScoresSynced[1] = data[o + 27];

            state.fourBallCueBallSynced = data[o + 28];
            state.isUrgentSynced = data[o + 29];
            state.colorTurnSynced = data[o + 30] != 0;
            return state;
        }

        private static int putInt32(byte[] data, int o, int v)
        {
            data[o] = (byte)v;
            data[o + 1] = (byte)(v >> 8);
            data[o + 2] = (byte)(v >> 16);
            data[o + 3] = (byte)(v >> 24);
            return o + 4;
        }

        private static int getInt32(byte[] data, int o)
        {
            return data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24);
        }

        private static int putUInt16(byte[] data, int o, ushort v)
        {
            data[o] = (byte)v;
            data[o + 1] = (byte)(v >> 8);
            return o + 2;
        }

        private static ushort getUInt16(byte[] data, int o)
        {
            return (ushort)(data[o] | (data[o + 1] << 8));
        }
    }
    private BilliardsModule table;
    private BasisNetworkShim net;
    private int ownerId = -1;
    private bool ownedLocallyOnClient;

    private bool hasBufferedMessages = false;
    public void _Init(BilliardsModule table_)
    {
        table = table_;

        for (int i = 0; i < SyncState.ballsPSynced.Length; i++)
        {
            SyncState.ballsPSynced[i] = table_.balls[i].transform.localPosition;
        }

        net = SafeUtil.MakeNetworkable(this);
        if (net != null)
        {
            net.NetworkReady += OnNetworkReady;
            net.NetworkMessageReceived += OnNetworkMessage;
            net.OwnershipTransfer += OnOwnershipTransfer;
            net.PlayerJoined += OnPlayerJoined;
            net.PlayerLeft += OnPlayerLeft;
        }
    }

    public bool IsLocalOwner()
    {
        return net != null && net.IsLocalOwner();
    }

    public int _GetOwnerId()
    {
        return ownerId;
    }

    private void OnNetworkReady()
    {
        if (net.IsOwnedLocallyOnServer)
        {
            ownerId = table._LocalPlayerId();
            ownedLocallyOnClient = true;
        }
        else if (table._GetPlayer(net.CurrentOwnerId) != null)
        {
            ownerId = net.CurrentOwnerId;
        }
    }

    // called when a player asks to take or leave a slot
    private void onSlotRequest(int requesterId, int slot, bool leave)
    {
        if (slot > 3) return;
        if (SyncState.gameStateSynced == 0) return; // we don't process player registrations if the lobby isn't open

        if (!ownedLocallyOnClient) return; // only the owner processes player registrations

        if (requesterId < 0)
        {
            return;
        }
        int slotOwnerID = requesterId;
        int slotHolder = SyncState.playerIDsSynced[slot];
        if (leave ? slotHolder != slotOwnerID : slotHolder != -1 && slotHolder != slotOwnerID) return;

        bool changedSlot = false;
        int numPlayersPrev = 0;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            if (SyncState.playerIDsSynced[i] != -1)
            {
                numPlayersPrev++;
            }
            if (SyncState.playerIDsSynced[i] == slotOwnerID)
            {
                if (i != slot)
                {
                    SyncState.playerIDsSynced[i] = -1;
                    changedSlot = true;
                }
            }
        }

        if (leave)
        {
            SyncState.playerIDsSynced[slot] = -1;
        }
        else
        {
            // otherwise, only allow registration if not already registered
            SyncState.playerIDsSynced[slot] = slotOwnerID;
        }

        int numPlayers = CountPlayers();
        if (numPlayersPrev != numPlayers || changedSlot)
        {
            if (numPlayers == 0)
            {
              SyncState.winningTeamSynced = 0; // prevent it thinking it was a reset
                if (!table.gameLive)
                {
                    SyncState.gameStateSynced = 0;
                }
            }
            bufferMessages(false);
        }
    }

    private void requestSlot(int slot, bool leave)
    {
        if (slot < 0 || slot > 3) return;
        sendToAll(new byte[] { MessageSlot, (byte)slot, (byte)(leave ? 1 : 0) });
        onSlotRequest(table._LocalPlayerId(), slot, leave);
    }

    [NonSerialized] public bool delayedDeserialization = false;
    public void OnDeserialization()
    {
        delayedDeserialization = false;

        if (table.localPlayerDistant)
        {
            delayedDeserialization = true;
            return;
        }

        if (table.isLocalSimulationRunning)
        {
            if (SyncState.isUrgentSynced == 0)
            {
                delayedDeserialization = true;
                return;
            }
            else if (SyncState.isUrgentSynced == 2) table.isLocalSimulationRunning = false;
        }

        table._OnRemoteDeserialization();
    }

    public void _OnGameWin(uint winnerId)
    {
        SyncState.gameStateSynced = 3;
        SyncState.winningTeamSynced = (byte)winnerId;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            SyncState.playerIDsSynced[i] = -1;
        }
        bufferMessages(false);
    }

    public void _OnGameReset()
    {
        SyncState.gameStateSynced = 0;
        SyncState.winningTeamSynced = 2;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            SyncState.playerIDsSynced[i] = -1;
        }

        bufferMessages(true);
    }

    public void _OnSimulationEnded(Vector3[] ballsP, uint ballsPocketed, byte[] fbScores, bool colorTurnLocal)
    {
        Array.Copy(ballsP, SyncState.ballsPSynced, MAX_BALLS);
        Array.Copy(fbScores, SyncState.fourBallScoresSynced, 2);
        SyncState.ballsPocketedSynced = (ushort)ballsPocketed;
        SyncState.colorTurnSynced = colorTurnLocal;

        bufferMessages(false);
    }

    public void _OnTurnPass(uint teamId)
    {
        SyncState.stateIdSynced++;

        SyncState.teamIdSynced = (byte)teamId;
        SyncState.turnStateSynced = 0;
        SyncState.foulStateSynced = 0;
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;
        swapFourBallCueBalls();
        if (table.isSnooker6Red)
        {
            SyncState.fourBallCueBallSynced = 0;
        }

        bufferMessages(false);
    }

    // Snooker only
    public void _OnTurnTie()
    {
        SyncState.stateIdSynced++;

        SyncState.teamIdSynced = (byte)UnityEngine.Random.Range(0, 2);
        SyncState.turnStateSynced = 2;
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;
        SyncState.foulStateSynced = 3;

        bufferMessages(false);
    }

    public void _OnTurnFoul(uint teamId, bool Scratch, bool objBlocked)
    {
        SyncState.stateIdSynced++;

        SyncState.teamIdSynced = (byte)teamId;
        SyncState.turnStateSynced = 2;
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;
        if (!table.isSnooker6Red)
        {
            if (objBlocked)
            {
                SyncState.foulStateSynced = 1;
            }
            else
                SyncState.foulStateSynced = 2;
        }
        else
        {
            if (Scratch)
            {
                SyncState.foulStateSynced = 3;
            }
            else if (objBlocked)
            {
                SyncState.foulStateSynced = 5;
            }
            else
            {
                SyncState.foulStateSynced = 4;
            }

            if (SyncState.fourBallCueBallSynced > 3)//reused variable to track number of fouls/repeated shots
            {
                SyncState.fourBallCueBallSynced = 0;//at the limit, 4, we set it to 0 to prevent the SnookerUndo button from appearing again
            }
            else
            {
                SyncState.fourBallCueBallSynced++;
            }
        }
        swapFourBallCueBalls();

        bufferMessages(false);
    }

    public void _OnTurnContinue()
    {
        SyncState.stateIdSynced++;

        SyncState.turnStateSynced = 0;
        SyncState.foulStateSynced = 0;
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;

        bufferMessages(false);
    }

    public void _OnTableClosed(uint teamColor)
    {
        SyncState.isTableOpenSynced = false;
        SyncState.teamColorSynced = (byte)teamColor;

        bufferMessages(false);
    }

    public void _OnHitBall(Vector3 cueBallV, Vector3 cueBallW)
    {
        SyncState.stateIdSynced++;

        SyncState.turnStateSynced = 1;
        SyncState.cueBallVSynced = cueBallV;
        SyncState.cueBallWSynced = cueBallW;

        bufferMessages(false);
    }

    public void _OnRepositionBalls(Vector3[] ballsP)
    {
        SyncState.stateIdSynced++;

        Array.Copy(ballsP, SyncState.ballsPSynced, MAX_BALLS);

        bufferMessages(false);
    }

    public void _OnLobbyOpened()
    {
        SyncState.winningTeamSynced = 0;
        SyncState.gameStateSynced = 1;
        SyncState.stateIdSynced = 0;

        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            SyncState.playerIDsSynced[i] = -1;
        }
        SyncState.playerIDsSynced[0] = table._LocalPlayerId();

        bufferMessages(false);
    }

    public void _OnLobbyClosed()
    {
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            SyncState.playerIDsSynced[i] = -1;
        }

        bufferMessages(false);
    }

    public void _OnGameStart(uint defaultBallsPocketed, Vector3[] ballPositions)
    {
        SyncState.stateIdSynced++;

        SyncState.gameStateSynced = 2;
        SyncState.ballsPocketedSynced = (ushort)defaultBallsPocketed;
        //reposition state
        if (table.isSnooker6Red)
        {
            SyncState.foulStateSynced = 3;
        }
        else
        {
            SyncState.foulStateSynced = 1;
        }
        if (table.is8Ball || table.is9Ball)
        {
            SyncState.colorTurnSynced = true;// re-used to track if it's the break
        }
        else
        {
            SyncState.colorTurnSynced = false;
        }
        SyncState.turnStateSynced = 0;
        SyncState.isTableOpenSynced = true;
        SyncState.teamIdSynced = 0;
        SyncState.teamColorSynced = 0;
        SyncState.fourBallCueBallSynced = 0;
        SyncState.cueBallVSynced = Vector3.zero;
        SyncState.cueBallWSynced = Vector3.zero;
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;
        Array.Copy(ballPositions, SyncState.ballsPSynced, MAX_BALLS);
        Array.Clear(SyncState.fourBallScoresSynced, 0, 2);

        bufferMessages(false);
    }

    public int _OnJoinTeam(int teamId)
    {
        if (teamId == 0)
        {
            if (SyncState.playerIDsSynced[0] == -1)
            {
                requestSlot(0, false);
                return 0;
            }
            else if (SyncState.teamsSynced && SyncState.playerIDsSynced[2] == -1)
            {
                requestSlot(2, false);
                return 2;
            }
        }
        else if (teamId == 1)
        {
            if (SyncState.playerIDsSynced[1] == -1)
            {
                requestSlot(1, false);
                return 1;
            }
            else if (SyncState.teamsSynced && SyncState.playerIDsSynced[3] == -1)
            {
                requestSlot(3, false);
                return 3;
            }
        }
        return -1;
    }

    public void _OnLeaveLobby(int playerId)
    {
        requestSlot(playerId, true);
    }

    public void _OnKickLobby(int playerId)
    {
        if (SyncState.playerIDsSynced[playerId] == -1) return;
        SyncState.playerIDsSynced[playerId] = -1;

        bufferMessages(false);
    }

    public void _OnTeamsChanged(bool teamsEnabled)
    {
        SyncState.teamsSynced = teamsEnabled;
        if (!teamsEnabled)
        {
            for (int i = 2; i < 4; i++)
            {
                SyncState.playerIDsSynced[i] = -1;
                if (CountPlayers() == 0)
                {
                    SyncState.gameStateSynced = 0;
                }
            }
        }

        bufferMessages(false);
    }

    public void _OnNoGuidelineChanged(bool noGuidelineEnabled)
    {
        SyncState.noGuidelineSynced = noGuidelineEnabled;

        bufferMessages(false);
    }

    public void _OnNoLockingChanged(bool noLockingEnabled)
    {
        SyncState.noLockingSynced = noLockingEnabled;

        bufferMessages(false);
    }

    public void _OnTimerChanged(byte newTimer)
    {
        SyncState.timerSynced = newTimer;

        bufferMessages(false);
    }

    public void _OnTableModelChanged(uint newTableModel)
    {
        SyncState.tableModelSynced = (byte)newTableModel;

        bufferMessages(false);
    }

    public void _OnPhysicsChanged(uint newPhysics)
    {
        SyncState.physicsSynced = (byte)newPhysics;

        bufferMessages(false);
    }

    public void _OnGameModeChanged(uint newGameMode)
    {
        SyncState.gameModeSynced = (byte)newGameMode;

        bufferMessages(false);
    }

    public void validatePlayers()
    {
        bool playerRemoved = false;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            if (SyncState.playerIDsSynced[i] == -1) continue;
            if (table._GetPlayer(SyncState.playerIDsSynced[i]) == null)
            {
                playerRemoved = true;
                SyncState.playerIDsSynced[i] = -1;
            }
        }
        if (CountPlayers() == 0 && !table.gameLive)
        {
            SyncState.gameStateSynced = 0;
        }
        if (playerRemoved)
        {
            bufferMessages(false);
        }
    }

    int CountPlayers()
    {
        int result = 0;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            if (SyncState.playerIDsSynced[i] != -1)
            {
                result++;
            }
        }
        return result;
    }

    public void removePlayer(int playedId)
    {
        bool playerRemoved = false;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            if (SyncState.playerIDsSynced[i] == playedId)
            {
                playerRemoved = true;
                SyncState.playerIDsSynced[i] = -1;
            }
        }
        if (CountPlayers() == 0 && !table.gameLive)
        {
            SyncState.gameStateSynced = 0;
        }
        if (playerRemoved)
        {
            bufferMessages(false);
        }
    }

    public void _ForceLoadFromState
    (
        int stateIdLocal,
        Vector3[] newBallsP, uint ballsPocketed, byte[] newScores, uint gameMode, uint teamId, uint foulState, bool isTableOpen, uint teamColor, uint fourBallCueBall,
        byte turnStateLocal, Vector3 cueBallV, Vector3 cueBallW, bool colorTurn
    )
    {
        SyncState.stateIdSynced = (ushort)stateIdLocal;

        Array.Copy(newBallsP, SyncState.ballsPSynced, MAX_BALLS);
        SyncState.ballsPocketedSynced = (ushort)ballsPocketed;
        Array.Copy(newScores, SyncState.fourBallScoresSynced, 2);
        SyncState.gameModeSynced = (byte)gameMode;
        SyncState.teamIdSynced = (byte)teamId;
        SyncState.foulStateSynced = (byte)foulState;
        SyncState.isTableOpenSynced = isTableOpen;
        SyncState.teamColorSynced = (byte)teamColor;
        SyncState.turnStateSynced = turnStateLocal;
        SyncState.cueBallVSynced = cueBallV;
        SyncState.cueBallWSynced = cueBallW;
        SyncState.fourBallCueBallSynced = (byte)fourBallCueBall;
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;
        SyncState.colorTurnSynced = colorTurn;

        bufferMessages(true);
        // OnDeserialization(); // jank! force deserialization so the practice manager knows to ignore it
    }

    public void _OnGlobalSettingsChanged(byte newPhysics, byte newTableModel)
    {
        if (!IsLocalOwner()) return;

        SyncState.physicsSynced = newPhysics;
        SyncState.tableModelSynced = newTableModel;

        bufferMessages(false);
    }

    private void swapFourBallCueBalls()
    {
        if (SyncState.gameModeSynced != 2 && SyncState.gameModeSynced != 3) return;

        SyncState.fourBallCueBallSynced ^= 0x01;

        Vector3 temp = SyncState.ballsPSynced[0];
        SyncState.ballsPSynced[0] = SyncState.ballsPSynced[13];
        SyncState.ballsPSynced[13] = temp;
    }

    private void bufferMessages(bool urgent)
    {
        SyncState.isUrgentSynced = (byte)(urgent ? 2 : 0);

        hasBufferedMessages = true;
    }

    public void _FlushBuffer()
    {
        if (!hasBufferedMessages) return;

        hasBufferedMessages = false;

        takeOwnership();
        this.RequestSerialization();
        OnDeserialization();
    }

    private void takeOwnership()
    {
        ownedLocallyOnClient = true;
        ownerId = table._LocalPlayerId();
        if (net != null && net.HasNetworkID) net.TakeOwnership();
    }

    private void RequestSerialization()
    {
        sendToAll(SyncState.ToBytes(MessageState));
    }

    private void sendToAll(byte[] buffer)
    {
        if (net == null || !net.HasNetworkID) return;
        net.SendCustomNetworkEvent(buffer, DeliveryMethod.ReliableOrdered, null);
    }

    private void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        if (buffer == null || buffer.Length == 0) return;
        byte Prefix = buffer[0];
        if (Prefix == MessageState)
        {
            GameStateData received = null;
            try
            {
                received = GameStateData.FromBytes(buffer, 1);
            }
            catch (Exception)
            {
                received = null;
            }
            if (received == null)
            {
                table._LogWarn("ignored a malformed table state from player " + PlayerID);
                return;
            }
            SyncState = received;
            OnDeserialization();
        }
        else if (Prefix == MessagePrepareShoot)
        {
            OnPlayerPrepareShoot();
        }
        else if (Prefix == MessageSlot)
        {
            if (buffer.Length < 3) return;
            onSlotRequest(PlayerID, buffer[1], buffer[2] != 0);
        }
    }
    public void _OnPlayerPrepareShoot()
    {
        sendToAll(new byte[] { MessagePrepareShoot });
    }
    public void OnPlayerPrepareShoot()
    {
        table._OnPlayerPrepareShoot();
    }

    private const float I16_MAXf = 32767.0f;

    private void encodeU16(byte[] data, int pos, ushort v)
    {
        data[pos] = (byte)(v & 0xff);
        data[pos + 1] = (byte)(((uint)v >> 8) & 0xff);
    }

    private ushort decodeU16(byte[] data, int pos)
    {
        return (ushort)(data[pos] | (((uint)data[pos + 1]) << 8));
    }

    // 6 char string from Vector3. Encodes floats in: [ -range, range ] to 0-65535
    private void encodeVec3Full(byte[] data, int pos, Vector3 vec, float range)
    {
        encodeU16(data, pos, (ushort)((Mathf.Clamp(vec.x, -range, range) / range) * I16_MAXf + I16_MAXf));
        encodeU16(data, pos + 2, (ushort)((Mathf.Clamp(vec.y, -range, range) / range) * I16_MAXf + I16_MAXf));
        encodeU16(data, pos + 4, (ushort)((Mathf.Clamp(vec.z, -range, range) / range) * I16_MAXf + I16_MAXf));
    }


    // Decode 6 chars at index to Vector3. Decodes from 0-65535 to [ -range, range ]
    private Vector3 decodeVec3Full(byte[] data, int start, float range)
    {
        ushort _x = decodeU16(data, start);
        ushort _y = decodeU16(data, start + 2);
        ushort _z = decodeU16(data, start + 4);

        float x = ((_x - I16_MAXf) / I16_MAXf) * range;
        float y = ((_y - I16_MAXf) / I16_MAXf) * range;
        float z = ((_z - I16_MAXf) / I16_MAXf) * range;

        return new Vector3(x, y, z);
    }

    private float decodeF32(byte[] data, int addr, float range)
    {
        return ((decodeU16(data, addr) - I16_MAXf) / I16_MAXf) * range;
    }

    private void floatToBytes(byte[] data, int pos, float v)
    {
        byte[] bytes = BitConverter.GetBytes(v);
        Array.Copy(bytes, 0, data, pos, 4);
    }

    public float bytesToFloat(byte[] data, int pos)
    {
        byte[] floatBytes = new byte[4];
        Array.Copy(data, pos, floatBytes, 0, 4);
        return BitConverter.ToSingle(floatBytes, 0);
    }

    private void Vec3ToBytes(byte[] data, int pos, Vector3 vec)
    {
        floatToBytes(data, pos, vec.x);
        floatToBytes(data, pos + 4, vec.y);
        floatToBytes(data, pos + 8, vec.z);
    }

    private Vector3 bytesToVec3(byte[] data, int start)
    {
        float x = bytesToFloat(data, start);
        float y = bytesToFloat(data, start + 4);
        float z = bytesToFloat(data, start + 8);

        return new Vector3(x, y, z);
    }

    private Color decodeColor(byte[] data, int addr)
    {
        ushort _r = decodeU16(data, addr);
        ushort _g = decodeU16(data, addr + 2);
        ushort _b = decodeU16(data, addr + 4);
        ushort _a = decodeU16(data, addr + 6);

        return new Color
        (
           ((_r - I16_MAXf) / I16_MAXf) * 20.0f,
           ((_g - I16_MAXf) / I16_MAXf) * 20.0f,
           ((_b - I16_MAXf) / I16_MAXf) * 20.0f,
           ((_a - I16_MAXf) / I16_MAXf) * 20.0f
        );
    }

    public void _OnLoadGameState(string gameStateStr)
    {
        if (gameStateStr.StartsWith("v3:"))
        {
            onLoadGameStateV3(gameStateStr.Substring(3));
        }
        else if (gameStateStr.StartsWith("v2:"))
        {
            onLoadGameStateV2(gameStateStr.Substring(3));
        }
        else if (gameStateStr.StartsWith("v1:"))
        {
            onLoadGameStateV1(gameStateStr.Substring(3));
        }
        else
        {
            onLoadGameStateV1(gameStateStr);
        }
    }

    private void onLoadGameStateV1(string gameStateStr)
    {
        if (!isValidBase64(gameStateStr)) return;

        byte[] gameState = Convert.FromBase64String(gameStateStr);
        if (gameState.Length != 0x54) return;

      SyncState.stateIdSynced++;

        for (int i = 0; i < 16; i++)
        {
            SyncState.ballsPSynced[i] = decodeVec3Full(gameState, i * 4, 2.5f);
        }
        SyncState.cueBallVSynced = decodeVec3Full(gameState, 0x40, 50.0f);
        SyncState.cueBallWSynced = decodeVec3Full(gameState, 0x46, 500.0f);

        uint spec = decodeU16(gameState, 0x4C);
        uint state = decodeU16(gameState, 0x4E);
        SyncState.turnStateSynced = (byte)((state & 0x1u) == 0x1u ? 1 : 0);
        SyncState.teamIdSynced = (byte)((state & 0x2u) >> 1);
        SyncState.foulStateSynced = (byte)((state & 0x4u) == 0x4u ? 1 : 0);
        SyncState.isTableOpenSynced = (state & 0x8u) == 0x8u;
        SyncState.teamColorSynced = (byte)((state & 0x10u) >> 4);
        SyncState.gameModeSynced = (byte)((state & 0x700u) >> 8);
        uint timerSetting = (state & 0x6000u) >> 13;
        switch (timerSetting)
        {
            case 0:
                SyncState.timerSynced = 0;
                break;
            case 1:
                SyncState.timerSynced = 60;
                break;
            case 2:
                SyncState.timerSynced = 30;
                break;
            case 3:
                SyncState.timerSynced = 15;
                break;
        }
        SyncState.timerStartSynced = BasisNetworkTimeShim.ServerTimeMilliseconds;
        SyncState.teamsSynced = (state & 0x8000u) == 0x8000u;

        if (SyncState.gameModeSynced == 2)
        {
            SyncState.fourBallScoresSynced[0] = (byte)(spec & 0x0fu);
            SyncState.fourBallScoresSynced[1] = (byte)((spec & 0x0fu) >> 4);
            if ((spec & 0x100u) == 0x100u) SyncState.gameModeSynced = 3;
        }
        else
        {
            SyncState.ballsPocketedSynced = (ushort)spec;
        }

        bufferMessages(true);
    }

    private void onLoadGameStateV2(string gameStateStr)
    {
        if (!isValidBase64(gameStateStr)) return;

        byte[] gameState = Convert.FromBase64String(gameStateStr);
        if (gameState.Length != 0x7b) return;

        SyncState.stateIdSynced++;

        for (int i = 0; i < 16; i++)
        {
            SyncState.ballsPSynced[i] = decodeVec3Full(gameState, i * 6, 2.5f);
        }
        SyncState.cueBallVSynced = decodeVec3Full(gameState, 0x60, 50.0f);
        SyncState.cueBallWSynced = decodeVec3Full(gameState, 0x66, 500.0f);

        SyncState.ballsPocketedSynced = decodeU16(gameState, 0x6C);
        SyncState.teamIdSynced = gameState[0x6E];
        SyncState.foulStateSynced = gameState[0x6F];
        SyncState.isTableOpenSynced = gameState[0x70] != 0;
        SyncState.teamColorSynced = gameState[0x71];
        SyncState.turnStateSynced = gameState[0x72];
        SyncState.gameModeSynced = gameState[0x73];
        SyncState.timerSynced = gameState[0x75]; // timer was recently changed to a byte, that's why this skips 1
        SyncState.teamsSynced = gameState[0x76] != 0;
        SyncState.fourBallScoresSynced[0] = gameState[0x77];
        SyncState.fourBallScoresSynced[1] = gameState[0x78];
        SyncState.fourBallCueBallSynced = gameState[0x79];
        SyncState.colorTurnSynced = gameState[0x7a] != 0;

        bufferMessages(true);
    }

    // V3 no longer encodes floats to shorts, as the string isn't synced it doesn't matter how long it is
    // ensures perfect replication of shots
    uint gameStateLength = 230u;
    private void onLoadGameStateV3(string gameStateStr)
    {
        if (!isValidBase64(gameStateStr)) return;

        byte[] gameState = Convert.FromBase64String(gameStateStr);
        if (gameState.Length != gameStateLength) return;

        SyncState.stateIdSynced++;

        int encodePos = 0; // Add the size of the loaded type in bytes after loading

        for (int i = 0; i < 16; i++)
        {
            SyncState.ballsPSynced[i] = bytesToVec3(gameState, encodePos);
            encodePos += 12;
        }
        SyncState.cueBallVSynced = bytesToVec3(gameState, encodePos);
        encodePos += 12;
        SyncState.cueBallWSynced = bytesToVec3(gameState, encodePos);
        encodePos += 12;

        SyncState.ballsPocketedSynced = decodeU16(gameState, encodePos);
        encodePos += 2;
        SyncState.teamIdSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.foulStateSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.isTableOpenSynced = gameState[encodePos] != 0;
        encodePos += 1;
        SyncState.teamColorSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.turnStateSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.gameModeSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.timerSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.teamsSynced = gameState[encodePos] != 0;
        encodePos += 1;
        SyncState.fourBallScoresSynced[0] = gameState[encodePos];
        encodePos += 1;
        SyncState.fourBallScoresSynced[1] = gameState[encodePos];
        encodePos += 1;
        SyncState.fourBallCueBallSynced = gameState[encodePos];
        encodePos += 1;
        SyncState.colorTurnSynced = gameState[encodePos] != 0;
        bufferMessages(true);
    }

    public string _EncodeGameState()
    {
        byte[] gameState = new byte[gameStateLength];
        int encodePos = 0; // Add the size of the recorded type in bytes after recording
        for (int i = 0; i < 16; i++)
        {
            Vec3ToBytes(gameState, encodePos, SyncState.ballsPSynced[i]);
            encodePos += 12;
        }
        Vec3ToBytes(gameState, encodePos, SyncState.cueBallVSynced);
        encodePos += 12;
        Vec3ToBytes(gameState, encodePos, SyncState.cueBallWSynced);
        encodePos += 12;

        encodeU16(gameState, encodePos, (ushort)(SyncState.ballsPocketedSynced & 0xFFFFu));
        encodePos += 2;
        gameState[encodePos] = SyncState.teamIdSynced;
        encodePos += 1;
        gameState[encodePos] = SyncState.foulStateSynced;
        encodePos += 1;
        gameState[encodePos] = (byte)(SyncState.isTableOpenSynced ? 1 : 0);
        encodePos += 1;
        gameState[encodePos] = SyncState.teamColorSynced;
        encodePos += 1;
        gameState[encodePos] = SyncState.turnStateSynced;
        encodePos += 1;
        gameState[encodePos] = SyncState.gameModeSynced;
        encodePos += 1;
        gameState[encodePos] = SyncState.timerSynced;
        encodePos += 1;
        gameState[encodePos] = (byte)(SyncState.teamsSynced ? 1 : 0);
        encodePos += 1;
        gameState[encodePos] = SyncState.fourBallScoresSynced[0];
        encodePos += 1;
        gameState[encodePos] = SyncState.fourBallScoresSynced[1];
        encodePos += 1;
        gameState[encodePos] = SyncState.fourBallCueBallSynced;
        encodePos += 1;
        gameState[encodePos] = (byte)(SyncState.colorTurnSynced ? 1 : 0);

        // find gameStateLength
        // Debug.Log("gameStateLength = " + (encodePos + 1));

        return "v3:" + Convert.ToBase64String(gameState);
    }

    // because udon won't let us try/catch
    private bool isValidBase64(string value)
    {
        // The quickest test. If the value is null or is equal to 0 it is not base64
        // Base64 string's length is always divisible by four, i.e. 8, 16, 20 etc.
        // If it is not you can return false. Quite effective
        // Further, if it meets the above criteria, then test for spaces.
        // If it contains spaces, it is not base64
        if (value == null || value.Length == 0 || value.Length % 4 != 0
            || value.Contains(" ") || value.Contains("\t") || value.Contains("\r") || value.Contains("\n"))
            return false;

        // 98% of all non base64 values are invalidated by this time.
        var index = value.Length - 1;

        // if there is padding step back
        if (value[index] == '=')
            index--;

        // if there are two padding chars step back a second time
        if (value[index] == '=')
            index--;

        // Now traverse over characters
        // You should note that I'm not creating any copy of the existing strings,
        // assuming that they may be quite large
        for (var i = 0; i <= index; i++)
            // If any of the character is not from the allowed list
            if (isInvalidBase64Char(value[i]))
                // return false
                return false;

        // If we got here, then the value is a valid base64 string
        return true;
    }

    private bool isInvalidBase64Char(char value)
    {
        var intValue = (int)value;

        // 1 - 9
        if (intValue >= 48 && intValue <= 57)
            return false;

        // A - Z
        if (intValue >= 65 && intValue <= 90)
            return false;

        // a - z
        if (intValue >= 97 && intValue <= 122)
            return false;

        // + or /
        return intValue != 43 && intValue != 47;
    }
    private void OnOwnershipTransfer(BasisNetworkPlayer player)
    {
        if (player == null) return;
        ownerId = player.playerId;
        ownedLocallyOnClient = player.playerId == table._LocalPlayerId();
        if (!ownedLocallyOnClient) return;
        validatePlayers();

        // If Owner left while sim was running, make sure new owner runs _TriggerSimulationEnded();
        IBasisPlayer simOwner = table._GetPlayer(table.simulationOwnerID);
        if (table.isLocalSimulationRunning || table.waitingForUpdate || delayedDeserialization)
        {
            if (delayedDeserialization)
            {
                // The person who took ownership had the table LoD'd
                table._LogInfo("Simulation changed ownership: New owner is in LoD mode, simulation end may be delayed");
                table.CheckDistanceLoD(); // Disables the LoD if owner & game is on
                OnDeserialization(); // this will run the last received simulation
            }
            if (simOwner == null || simOwner.GetPlayerId() == table.simulationOwnerID)
            {
                table.isLocalSimulationOurs = true;
                if (!table.isLocalSimulationRunning)
                {
                    table._TriggerSimulationEnded(false, true);
                    table._LogInfo("Simulation changed ownership: Owner probably lagged out during sim");
                }
                else
                {
                    table._LogInfo("Simulation changed ownership: Owner quit during sim");
                }
            }
        }
    }

    private void OnPlayerJoined(BasisNetworkPlayer player)
    {
        if (player == null || !IsLocalOwner()) return;
        if (player.playerId == table._LocalPlayerId()) return;
        net.SendCustomNetworkEvent(SyncState.ToBytes(MessageState), DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
    }

    private void OnPlayerLeft(BasisNetworkPlayer player)
    {
        if (player == null || !IsLocalOwner()) return;
        removePlayer(player.playerId);
    }
}
