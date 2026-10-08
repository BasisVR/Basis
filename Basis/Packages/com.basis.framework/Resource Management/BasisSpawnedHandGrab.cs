using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using System.Collections.Generic;
using UnityEngine;

namespace Basis
{
    /// <summary>
    /// Hands a freshly spawned prop to the player who asked for it. The spawn message itself only
    /// carries a world pose, so the grab is arranged locally: the requesting client books the item's
    /// net id here and the spawn path redeems it once the bundle has finished loading. Every other
    /// client just sees the prop appear and be picked up.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisSpawnedHandGrab
    {
        private const float RequestLifetimeSeconds = 600f;

        private struct PendingGrab
        {
            public BasisPropSpawnHand Hand;
            public float RequestedAt;
        }

        private static readonly Dictionary<string, PendingGrab> _pending = new Dictionary<string, PendingGrab>();
        private static readonly List<string> _expired = new List<string>();

        private static readonly List<PendingRetry> _retries = new List<PendingRetry>();
        private static readonly List<PendingRetry> _retriesDraining = new List<PendingRetry>();
        private static bool _retryHooked;

        private struct PendingRetry
        {
            public GameObject Root;
            public BasisPropSpawnHand Hand;
        }

        /// <summary>
        /// Books a grab against a spawn that has been requested but not yet loaded.
        /// </summary>
        public static void Request(string loadedNetId, BasisPropSpawnHand hand)
        {
            if (string.IsNullOrEmpty(loadedNetId))
            {
                return;
            }

            PruneExpired();
            _pending[loadedNetId] = new PendingGrab { Hand = hand, RequestedAt = Time.realtimeSinceStartup };
        }

        public static void Cancel(string loadedNetId)
        {
            if (!string.IsNullOrEmpty(loadedNetId))
            {
                _pending.Remove(loadedNetId);
            }
        }

        /// <summary>
        /// Redeems a booked grab for a spawn that has just finished loading. Safe to call for every
        /// spawn; it is a no-op unless this client booked that net id.
        /// </summary>
        public static bool TryRedeem(string loadedNetId, GameObject root)
        {
            if (string.IsNullOrEmpty(loadedNetId) || root == null)
            {
                return false;
            }

            if (!_pending.TryGetValue(loadedNetId, out PendingGrab pending))
            {
                return false;
            }

            _pending.Remove(loadedNetId);

            if (Time.realtimeSinceStartup - pending.RequestedAt > RequestLifetimeSeconds)
            {
                return false;
            }

            return TryGrab(root, pending.Hand);
        }

        /// <summary>
        /// Puts an already loaded prop into a hand. Falls back to a single retry on the next late
        /// tick, covering a prop whose interactable is not wired up yet on the frame it appears.
        /// </summary>
        public static bool TryGrab(GameObject root, BasisPropSpawnHand hand)
        {
            if (Grab(root, hand))
            {
                return true;
            }

            ScheduleRetry(root, hand);
            return false;
        }

        private static bool Grab(GameObject root, BasisPropSpawnHand hand)
        {
            if (root == null)
            {
                return false;
            }

            BasisPlayerInteract interact = BasisPlayerInteract.Instance;
            if (interact == null)
            {
                return false;
            }

            if (!PropSpawnPlacement.TryResolveHand(hand, out BasisInput input, out _))
            {
                return false;
            }

            BasisInteractableObject target = root.GetComponentInChildren<BasisInteractableObject>(true);
            if (target == null)
            {
                return false;
            }

            return interact.TryDirectGrab(target, input);
        }

        private static void ScheduleRetry(GameObject root, BasisPropSpawnHand hand)
        {
            if (root == null)
            {
                return;
            }

            _retries.Add(new PendingRetry { Root = root, Hand = hand });

            if (_retryHooked)
            {
                return;
            }

            _retryHooked = true;
            BasisLocalPlayer.AfterSimulateOnLate.AddAction(122, RunRetries);
        }

        private static void RunRetries()
        {
            BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(122, RunRetries);
            _retryHooked = false;

            _retriesDraining.AddRange(_retries);
            _retries.Clear();

            for (int Index = 0; Index < _retriesDraining.Count; Index++)
            {
                PendingRetry retry = _retriesDraining[Index];
                if (retry.Root != null && !Grab(retry.Root, retry.Hand))
                {
                    BasisDebug.Log($"Prop spawned for {retry.Hand} could not be handed over; leaving it where it landed.", BasisDebug.LogTag.Pickups);
                }
            }

            _retriesDraining.Clear();
        }

        private static void PruneExpired()
        {
            float now = Time.realtimeSinceStartup;
            foreach (KeyValuePair<string, PendingGrab> entry in _pending)
            {
                if (now - entry.Value.RequestedAt > RequestLifetimeSeconds)
                {
                    _expired.Add(entry.Key);
                }
            }

            for (int Index = 0; Index < _expired.Count; Index++)
            {
                _pending.Remove(_expired[Index]);
            }
            _expired.Clear();
        }
    }
}
