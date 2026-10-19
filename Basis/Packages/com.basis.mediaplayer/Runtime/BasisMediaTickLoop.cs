using System;
using System.Collections.Generic;

/// <summary>
/// Runs a player's tick consumers in order while the list may change under
/// it: OnDisable removes a consumer at once, so one call can remove itself,
/// another, or re-register. Each consumer runs at most once per call, and
/// none still on the list is left out.
/// </summary>
internal static class BasisMediaTickLoop
{
    /// <param name="ran">Scratch the caller owns: which consumers have run
    /// this call. Cleared here.</param>
    internal static void Run(List<IBasisMediaTickConsumer> consumers, List<IBasisMediaTickConsumer> ran)
    {
        ran.Clear();
        int i = 0;
        while (i < consumers.Count)
        {
            IBasisMediaTickConsumer consumer = consumers[i];
            if (HasRun(ran, consumer))
            {
                i++;
                continue;
            }

            ran.Add(consumer);
            // Contained, so one consumer throwing cannot take the rest of
            // the tick with it.
            try
            {
                consumer.MediaTick();
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorOnce(
                    $"[BasisMedia] {consumer.GetType().Name} tick failed: {e}",
                    BasisDebug.LogTag.Video);
            }

            // The call may have reshaped the list; scan again from the start,
            // skipping what has run.
            i = 0;
        }
    }

    static bool HasRun(List<IBasisMediaTickConsumer> ran, IBasisMediaTickConsumer consumer)
    {
        for (int i = 0; i < ran.Count; i++)
        {
            if (ReferenceEquals(ran[i], consumer)) return true;
        }

        return false;
    }
}
