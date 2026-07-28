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

    /// <summary>
    /// The actions already in execution order, flattened when the subscriber list changes rather than
    /// walked through two indirections on every invoke. These fire several times a frame with every
    /// per-frame system in the process subscribed, so the dispatch itself is worth keeping flat.
    ///
    /// It also makes the invoke re-entrancy-safe: a subscriber that removes itself while being called
    /// mutates the lists, and iterating a snapshot means that cannot walk off the end mid-dispatch.
    /// </summary>
    private Action[] orderedActions = Array.Empty<Action>();

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

        // Flatten once here so the per-frame invoke is a straight array walk.
        if (orderedActions.Length != executionOrder.Count)
        {
            orderedActions = new Action[executionOrder.Count];
        }
        for (int Index = 0; Index < executionOrder.Count; Index++)
        {
            orderedActions[Index] = entries[executionOrder[Index]].Action;
        }
    }

    public void Invoke()
    {
        // Local copy so a subscriber that adds or removes during dispatch swaps the field without
        // this loop following it mid-walk.
        Action[] actions = orderedActions;
        for (int Index = 0; Index < actions.Length; Index++)
        {
            actions[Index]?.Invoke();
        }
    }
}
