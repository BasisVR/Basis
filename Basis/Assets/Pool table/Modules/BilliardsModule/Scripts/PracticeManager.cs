
using System;
using UnityEngine;



public class PracticeManager : MonoBehaviour
{
    private BilliardsModule table;

    private object[] history = new object[128];

    private int currentPtr;
    private int latestPtr;

    /* private bool hack_currentlyLoading;
    private bool hack_dontRecordNext; */

    public void _Init(BilliardsModule table_)
    {
        table = table_;

        _Clear();
    }

    public void _Tick()
    {
    }

    public void _Clear()
    {
        Array.Clear(history, 0, history.Length);
        currentPtr = 0;
        latestPtr = 0;
    }

    public void _Record()
    {
        /*if (hack_currentlyLoading) return;
        
        if (hack_dontRecordNext)
        {
            hack_dontRecordNext = false;
            currentPtr++;
            return;
        }*/

        int stateIdLocal = table.networkingManager.NetworkedSyncInfo.stateIdSynced;

        if (stateIdLocal == currentPtr) return; // already seen

        if (stateIdLocal < 0 || stateIdLocal >= 1024) return; // abuse?

        // set current pointer to whatever we're recording
        currentPtr = stateIdLocal;

        // expand if needed
        if (currentPtr >= history.Length)
        {
            int newSize = history.Length * 2;
            if (newSize < currentPtr) newSize = currentPtr;

            object[] newHistory = new object[newSize];
            Array.Copy(history, newHistory, history.Length);
            history = newHistory;
        }

        object oldValue = history[currentPtr];
        object newValue = table._SerializeInMemoryState();

        history[currentPtr] = newValue;

        // set latest pointer to current pointer if we're diverging from history
        if (oldValue != null && !table._AreInMemoryStatesEqual((object[])oldValue, (object[])newValue))
        {
            latestPtr = currentPtr;
        }
        // otherwise, set it only if we're seeing something new
        else if (stateIdLocal > latestPtr)
        {
            latestPtr = stateIdLocal;
        }

        table._LogInfo($"recording state current={currentPtr} latest={latestPtr}");
    }

    public void _Undo()
    {
        if (!table.isPlayer) { return; }
        int newPtr = pop(false);
        if (newPtr == -1)
        {
            table._IndicateError();
            return;
        }

        load(newPtr);
    }

    public void _SnookerUndo()
    {
        if (!table.isSnooker6Red) { return; }
        if (table.foulStateLocal == 0 || table.fourBallCueBallLocal == 0) { return; }
        if (!table.isMyTurn()) { return; }

        int newPtr = pop(true);
        if (newPtr == -1)
        {
            table._IndicateError();
            return;
        }

        load_SnookerUndo(currentPtr - newPtr);
    }

    public void _Redo()
    {
        if (!table.isPlayer) { return; }
        int newPtr = push();
        if (newPtr == -1)
        {
            table._IndicateError();
            return;
        }

        load(newPtr);
    }

    private int push()
    {
        int newPtr = currentPtr;

        while (newPtr < latestPtr)
        {
            newPtr++;

            if (history[newPtr] == null) continue;

            return newPtr;
        }

        return -1;
    }

    private int pop(bool snookerUndo)
    {
        int newPtr = currentPtr;

        while (newPtr > 0)
        {
            /*if (currentPtr <= 1)
            {
                table._IndicateError();
                return false;
            }*/
            newPtr--;

            if (history[newPtr] == null) continue;
            object[] state = (object[])history[newPtr];
            if (snookerUndo)
            {
                // repositioining the ball counts as a step, so we need to go back to the last step when it wasn't our turn
                if ((byte)state[4] == (byte)table.localTeamId)
                {
                    continue;
                }
            }
            if ((byte)state[9] == 0 || (byte)state[9] == 2)
            {
                return newPtr;
            }
        }

        return -1;
    }

    private void load_SnookerUndo(int amountBack)
    {
        if (table.isLocalSimulationRunning)
        {
            table._LogInfo("interrupting simulation and loading new state");
        }

        object[] state = (object[])history[currentPtr - amountBack];
        object[] curState = (object[])history[currentPtr];
        //set the values we don't want to reset
        state[2] = curState[2];//scores
        state[5] = (uint)6;//foulstate
        state[6] = false;//tableisopen
        state[8] = curState[8];//fourBallCueBall

        // (Vector3[])state[0], (uint)state[1], (int[])state[2], (uint)state[3], (uint)state[4], (uint)state[5], (bool)state[6], (uint)state[7], (uint)state[8],
        // (byte)state[9], (Vector3)state[10], (Vector3)state[11], (byte)state[12], (bool)state[13]
        //==
        // Vector3[] newBallsP, uint ballsPocketed, int[] newScores, uint gameMode, uint teamId, uint foulState, bool isTableOpen, uint teamColor, uint fourBallCueBall,
        // byte turnStateLocal, Vector3 cueBallV, Vector3 cueBallW, byte previewWinningTeam, bool colorTurn

        // hack_dontRecordNext = (byte) state[9] == 1;
        // hack_currentlyLoading = true;
        table._LoadInMemoryState(state, currentPtr + 1);
        // hack_currentlyLoading = false;

        table._IndicateSuccess();
    }

    private void load(int newPtr)
    {
        if (table.isLocalSimulationRunning)
        {
            table._LogInfo("interrupting simulation and loading new state");
        }

        object[] state = (object[])history[newPtr];
        // hack_dontRecordNext = (byte) state[9] == 1;
        // hack_currentlyLoading = true;
        table._LoadInMemoryState(state, newPtr);
        // hack_currentlyLoading = false;

        table._IndicateSuccess();
    }
}
