using Basis;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using BasisSerializer.OdinSerializer;
using LiteNetLib;
using System;
using System.Threading.Tasks;
using UnityEngine;
public class NetworkingManager : BasisNetworkBehaviour
{
    private const int MAX_PLAYERS = 4;
    private const int MAX_BALLS = 16;
    [SerializeField]
    public NetworkTransportedInformation NetworkedSyncInfo = new NetworkTransportedInformation();
    [System.Serializable]
    public class NetworkTransportedInformation
    {
        // players in the game - this field should only be updated by the host, stored in the zeroth position
        public int[] playerIDsSynced = { -1, -1, -1, -1 };

        // ball positions
        public Vector3[] ballsPSynced = new Vector3[MAX_BALLS];

        // cue ball linear velocity
        public Vector3 cueBallVSynced;

        // cue ball angular velocity
        public Vector3 cueBallWSynced;

        // the current state id - this value should increment monotonically, with each id representing a distinct state that's worth snapshotting
        public ushort stateIdSynced;

        // bitmask of pocketed balls
        public ushort ballsPocketedSynced;

        // the current team which is playing
        public byte teamIdSynced;

        // when the timer for the current shot started, driven by the player who trigger the transition
        public int timerStartSynced;

        // the current reposition state (0 is reposition not allowed, 1 is reposition in kitchen, 2 is reposition anywhere, 3 is reposition in snooker D,
        //4 is a foul with no reposition, 5 is foul with no reposition and snookered (free ball)) 6 is after SnookerUndo was used
        public byte foulStateSynced;

        // whether or not the table is open or not (i.e. no suit decided yet)
        public bool isTableOpenSynced;

        // which suit each team has picked - xor with teamId to find the suit (0 is solids, 1 is stripes)
        public byte teamColorSynced;

        // which team won, only used if gameState is 3. (0 is team 0, 1 is team 1, 2 is force reset)
        public byte winningTeamSynced;

        // the current game state (0 is no lobby, 1 is lobby created, 2 is game started, 3 is game finished)
        public byte gameStateSynced;

        // the current turn state (0 is shooting, 1 is simulating, 2 is ran out of time (auto transition to 0), 3 is selecting 4 ball mode)
        public byte turnStateSynced;

        // the current gamemode (0 is 8ball, 1 is 9ball, 2 is jp4b, 3 is kr4b, 4 is Snooker6Red)
        public byte gameModeSynced;

        // the timer for the current game in seconds
        public byte timerSynced;

        // table being used
        public byte tableModelSynced;

        // physics being used
        public byte physicsSynced;

        // whether or not the current game is played with teams
        public bool teamsSynced;

        // whether or not the guideline will be shown
        public bool noGuidelineSynced;

        // whether or not the cue can be locked
        public bool noLockingSynced;

        // scores if game state is 2 or 3 (4ball)
        public byte[] fourBallScoresSynced = new byte[2];

        // the currently active four ball cue ball (0 is white, 1 is yellow) // also used in Snooker to track how many fouls/repeated shots have occurred in a row
        public byte fourBallCueBallSynced;

        // whether this update is urgent and should interrupt any local simulations (0 is no, 1 is interrupt, 2 is interrupt and halt)
        public byte isUrgentSynced;

        // 6RedSnooker: currently a turn where a color should be pocketed // Also re-used in 8ball and 9ball to track if it's the break
        public bool colorTurnSynced;
    }

    [SerializeField] public PlayerSlot playerSlot;
    public BilliardsModule table;

    public bool hasBufferedMessages = false;

    // private bool hasDeferredUpdate;
    // private bool hasLocalUpdate;

