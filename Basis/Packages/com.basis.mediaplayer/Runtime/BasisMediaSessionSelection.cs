using System.Collections.Generic;

/// <summary>
/// The session governor's choice of which players run, as a pure function so
/// it can be read and tested on its own. The caller ranks the candidates
/// (promoted first, then nearest) and applies the result.
/// </summary>
internal static class BasisMediaSessionSelection
{
    internal struct Candidate
    {
        /// <summary>Holding a session now.</summary>
        public bool Active;
        /// <summary>Promoted: outranks distance, and takes a slot without
        /// clearing the swap margin.</summary>
        public bool Promoted;
        /// <summary>Inside its dwell: it moved recently and stays where it
        /// is this pass, whichever way it would otherwise move.</summary>
        public bool Held;
        public float Distance;
    }

    /// <summary>
    /// Decide the retained set first, then the transitions that reach it.
    /// <paramref name="ranked"/> is in order of preference. An active player
    /// stays unless the cap no longer has room for it, or a dormant player
    /// ranked above it clears the swap margin against it. A dormant player
    /// takes a free slot outright; with none free it displaces the
    /// lowest-ranked active below it, and only when meaningfully nearer, or
    /// promoted. Nothing is demoted without something to take its place, so
    /// the viewer is never left with nothing playing while the margin holds
    /// the pair apart.
    /// </summary>
    internal static void Select(
        IReadOnlyList<Candidate> ranked, int cap, float swapMargin,
        List<int> activate, List<int> demote)
    {
        activate.Clear();
        demote.Clear();
        if (cap <= 0) return;

        int activeCount = 0;
        for (int i = 0; i < ranked.Count; i++)
        {
            if (ranked[i].Active) activeCount++;
        }

        // The cap has fewer slots than there are sessions: the lowest-ranked
        // ones give way, dwell permitting.
        int excess = activeCount - cap;
        for (int i = ranked.Count - 1; i >= 0 && excess > 0; i--)
        {
            if (!ranked[i].Active || ranked[i].Held) continue;
            demote.Add(i);
            excess--;
        }

        int free = cap - (activeCount - demote.Count);
        for (int i = 0; i < ranked.Count; i++)
        {
            Candidate candidate = ranked[i];
            if (candidate.Active || candidate.Held) continue;

            if (free > 0)
            {
                activate.Add(i);
                free--;
                continue;
            }

            // The lowest-ranked active below the candidate, so the retained set
            // stays the top of the ranking, except where the swap margin or a
            // dwell keeps a lower-ranked active in place. None means no later
            // candidate can displace either: their "below" sets are subsets of
            // this one's.
            int victim = LowestRankedActiveBelow(ranked, i, demote);
            if (victim < 0) break;
            if (!candidate.Promoted && candidate.Distance > ranked[victim].Distance * swapMargin) continue;

            demote.Add(victim);
            activate.Add(i);
        }
    }

    static int LowestRankedActiveBelow(IReadOnlyList<Candidate> ranked, int above, List<int> demoted)
    {
        for (int j = ranked.Count - 1; j > above; j--)
        {
            if (ranked[j].Active && !ranked[j].Held && !demoted.Contains(j)) return j;
        }

        return -1;
    }

    /// <summary>Where a reactivated player goes back to: a session that was
    /// playing moved on while it was away; a paused one did not.</summary>
    internal static double ResumeTarget(bool paused, double savedSeconds, double elapsedSeconds)
        => paused ? savedSeconds : savedSeconds + elapsedSeconds;
}
