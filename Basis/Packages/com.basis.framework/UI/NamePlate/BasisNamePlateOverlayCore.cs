using System.Collections.Generic;

namespace Basis.Scripts.UI.NamePlate
{
    /// <summary>
    /// Pure policy math for the nameplate overlay limiter (chat bubbles and avatar-loading
    /// displays): nearest-K selection, loading-text quantization, and chat-display idle
    /// release. No UnityEngine dependencies beyond containers so the whole surface is
    /// unit-testable — the Unity side lives in <see cref="BasisNamePlateOverlayLimiter"/>.
    /// </summary>
    public static class BasisNamePlateOverlayCore
    {
        /// <summary>
        /// Marks the <paramref name="cap"/> nearest entries visible. <paramref name="distancesSq"/>
        /// is parallel to the caller's item list; <paramref name="visibleOut"/> is cleared and
        /// refilled to the same length. <paramref name="indexScratch"/> is a reusable buffer so
        /// steady-state calls do not allocate. Returns the number of visible entries.
        /// </summary>
        public static int SelectNearest(List<float> distancesSq, int cap, List<int> indexScratch, List<bool> visibleOut)
        {
            int count = distancesSq.Count;
            visibleOut.Clear();
            if (count == 0)
            {
                return 0;
            }
            if (cap <= 0)
            {
                for (int i = 0; i < count; i++)
                {
                    visibleOut.Add(false);
                }
                return 0;
            }
            if (count <= cap)
            {
                for (int i = 0; i < count; i++)
                {
                    visibleOut.Add(true);
                }
                return count;
            }

            indexScratch.Clear();
            for (int i = 0; i < count; i++)
            {
                indexScratch.Add(i);
                visibleOut.Add(false);
            }
            indexScratch.Sort((a, b) => distancesSq[a].CompareTo(distancesSq[b]));
            for (int i = 0; i < cap; i++)
            {
                visibleOut[indexScratch[i]] = true;
            }
            return cap;
        }

        /// <summary>
        /// Quantizes a 0–100 progress value into buckets of <paramref name="stepPercent"/> so
        /// the loading label only re-tessellates when the bucket changes. A non-positive step
        /// means every whole percent is its own bucket.
        /// </summary>
        public static int ProgressBucket(float progress, float stepPercent)
        {
            if (progress < 0f)
            {
                progress = 0f;
            }
            if (stepPercent <= 0f)
            {
                stepPercent = 1f;
            }
            return (int)(progress / stepPercent);
        }

        /// <summary>
        /// True when a created-but-empty chat display has sat unused long enough to give its
        /// TMP + bubble objects back. Displays with live content never release.
        /// </summary>
        public static bool ShouldReleaseChatDisplay(bool displayExists, bool hasAnyContent, double now, double lastActiveTime, double idleSeconds)
        {
            if (!displayExists || hasAnyContent)
            {
                return false;
            }
            return now - lastActiveTime >= idleSeconds;
        }

        /// <summary>
        /// Whether an avatar-load progress report means the load is finished and the loading
        /// display should hide. Tolerant comparison — the old exact <c>== 100</c> check missed
        /// values that arrive as 99.999… or above 100.
        /// </summary>
        public static bool IsLoadingComplete(float progress)
        {
            return progress >= 99.999f;
        }
    }
}
