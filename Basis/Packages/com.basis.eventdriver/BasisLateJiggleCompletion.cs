using System;
using GatorDragonGames.JigglePhysics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Basis.EventDriver
{
    /// <summary>
    /// Moves the jiggle pose completion to the last moment in the frame that still works, so the
    /// main thread stops waiting on it.
    ///
    /// The pose jobs are scheduled in LateUpdate and completed a few statements later, which leaves
    /// the main thread blocked on them for most of their duration. Nothing reads a jiggled bone in
    /// between — the consumer is rendering. So the completion is pushed into a PlayerLoop step just
    /// ahead of the particle system's end-of-frame update, and everything Unity runs in between —
    /// the rest of LateUpdate, other scripts, canvas and culling work — overlaps the jobs for free,
    /// without anyone having to find work to move.
    ///
    /// The deadline is the part to be careful with. Anything that reads a jiggled bone before this
    /// step runs sees the previous frame's pose: no error, just motion one frame stale, which is
    /// exactly the kind of thing that gets attributed to something else. If that shows up, set
    /// <see cref="Enabled"/> false and the completion goes back to its original place.
    /// </summary>
    public static class BasisLateJiggleCompletion
    {
        /// <summary>
        /// False puts the completion back inline where it was. Kept as a switch rather than removed
        /// so a frame-late pose can be ruled in or out in one change.
        /// </summary>
        public static bool Enabled = true;

        /// <summary>Set when a pose has been scheduled and not yet completed.</summary>
        private static bool sPending;
        private static bool sInstalled;

        /// <summary>Called by the driver after scheduling, to arm the late completion.</summary>
        public static void MarkPosePending()
        {
            sPending = true;
        }

        /// <summary>
        /// Completes the pose if one is outstanding. Safe to call more than once in a frame — the
        /// driver still calls it inline when deferral is off, and the loop step calls it always.
        /// </summary>
        public static void CompleteIfPending()
        {
            if (!sPending)
            {
                return;
            }
            sPending = false;
            using (BasisEventDriverMarkers.JiggleCompletePoseDeferred.Auto())
            {
                JigglePhysics.CompletePose();
            }
        }

        private struct BasisJiggleCompleteStep { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (sInstalled)
            {
                return;
            }

            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (!InsertBeforeParticles(ref root))
            {
                // Unity reorganised the loop, or the stage is not present in this player. Leaving
                // the flag clear keeps the driver completing inline, which is correct and only
                // costs the stall this class exists to remove.
                Debug.LogWarning(
                    "Jiggle pose completion could not be deferred: the particle update stage was " +
                    "not found in the player loop. Completing inline instead.");
                Enabled = false;
                return;
            }

            PlayerLoop.SetPlayerLoop(root);
            sInstalled = true;
            Application.quitting += Uninstall;
        }

        private static void Uninstall()
        {
            Application.quitting -= Uninstall;
            sInstalled = false;
            sPending = false;
        }

        /// <summary>
        /// Splices the completion in immediately ahead of the particle system's end-of-frame update,
        /// which is the first thing in the loop that consumes the posed transforms.
        /// </summary>
        private static bool InsertBeforeParticles(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null)
            {
                return false;
            }

            for (int Index = 0; Index < system.subSystemList.Length; Index++)
            {
                if (system.subSystemList[Index].type == typeof(PostLateUpdate.ParticleSystemEndUpdateAll))
                {
                    PlayerLoopSystem[] replacement = new PlayerLoopSystem[system.subSystemList.Length + 1];
                    Array.Copy(system.subSystemList, replacement, Index);
                    replacement[Index] = new PlayerLoopSystem
                    {
                        type = typeof(BasisJiggleCompleteStep),
                        updateDelegate = CompleteIfPending,
                    };
                    Array.Copy(
                        system.subSystemList, Index, replacement, Index + 1,
                        system.subSystemList.Length - Index);
                    system.subSystemList = replacement;
                    return true;
                }

                PlayerLoopSystem child = system.subSystemList[Index];
                if (InsertBeforeParticles(ref child))
                {
                    system.subSystemList[Index] = child;
                    return true;
                }
            }
            return false;
        }
    }
}
