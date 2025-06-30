using System;
using System.Collections.Generic;
public class BasisOrderedDelegate
{
    private struct Entry
    {
        public int Priority;
        public Action Action;

        public Entry(int priority, Action action)
        {
            Priority = priority;
            Action = action;
        }
    }

    private readonly List<Entry> entries = new List<Entry>();
    private List<int> executionOrder = new List<int>();
    public int Count { get; private set; }

    public void AddAction(int priority, Action action)
    {
        entries.Add(new Entry(priority, action));
        RebuildExecutionOrder();
        Count = executionOrder.Count;
    }

    public void RemoveAction(int priority, Action action)
    {
        for (int Index = entries.Count - 1; Index >= 0; Index--)
        {
            if (entries[Index].Priority == priority && entries[Index].Action == action)
            {
                //    BasisDebug.Log("removing Action at " + priority);
                entries.RemoveAt(Index);
                RebuildExecutionOrder();
                Count = executionOrder.Count;
                return;
            }
        }
    }

    private void RebuildExecutionOrder()
    {
        executionOrder.Clear();
        int Count = entries.Count;
        for (int Index = 0; Index < Count; Index++)
        {
            executionOrder.Add(Index);
        }

        executionOrder.Sort((a, b) => entries[a].Priority.CompareTo(entries[b].Priority));
    }

    public void Invoke()
    {
        for (int Index = 0; Index < Count; Index++)
        {
            int index = executionOrder[Index];
            entries[index].Action?.Invoke();
        }
    }
}