    public void _Init(BilliardsModule table_)
    {
        table = table_;

        for (int i = 0; i < NetworkedSyncInfo.ballsPSynced.Length; i++)
        {
            NetworkedSyncInfo.ballsPSynced[i] = table_.balls[i].transform.localPosition;
        }

        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            playerSlot._Init(this);
        }

    }

    // called by the PlayerSlot script
    public void _OnPlayerSlotChanged(PlayerSlot slot)
    {
        if (NetworkedSyncInfo.gameStateSynced == 0) return; // we don't process player registrations if the lobby isn't open

        if (!IsLocalOwner()) return; // only the owner processes player registrations

        BasisNetworkPlayer slotOwner = slot.currentOwnedPlayer;
        if (slotOwner != null)
        {
            int slotOwnerID = slotOwner.playerId;

            bool changedSlot = false;
            int numPlayersPrev = 0;
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (NetworkedSyncInfo.playerIDsSynced[i] != -1)
                {
                    numPlayersPrev++;
                }
                if (NetworkedSyncInfo.playerIDsSynced[i] == slotOwnerID)
                {
                    if (i != slot.SyncedSlot.slot)
                    {
                        NetworkedSyncInfo.playerIDsSynced[i] = -1;
                        changedSlot = true;
                    }
                }
            }

            // if we're deregistering a player, always allow
            if (slot.SyncedSlot.leave)
            {
                NetworkedSyncInfo.playerIDsSynced[slot.SyncedSlot.slot] = -1;
            }
            else
            {
                // otherwise, only allow registration if not already registered
                NetworkedSyncInfo.playerIDsSynced[slot.SyncedSlot.slot] = slotOwner.playerId;
            }

            int numPlayers = CountPlayers();
            if (numPlayersPrev != numPlayers || changedSlot)
            {
                if (numPlayers == 0)
                {
                    NetworkedSyncInfo.winningTeamSynced = 0; // prevent it thinking it was a reset
                    if (!table.gameLive)
                    {
                        NetworkedSyncInfo.gameStateSynced = 0;
                    }
                }
                bufferMessages(false);
            }
        }
    }

    /*public override void OnDeserialization()
    {
        if (table == null)
        {
            hasDeferredUpdate = true;
            return;
        }

        if (table.isLocalSimulationRunning && isUrgentSynced == 0)
        {
            table._LogInfo("received non-urgent update, deferring until local simulation is complete");
            hasDeferredUpdate = true;
            return;
        }
        
        processRemoteState();
    }*/
    public override void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
    {
        BasisDebug.Log("OnNetworkMessage For Billard");
        if (buffer == null)
        {
            OnPlayerPrepareShoot();
        }
        else
        {
            NetworkedSyncInfo = SerializationUtility.DeserializeValue<NetworkTransportedInformation>(buffer, DataFormat.Binary);

            OnDeserialization();
        }
    }

    internal void OnDeserialization()
    {

        table._OnRemoteDeserialization();

        if (table.isLocalSimulationRunning)
        {
            if (NetworkedSyncInfo.isUrgentSynced == 0)
            {
             //   delayedDeserialization = true;
               // return;
            }
            else if (NetworkedSyncInfo.isUrgentSynced == 2)
            {
                table.isLocalSimulationRunning = false;
            }
        }
    }
    public void _OnGameWin(uint winnerId)
    {
        NetworkedSyncInfo.gameStateSynced = 3;
        NetworkedSyncInfo.winningTeamSynced = (byte)winnerId;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            NetworkedSyncInfo.playerIDsSynced[i] = -1;
        }
        bufferMessages(false);
    }

    public void _OnGameReset()
    {
        NetworkedSyncInfo.gameStateSynced = 0;
        NetworkedSyncInfo.winningTeamSynced = 2;
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            NetworkedSyncInfo.playerIDsSynced[i] = -1;
        }

        bufferMessages(true);
    }

    public void _OnSimulationEnded(Vector3[] ballsP, uint ballsPocketed, byte[] fbScores, bool colorTurnLocal)
    {
        Array.Copy(ballsP, NetworkedSyncInfo.ballsPSynced, MAX_BALLS);
        Array.Copy(fbScores, NetworkedSyncInfo.fourBallScoresSynced, 2);
        NetworkedSyncInfo.ballsPocketedSynced = (ushort)ballsPocketed;
        NetworkedSyncInfo.colorTurnSynced = colorTurnLocal;

        bufferMessages(false);
    }

    public void _OnTurnPass(uint teamId)
    {
        NetworkedSyncInfo.stateIdSynced++;

        NetworkedSyncInfo.teamIdSynced = (byte)teamId;
        NetworkedSyncInfo.turnStateSynced = 0;
        NetworkedSyncInfo.foulStateSynced = 0;
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();
        swapFourBallCueBalls();
        if (table.isSnooker6Red)
        {
            NetworkedSyncInfo.fourBallCueBallSynced = 0;
        }

        bufferMessages(false);
    }

    // Snooker only
    public void _OnTurnTie()
    {
        NetworkedSyncInfo.stateIdSynced++;

        NetworkedSyncInfo.teamIdSynced = (byte)UnityEngine.Random.Range(0, 2);
        NetworkedSyncInfo.turnStateSynced = 2;
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();
        NetworkedSyncInfo.foulStateSynced = 3;

        bufferMessages(false);
    }

    public void _OnTurnFoul(uint teamId, bool Scratch, bool objBlocked)
    {
        NetworkedSyncInfo.stateIdSynced++;

        NetworkedSyncInfo.teamIdSynced = (byte)teamId;
        NetworkedSyncInfo.turnStateSynced = 2;
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();
        if (!table.isSnooker6Red)
        {
            if (objBlocked)
            {
                NetworkedSyncInfo.foulStateSynced = 1;
            }
            else
                NetworkedSyncInfo.foulStateSynced = 2;
        }
        else
        {
            if (Scratch)
            {
                NetworkedSyncInfo.foulStateSynced = 3;
            }
            else if (objBlocked)
            {
                NetworkedSyncInfo.foulStateSynced = 5;
            }
            else
            {
                NetworkedSyncInfo.foulStateSynced = 4;
            }

            if (NetworkedSyncInfo.fourBallCueBallSynced > 3)//reused variable to track number of fouls/repeated shots
            {
                NetworkedSyncInfo.fourBallCueBallSynced = 0;//at the limit, 4, we set it to 0 to prevent the SnookerUndo button from appearing again
            }
            else
            {
                NetworkedSyncInfo.fourBallCueBallSynced++;
            }
        }
        swapFourBallCueBalls();

        bufferMessages(false);
    }

    public void _OnTurnContinue()
    {
        NetworkedSyncInfo.stateIdSynced++;

        NetworkedSyncInfo.turnStateSynced = 0;
        NetworkedSyncInfo.foulStateSynced = 0;
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();

        bufferMessages(false);
    }

    public void _OnTableClosed(uint teamColor)
    {
        NetworkedSyncInfo.isTableOpenSynced = false;
        NetworkedSyncInfo.teamColorSynced = (byte)teamColor;

        bufferMessages(false);
    }

    public void _OnHitBall(Vector3 cueBallV, Vector3 cueBallW)
    {
        NetworkedSyncInfo.stateIdSynced++;

        NetworkedSyncInfo.turnStateSynced = 1;
        NetworkedSyncInfo.cueBallVSynced = cueBallV;
        NetworkedSyncInfo.cueBallWSynced = cueBallW;

        bufferMessages(false);
    }

    /*public void _OnPlaceBall()
    {
        foulStateSynced = 0;

        broadcastAndProcess(false);
    }*/

    public void _OnRepositionBalls(Vector3[] ballsP)
    {
        NetworkedSyncInfo.stateIdSynced++;

        Array.Copy(ballsP, NetworkedSyncInfo.ballsPSynced, MAX_BALLS);

        bufferMessages(false);
    }

    public void _OnLobbyOpened()
    {
        NetworkedSyncInfo.winningTeamSynced = 0;
        NetworkedSyncInfo.gameStateSynced = 1;
        NetworkedSyncInfo.stateIdSynced = 0;

        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            NetworkedSyncInfo.playerIDsSynced[i] = -1;
        }
        NetworkedSyncInfo.playerIDsSynced[0] = BasisNetworkPlayer.LocalPlayer.playerId;

        bufferMessages(false);
    }

    public void _OnLobbyClosed()
    {
        for (int i = 0; i < MAX_PLAYERS; i++)
        {
            NetworkedSyncInfo.playerIDsSynced[i] = -1;
        }

        bufferMessages(false);
    }

    public void _OnGameStart(uint defaultBallsPocketed, Vector3[] ballPositions)
    {
        NetworkedSyncInfo.stateIdSynced++;

        NetworkedSyncInfo.gameStateSynced = 2;
        NetworkedSyncInfo.ballsPocketedSynced = (ushort)defaultBallsPocketed;
        //reposition state
        if (table.isSnooker6Red)
        {
            NetworkedSyncInfo.foulStateSynced = 3;
        }
        else
        {
            NetworkedSyncInfo.foulStateSynced = 1;
        }
        if (table.is8Ball || table.is9Ball)
        {
            NetworkedSyncInfo.colorTurnSynced = true;// re-used to track if it's the break
        }
        else
        {
            NetworkedSyncInfo.colorTurnSynced = false;
        }
        NetworkedSyncInfo.turnStateSynced = 0;
        NetworkedSyncInfo.isTableOpenSynced = true;
        NetworkedSyncInfo.teamIdSynced = 0;
        NetworkedSyncInfo.teamColorSynced = 0;
        NetworkedSyncInfo.fourBallCueBallSynced = 0;
        NetworkedSyncInfo.cueBallVSynced = Vector3.zero;
        NetworkedSyncInfo.cueBallWSynced = Vector3.zero;
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();
        Array.Copy(ballPositions, NetworkedSyncInfo.ballsPSynced, MAX_BALLS);
        Array.Clear(NetworkedSyncInfo.fourBallScoresSynced, 0, 2);

        bufferMessages(false);
    }

    public int _OnJoinTeam(int teamId)
    {
        if (teamId == 0)
        {
            if (NetworkedSyncInfo.playerIDsSynced[0] == -1)
            {
                playerSlot.JoinSlot(0);
                return 0;
            }
            else if (NetworkedSyncInfo.teamsSynced && NetworkedSyncInfo.playerIDsSynced[2] == -1)
            {
                playerSlot.JoinSlot(2);
                return 2;
            }
        }
        else if (teamId == 1)
        {
            if (NetworkedSyncInfo.playerIDsSynced[1] == -1)
            {
                playerSlot.JoinSlot(1);
                return 1;
            }
            else if (NetworkedSyncInfo.teamsSynced && NetworkedSyncInfo.playerIDsSynced[3] == -1)
            {
                playerSlot.JoinSlot(3);
                return 3;
            }
        }
        return -1;
    }

    public void _OnLeaveLobby(int playerId)
    {
        playerSlot.LeaveSlot(playerId);
    }

    public void _OnKickLobby(int playerId)
    {
        if (NetworkedSyncInfo.playerIDsSynced[playerId] == -1) return;
        NetworkedSyncInfo.playerIDsSynced[playerId] = -1;

        bufferMessages(false);
    }

    public void _OnTeamsChanged(bool teamsEnabled)
    {
        NetworkedSyncInfo.teamsSynced = teamsEnabled;
        if (!teamsEnabled)
        {
            for (int i = 2; i < 4; i++)
            {
                NetworkedSyncInfo.playerIDsSynced[i] = -1;
                if (CountPlayers() == 0)
                {
                    NetworkedSyncInfo.gameStateSynced = 0;
                }
            }
        }

        bufferMessages(false);
    }

    public void _OnNoGuidelineChanged(bool noGuidelineEnabled)
    {
        NetworkedSyncInfo.noGuidelineSynced = noGuidelineEnabled;

        bufferMessages(false);
    }

    public void _OnNoLockingChanged(bool noLockingEnabled)
    {
        NetworkedSyncInfo.noLockingSynced = noLockingEnabled;

        bufferMessages(false);
    }

    public void _OnTimerChanged(byte newTimer)
    {
        NetworkedSyncInfo.timerSynced = newTimer;

        bufferMessages(false);
    }

    public void _OnTableModelChanged(byte newTableModel)
    {
        NetworkedSyncInfo.tableModelSynced = newTableModel;

        bufferMessages(false);
    }

    public void _OnPhysicsChanged(byte newPhysics)
    {
        NetworkedSyncInfo.physicsSynced = newPhysics;

        bufferMessages(false);
    }

    public void _OnGameModeChanged(byte newGameMode)
    {
        NetworkedSyncInfo.gameModeSynced = newGameMode;

        bufferMessages(false);
    }

    public void validatePlayers()
    {
        bool playerRemoved = false;
        for (int Index = 0; Index < MAX_PLAYERS; Index++)
        {
            if (NetworkedSyncInfo.playerIDsSynced[Index] == -1) continue;
            BasisNetworkPlayer plyr = BasisNetworkPlayer.GetPlayerById(NetworkedSyncInfo.playerIDsSynced[Index]);
            if (plyr == null)
            {
                playerRemoved = true;
                NetworkedSyncInfo.playerIDsSynced[Index] = -1;
            }
        }
        if (CountPlayers() == 0 && !table.gameLive)
        {
            NetworkedSyncInfo.gameStateSynced = 0;
        }
        if (playerRemoved)
        {
            bufferMessages(false);
        }
    }

    int CountPlayers()
    {
        int result = 0;
        for (int Index = 0; Index < MAX_PLAYERS; Index++)
        {
            if (NetworkedSyncInfo.playerIDsSynced[Index] != -1)
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
            if (NetworkedSyncInfo.playerIDsSynced[i] == playedId)
            {
                playerRemoved = true;
                NetworkedSyncInfo.playerIDsSynced[i] = -1;
            }
        }
        if (CountPlayers() == 0 && !table.gameLive)
        {
            NetworkedSyncInfo.gameStateSynced = 0;
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
        NetworkedSyncInfo.stateIdSynced = (ushort)stateIdLocal;

        Array.Copy(newBallsP, NetworkedSyncInfo.ballsPSynced, MAX_BALLS);
        NetworkedSyncInfo.ballsPocketedSynced = (ushort)ballsPocketed;
        Array.Copy(newScores, NetworkedSyncInfo.fourBallScoresSynced, 2);
        NetworkedSyncInfo.gameModeSynced = (byte)gameMode;
        NetworkedSyncInfo.teamIdSynced = (byte)teamId;
        NetworkedSyncInfo.foulStateSynced = (byte)foulState;
        NetworkedSyncInfo.isTableOpenSynced = isTableOpen;
        NetworkedSyncInfo.teamColorSynced = (byte)teamColor;
        NetworkedSyncInfo.turnStateSynced = turnStateLocal;
        NetworkedSyncInfo.cueBallVSynced = cueBallV;
        NetworkedSyncInfo.cueBallWSynced = cueBallW;
        NetworkedSyncInfo.fourBallCueBallSynced = (byte)fourBallCueBall;
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();
        NetworkedSyncInfo.colorTurnSynced = colorTurn;

        bufferMessages(true);
        // OnDeserialization(); // jank! force deserialization so the practice manager knows to ignore it
    }

    public void _OnGlobalSettingsChanged(byte newPhysics, byte newTableModel)
    {
        if (!IsLocalOwner()) return;

        NetworkedSyncInfo.physicsSynced = newPhysics;
        NetworkedSyncInfo.tableModelSynced = newTableModel;

        bufferMessages(false);
    }

    private void swapFourBallCueBalls()
    {
        if (NetworkedSyncInfo.gameModeSynced != 2 && NetworkedSyncInfo.gameModeSynced != 3) return;

        NetworkedSyncInfo.fourBallCueBallSynced ^= 0x01;

        Vector3 temp = NetworkedSyncInfo.ballsPSynced[0];
        NetworkedSyncInfo.ballsPSynced[0] = NetworkedSyncInfo.ballsPSynced[13];
        NetworkedSyncInfo.ballsPSynced[13] = temp;
    }

    private void bufferMessages(bool urgent)
    {
        NetworkedSyncInfo.isUrgentSynced = (byte)(urgent ? 2 : 0);

        hasBufferedMessages = true;
    }

    public async void _FlushBuffer()
    {
        if (!hasBufferedMessages) return;

        hasBufferedMessages = false;
        if (IsLocalOwner() == false)
        {
            BasisDebug.Log("Requesting Async");
            await TakeOwnershipAsync();
        }
        BasisDebug.Log("Punching NetworkedSyncInfo");
        byte[] Data = SerializationUtility.SerializeValue(NetworkedSyncInfo, DataFormat.Binary);
        SendCustomNetworkEvent(Data, DeliveryMethod.ReliableOrdered);
        OnDeserialization();
       // asd
    }

    public void _OnPlayerPrepareShoot()
    {
        //sends a empty packet will interop as prepare shoot
        SendCustomNetworkEvent(null, DeliveryMethod.ReliableOrdered);
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

        NetworkedSyncInfo.stateIdSynced++;

        for (int i = 0; i < 16; i++)
        {
            NetworkedSyncInfo.ballsPSynced[i] = decodeVec3Full(gameState, i * 4, 2.5f);
        }
        NetworkedSyncInfo.cueBallVSynced = decodeVec3Full(gameState, 0x40, 50.0f);
        NetworkedSyncInfo.cueBallWSynced = decodeVec3Full(gameState, 0x46, 500.0f);

        uint spec = decodeU16(gameState, 0x4C);
        uint state = decodeU16(gameState, 0x4E);
        NetworkedSyncInfo.turnStateSynced = (byte)((state & 0x1u) == 0x1u ? 1 : 0);
        NetworkedSyncInfo.teamIdSynced = (byte)((state & 0x2u) >> 1);
        NetworkedSyncInfo.foulStateSynced = (byte)((state & 0x4u) == 0x4u ? 1 : 0);
        NetworkedSyncInfo.isTableOpenSynced = (state & 0x8u) == 0x8u;
        NetworkedSyncInfo.teamColorSynced = (byte)((state & 0x10u) >> 4);
        NetworkedSyncInfo.gameModeSynced = (byte)((state & 0x700u) >> 8);
        uint timerSetting = (state & 0x6000u) >> 13;
        switch (timerSetting)
        {
            case 0:
                NetworkedSyncInfo.timerSynced = 0;
                break;
            case 1:
                NetworkedSyncInfo.timerSynced = 60;
                break;
            case 2:
                NetworkedSyncInfo.timerSynced = 30;
                break;
            case 3:
                NetworkedSyncInfo.timerSynced = 15;
                break;
        }
        NetworkedSyncInfo.timerStartSynced = BasisNetworkManagement.GetServerTimeInMilliseconds();
        NetworkedSyncInfo.teamsSynced = (state & 0x8000u) == 0x8000u;

        if (NetworkedSyncInfo.gameModeSynced == 2)
        {
            NetworkedSyncInfo.fourBallScoresSynced[0] = (byte)(spec & 0x0fu);
            NetworkedSyncInfo.fourBallScoresSynced[1] = (byte)((spec & 0x0fu) >> 4);
            if ((spec & 0x100u) == 0x100u) NetworkedSyncInfo.gameModeSynced = 3;
        }
        else
        {
            NetworkedSyncInfo.ballsPocketedSynced = (ushort)spec;
        }

        bufferMessages(true);
    }

    private void onLoadGameStateV2(string gameStateStr)
    {
        if (!isValidBase64(gameStateStr)) return;

        byte[] gameState = Convert.FromBase64String(gameStateStr);
        if (gameState.Length != 0x7b) return;

        NetworkedSyncInfo.stateIdSynced++;

        for (int i = 0; i < 16; i++)
        {
            NetworkedSyncInfo.ballsPSynced[i] = decodeVec3Full(gameState, i * 6, 2.5f);
        }
        NetworkedSyncInfo.cueBallVSynced = decodeVec3Full(gameState, 0x60, 50.0f);
        NetworkedSyncInfo.cueBallWSynced = decodeVec3Full(gameState, 0x66, 500.0f);

        NetworkedSyncInfo.ballsPocketedSynced = decodeU16(gameState, 0x6C);
        NetworkedSyncInfo.teamIdSynced = gameState[0x6E];
        NetworkedSyncInfo.foulStateSynced = gameState[0x6F];
        NetworkedSyncInfo.isTableOpenSynced = gameState[0x70] != 0;
        NetworkedSyncInfo.teamColorSynced = gameState[0x71];
        NetworkedSyncInfo.turnStateSynced = gameState[0x72];
        NetworkedSyncInfo.gameModeSynced = gameState[0x73];
        NetworkedSyncInfo.timerSynced = gameState[0x75]; // timer was recently changed to a byte, that's why this skips 1
        NetworkedSyncInfo.teamsSynced = gameState[0x76] != 0;
        NetworkedSyncInfo.fourBallScoresSynced[0] = gameState[0x77];
        NetworkedSyncInfo.fourBallScoresSynced[1] = gameState[0x78];
        NetworkedSyncInfo.fourBallCueBallSynced = gameState[0x79];
        NetworkedSyncInfo.colorTurnSynced = gameState[0x7a] != 0;

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

        NetworkedSyncInfo.stateIdSynced++;

        int encodePos = 0; // Add the size of the loaded type in bytes after loading

        for (int i = 0; i < 16; i++)
        {
            NetworkedSyncInfo.ballsPSynced[i] = bytesToVec3(gameState, encodePos);
            encodePos += 12;
        }
        NetworkedSyncInfo.cueBallVSynced = bytesToVec3(gameState, encodePos);
        encodePos += 12;
        NetworkedSyncInfo.cueBallWSynced = bytesToVec3(gameState, encodePos);
        encodePos += 12;

        NetworkedSyncInfo.ballsPocketedSynced = decodeU16(gameState, encodePos);
        encodePos += 2;
        NetworkedSyncInfo.teamIdSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.foulStateSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.isTableOpenSynced = gameState[encodePos] != 0;
        encodePos += 1;
        NetworkedSyncInfo.teamColorSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.turnStateSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.gameModeSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.timerSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.teamsSynced = gameState[encodePos] != 0;
        encodePos += 1;
        NetworkedSyncInfo.fourBallScoresSynced[0] = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.fourBallScoresSynced[1] = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.fourBallCueBallSynced = gameState[encodePos];
        encodePos += 1;
        NetworkedSyncInfo.colorTurnSynced = gameState[encodePos] != 0;
        bufferMessages(true);
    }

    public string _EncodeGameState()
    {
        byte[] gameState = new byte[gameStateLength];
        int encodePos = 0; // Add the size of the recorded type in bytes after recording
        for (int i = 0; i < 16; i++)
        {
            Vec3ToBytes(gameState, encodePos, NetworkedSyncInfo.ballsPSynced[i]);
            encodePos += 12;
        }
        Vec3ToBytes(gameState, encodePos, NetworkedSyncInfo.cueBallVSynced);
        encodePos += 12;
        Vec3ToBytes(gameState, encodePos, NetworkedSyncInfo.cueBallWSynced);
        encodePos += 12;

        encodeU16(gameState, encodePos, (ushort)(NetworkedSyncInfo.ballsPocketedSynced & 0xFFFFu));
        encodePos += 2;
        gameState[encodePos] = NetworkedSyncInfo.teamIdSynced;
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.foulStateSynced;
        encodePos += 1;
        gameState[encodePos] = (byte)(NetworkedSyncInfo.isTableOpenSynced ? 1 : 0);
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.teamColorSynced;
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.turnStateSynced;
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.gameModeSynced;
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.timerSynced;
        encodePos += 1;
        gameState[encodePos] = (byte)(NetworkedSyncInfo.teamsSynced ? 1 : 0);
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.fourBallScoresSynced[0];
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.fourBallScoresSynced[1];
        encodePos += 1;
        gameState[encodePos] = NetworkedSyncInfo.fourBallCueBallSynced;
        encodePos += 1;
        gameState[encodePos] = (byte)(NetworkedSyncInfo.colorTurnSynced ? 1 : 0);

        // find gameStateLength
        // Debug.Log("gameStateLength = " + (encodePos + 1));

        return "v3:" + Convert.ToBase64String(gameState, Base64FormattingOptions.None);
    }

    // because udon won't let us try/catch
    private bool isValidBase64(string value)
    {
        // The quickest test. If the value is null or is equal to 0 it is not base64
        // Base64 string's length is always divisible by four, i.e. 8, 16, 20 etc. 
        // If it is not you can return false. Quite effective
        // Further, if it meets the above criterias, then test for spaces.
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
    public override void OnOwnershipTransfer(BasisNetworkPlayer player)
    {
        if (!player.IsLocal)
        {
            return;
        }

        validatePlayers();

        // If Owner left while sim was running, make sure new owner runs _TriggerSimulationEnded(); 
        BasisNetworkPlayer simOwner = BasisNetworkPlayer.GetPlayerById(table.simulationOwnerID);
        if (table.isLocalSimulationRunning || table.waitingForUpdate)
        {
            if (simOwner.playerId == table.simulationOwnerID)
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
    public override void OnPlayerLeft(BasisNetworkPlayer player)
    {
        if (IsOwnedLocally == false)
        {
            return;
        }
        // if (!BasisNetworkPlayer.LocalPlayer.IsOwnerCached(gameObject))
        // {
        //    return;
        //}

        removePlayer(player.playerId);
    }
}
