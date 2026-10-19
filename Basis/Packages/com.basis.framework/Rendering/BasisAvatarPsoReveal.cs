using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Releases an avatar's atomic load-time visibility gate after the first post-calibration
    /// LateUpdate. Unlike the removed staged renderer reveal, this never exposes body and clothing
    /// in different frames: every renderer is suppressed and restored as one unit.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisAvatarPsoReveal
    {
        private const int RevealPriority = 10000;
        public static bool Enabled = false;

        public static void Apply(bool enabled)
        {
            Enabled = enabled;
        }

        private static readonly Queue<BasisAvatar> Pending = new Queue<BasisAvatar>();
        private static bool subscribed;

        /// <summary>Reveal the complete avatar after this frame's final pose application.</summary>
        public static void BeginStagedReveal(BasisAvatar avatar)
        {
            if (avatar == null || !avatar.HasLoadVisibilityGate)
            {
                return;
            }
            Pending.Enqueue(avatar);
            if (!subscribed)
            {
                subscribed = true;
                BasisLocalPlayer.AfterSimulateOnLate.AddAction(RevealPriority, RevealPending);
            }
        }

        private static void RevealPending()
        {
            BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(RevealPriority, RevealPending);
            subscribed = false;
            while (Pending.Count > 0)
            {
                BasisAvatar avatar = Pending.Dequeue();
                if (avatar != null)
                {
                    avatar.CompleteLoadVisibilityGate();
                }
            }
        }

        /// <summary>Compatibility overload for external callers built against the old no-op API.</summary>
        public static void BeginStagedReveal(Renderer[] renders)
        {
        }
    }
}
