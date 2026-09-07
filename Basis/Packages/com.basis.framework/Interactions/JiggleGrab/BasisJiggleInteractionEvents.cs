using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using GatorDragonGames.JigglePhysics;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    /// <summary>
    /// Grab and touch notifications for jiggle chains, for issue #685 — avatars, props and world
    /// objects that want to react when a player handles them.
    ///
    /// <b>Strictly opt in.</b> Nothing is detected for an object until something registers a
    /// listener for it, and when the listener list is empty the per-frame tick returns on a count
    /// check. Cost is proportional to the objects that asked for events, never to the number of
    /// jiggle rigs in the instance — which is what makes it safe in a thousand-player room where
    /// almost nobody is listening.
    ///
    /// Touch is hand proximity, measured with the same palm-to-fingertip grip volume a grab press
    /// uses (<see cref="BasisJiggleGrabPicker"/>), so "touched" means the same thing as "close
    /// enough to grab" and no simulation state has to be read back out of the Burst jobs.
    ///
    /// Events are edge triggered with a dwell, so a hand resting on a chain produces one begin and
    /// one end rather than a callback every frame.
    /// </summary>
    public static class BasisJiggleInteractionEvents
    {
        /// <summary>Cap on simultaneously registered listeners.</summary>
        public const int MaxListeners = 256;
        /// <summary>Only players this close to a listener are tested at all.</summary>
        public const float TouchScanRange = 3f;
        /// <summary>Minimum time a contact must hold before the opposite edge can fire.</summary>
        public const float TouchDwellSeconds = 0.15f;
        /// <summary>Ceiling on hand-versus-chain tests per frame across every listener.</summary>
        public const int MaxTouchTestsPerFrame = 512;
        /// <summary>How often a listener re-resolves the jiggle rigs under its root.</summary>
        public const float RigRefreshSeconds = 2f;

        public enum InteractionKind : byte
        {
            Grab = 0,
            Touch = 1,
        }

        /// <summary>
        /// What happened, in plain values only. Nothing here is a handle into the simulation or the
        /// scene, so it is safe to hand across the sandbox boundary.
        /// </summary>
        public struct InteractionEvent
        {
            public InteractionKind Kind;
            /// <summary>True on the leading edge, false when the grab or touch ends.</summary>
            public bool Began;
            /// <summary>Network id of the player doing it. Zero when offline.</summary>
            public ushort PlayerId;
            /// <summary>0 = left, 1 = right.</summary>
            public byte Hand;
            /// <summary>Which jiggle rig under the listener's root, in the order the rigs sit on it.</summary>
            public byte RigIndex;
            /// <summary>World position of the chain point involved.</summary>
            public Vector3 Position;
        }

        private class Listener
        {
            public Transform Root;
            public Action<InteractionEvent> Handler;
            public JiggleRig[] Rigs = Array.Empty<JiggleRig>();
            public float RigsRefreshedAt = float.NegativeInfinity;
            // Keyed by (playerId << 8 | hand): is that hand currently on one of these chains.
            public readonly Dictionary<int, TouchState> Touching = new Dictionary<int, TouchState>();
        }

        private struct TouchState
        {
            public BasisJiggleTouchLatch Latch;
            public byte RigIndex;
            public Vector3 Position;
        }

        private static readonly List<Listener> listeners = new List<Listener>();
        private static readonly List<int> touchScratch = new List<int>();
        private static int scanCursor;

        /// <summary>True while anything is listening — the whole system is dormant otherwise.</summary>
        public static bool HasListeners => listeners.Count > 0;

        /// <summary>
        /// Starts reporting grabs and touches on the jiggle rigs under <paramref name="root"/>.
        /// Registering the same root and handler twice is a no-op rather than a double dispatch.
        /// </summary>
        public static bool RegisterListener(Transform root, Action<InteractionEvent> handler)
        {
            if (root == null || handler == null)
            {
                return false;
            }
            int count = listeners.Count;
            for (int Index = 0; Index < count; Index++)
            {
                if (listeners[Index].Root == root && listeners[Index].Handler == handler)
                {
                    return true;
                }
            }
            if (count >= MaxListeners)
            {
                BasisDebug.LogError($"Jiggle interaction listeners are capped at {MaxListeners}; refusing {root.name}.");
                return false;
            }
            listeners.Add(new Listener { Root = root, Handler = handler });
            return true;
        }

        public static void UnregisterListener(Transform root, Action<InteractionEvent> handler)
        {
            for (int Index = listeners.Count - 1; Index >= 0; Index--)
            {
                if (listeners[Index].Root == root && listeners[Index].Handler == handler)
                {
                    listeners.RemoveAt(Index);
                }
            }
        }

        public static void Clear()
        {
            listeners.Clear();
        }

        /// <summary>
        /// How many jiggle rigs sit under <paramref name="root"/>. Rig indices in an
        /// <see cref="InteractionEvent"/> are positions in this set, in hierarchy order.
        /// </summary>
        public static int GetRigCount(Transform root)
        {
            return ResolveRigs(root).Length;
        }

        /// <summary>
        /// Name of a rig's root bone, so content can tell which chain an event was about without
        /// hard-coding an index. Empty string when the index is out of range.
        /// </summary>
        public static string GetRigName(Transform root, int rigIndex)
        {
            JiggleRig[] rigs = ResolveRigs(root);
            if (rigIndex < 0 || rigIndex >= rigs.Length || rigs[rigIndex] == null)
            {
                return string.Empty;
            }
            Transform rootBone = rigs[rigIndex].GetJiggleRigData().rootBone;
            return rootBone != null ? rootBone.name : rigs[rigIndex].name;
        }

        /// <summary>
        /// Index of the rig whose root bone carries <paramref name="rigName"/>, or -1. The intended
        /// use is to resolve the indices you care about once at start-up and compare against them in
        /// the callbacks, rather than assuming an ordering.
        /// </summary>
        public static int FindRig(Transform root, string rigName)
        {
            if (string.IsNullOrEmpty(rigName))
            {
                return -1;
            }
            JiggleRig[] rigs = ResolveRigs(root);
            for (int Index = 0; Index < rigs.Length; Index++)
            {
                JiggleRig rig = rigs[Index];
                if (rig == null)
                {
                    continue;
                }
                Transform rootBone = rig.GetJiggleRigData().rootBone;
                string name = rootBone != null ? rootBone.name : rig.name;
                if (string.Equals(name, rigName, StringComparison.Ordinal))
                {
                    return Index;
                }
            }
            return -1;
        }

        /// <summary>
        /// Rigs for a root, reusing a registered listener's cached list so a query from a callback
        /// does not re-walk the hierarchy.
        /// </summary>
        private static JiggleRig[] ResolveRigs(Transform root)
        {
            if (root == null)
            {
                return Array.Empty<JiggleRig>();
            }
            int count = listeners.Count;
            for (int Index = 0; Index < count; Index++)
            {
                if (listeners[Index].Root == root)
                {
                    RefreshRigs(listeners[Index], Time.unscaledTime);
                    return listeners[Index].Rigs;
                }
            }
            return root.GetComponentsInChildren<JiggleRig>(true);
        }

        /// <summary>
        /// Raised by <see cref="BasisJiggleGrabDriver"/> when a grab starts or ends. Delivered to
        /// whichever listener owns the grabbed rig; no listener means no work beyond a count check.
        /// </summary>
        internal static void ReportGrab(JiggleRig rig, ushort grabberId, byte hand, Vector3 position, bool began)
        {
            if (listeners.Count == 0 || rig == null)
            {
                return;
            }
            int count = listeners.Count;
            for (int Index = 0; Index < count; Index++)
            {
                Listener listener = listeners[Index];
                int rigIndex = IndexOfRig(listener, rig);
                if (rigIndex < 0)
                {
                    continue;
                }
                Dispatch(listener, new InteractionEvent
                {
                    Kind = InteractionKind.Grab,
                    Began = began,
                    PlayerId = grabberId,
                    Hand = hand,
                    RigIndex = (byte)rigIndex,
                    Position = position,
                });
            }
        }

        /// <summary>
        /// Per-frame touch detection. Called from the frame-sync window, where avatar transforms are
        /// posed and free of in-flight jiggle jobs.
        /// </summary>
        public static void FrameTick()
        {
            int listenerCount = listeners.Count;
            if (listenerCount == 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            int budget = MaxTouchTestsPerFrame;

            for (int step = 0; step < listenerCount && budget > 0; step++)
            {
                // Round robin so a listener late in the list still gets serviced when the budget is
                // tight, rather than the first few starving the rest every frame.
                scanCursor = (scanCursor + 1) % listenerCount;
                Listener listener = listeners[scanCursor];
                if (listener.Root == null)
                {
                    listeners.RemoveAt(scanCursor);
                    listenerCount = listeners.Count;
                    if (listenerCount == 0)
                    {
                        return;
                    }
                    scanCursor = scanCursor % listenerCount;
                    continue;
                }
                RefreshRigs(listener, now);
                if (listener.Rigs.Length == 0)
                {
                    continue;
                }
                ScanListener(listener, now, ref budget);
            }
        }

        private static void ScanListener(Listener listener, float now, ref int budget)
        {
            Vector3 rootPosition = listener.Root.position;
            float range = BasisPlayerInteract.AvatarScaledRange(TouchScanRange);
            float rangeSquared = range * range;

            touchScratch.Clear();
            foreach (KeyValuePair<int, TouchState> pair in listener.Touching)
            {
                touchScratch.Add(pair.Key);
            }

            TestPlayer(listener, BasisLocalPlayer.Instance, LocalPlayerId(), rootPosition, rangeSquared, now, ref budget);

            foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
            {
                if (budget <= 0)
                {
                    break;
                }
                BasisRemotePlayer remote = pair.Value;
                if (remote == null || remote.IsDestroyed)
                {
                    continue;
                }
                // A player who cannot grab this avatar should not be reported as touching it either;
                // otherwise a block is visible to content as "they are still handling me".
                if (!BasisJiggleGrabPermissions.CanLocalGrab(remote))
                {
                    continue;
                }
                TestPlayer(listener, remote, pair.Key, rootPosition, rangeSquared, now, ref budget);
            }

            // Anything still in the scratch list was not re-confirmed this pass, so the hand left.
            int stale = touchScratch.Count;
            for (int Index = 0; Index < stale; Index++)
            {
                int key = touchScratch[Index];
                if (!listener.Touching.TryGetValue(key, out TouchState state) || !state.Latch.Touching)
                {
                    listener.Touching.Remove(key);
                    continue;
                }
                // The player went out of range or left entirely, so no contact was measured for them
                // this pass. Same dwell as a normal release, then forget them.
                if (state.Latch.Update(false, now, TouchDwellSeconds) != BasisJiggleTouchEdge.Ended)
                {
                    listener.Touching[key] = state;
                    continue;
                }
                listener.Touching.Remove(key);
                Dispatch(listener, new InteractionEvent
                {
                    Kind = InteractionKind.Touch,
                    Began = false,
                    PlayerId = (ushort)(key >> 8),
                    Hand = (byte)(key & 0xFF),
                    RigIndex = state.RigIndex,
                    Position = state.Position,
                });
            }
        }

        private static void TestPlayer(Listener listener, IBasisPlayer player, ushort playerId,
            Vector3 rootPosition, float rangeSquared, float now, ref int budget)
        {
            if (player == null || player.IsDestroyed)
            {
                return;
            }
            Transform anchor = player.AvatarAnimatorTransform != null ? player.AvatarAnimatorTransform : player.AvatarTransform;
            if (anchor != null && (anchor.position - rootPosition).sqrMagnitude > rangeSquared)
            {
                return;
            }

            for (byte hand = 0; hand < 2 && budget > 0; hand++)
            {
                budget--;
                if (!BasisJiggleGrabDriver.TryGetPlayerGrasp(player, hand, out Vector3 palm, out Vector3 fingerTip))
                {
                    continue;
                }
                bool touching = TryFindTouchedPoint(listener, palm, fingerTip, out byte rigIndex, out Vector3 position);
                UpdateTouchState(listener, playerId, hand, touching, rigIndex, position, now);
            }
        }

        private static bool TryFindTouchedPoint(Listener listener, Vector3 palm, Vector3 fingerTip,
            out byte rigIndex, out Vector3 position)
        {
            rigIndex = 0;
            position = default;
            float radius = BasisPlayerInteract.AvatarScaledRange(BasisJiggleGrabDriver.GrabSearchRadius);
            float bestScore = float.MaxValue;
            bool found = false;

            int count = Mathf.Min(listener.Rigs.Length, byte.MaxValue);
            for (int Index = 0; Index < count; Index++)
            {
                JiggleRig rig = listener.Rigs[Index];
                if (rig == null || !rig.isActiveAndEnabled || rig.GetLockedFromGrabbing())
                {
                    continue;
                }
                JiggleTree tree = rig.GetJiggleTree();
                if (tree == null || tree.dirty || tree.bones == null || tree.points == null)
                {
                    continue;
                }
                int pointCount = Mathf.Min(tree.points.Length, tree.bones.Length);
                for (int pointIndex = 1; pointIndex < pointCount; pointIndex++)
                {
                    if (!tree.points[pointIndex].hasTransform)
                    {
                        continue;
                    }
                    Transform bone = tree.bones[pointIndex];
                    if (!bone)
                    {
                        continue;
                    }
                    Vector3 bonePosition = bone.position;
                    if (!BasisJiggleGrabPicker.TryScoreGrasp(bonePosition, palm, fingerTip, radius, out float score))
                    {
                        continue;
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        rigIndex = (byte)Index;
                        position = bonePosition;
                        found = true;
                    }
                }
            }
            return found;
        }

        private static void UpdateTouchState(Listener listener, ushort playerId, byte hand, bool touching,
            byte rigIndex, Vector3 position, float now)
        {
            int key = (playerId << 8) | hand;
            touchScratch.Remove(key);

            if (!listener.Touching.TryGetValue(key, out TouchState state))
            {
                if (!touching)
                {
                    return;
                }
                state = new TouchState { Latch = BasisJiggleTouchLatch.Fresh };
            }
            if (touching)
            {
                state.RigIndex = rigIndex;
                state.Position = position;
            }

            BasisJiggleTouchEdge edge = state.Latch.Update(touching, now, TouchDwellSeconds);
            if (!state.Latch.Touching && edge == BasisJiggleTouchEdge.None)
            {
                // Never took hold, so there is nothing to remember about this hand.
                listener.Touching.Remove(key);
                return;
            }
            listener.Touching[key] = state;

            if (edge == BasisJiggleTouchEdge.None)
            {
                return;
            }
            Dispatch(listener, new InteractionEvent
            {
                Kind = InteractionKind.Touch,
                Began = edge == BasisJiggleTouchEdge.Began,
                PlayerId = playerId,
                Hand = hand,
                RigIndex = state.RigIndex,
                Position = state.Position,
            });
        }

        private static void Dispatch(Listener listener, InteractionEvent interaction)
        {
            try
            {
                listener.Handler(interaction);
            }
            catch (Exception exception)
            {
                // A listener that throws is a bug in that listener, not a reason to drop the rest of
                // this frame's events for everyone else.
                BasisDebug.LogError($"Jiggle interaction listener threw: {exception}");
            }
        }

        private static int IndexOfRig(Listener listener, JiggleRig rig)
        {
            RefreshRigs(listener, Time.unscaledTime);
            JiggleRig[] rigs = listener.Rigs;
            for (int Index = 0; Index < rigs.Length; Index++)
            {
                if (ReferenceEquals(rigs[Index], rig))
                {
                    return Index;
                }
            }
            return -1;
        }

        private static void RefreshRigs(Listener listener, float now)
        {
            if (now - listener.RigsRefreshedAt < RigRefreshSeconds)
            {
                return;
            }
            listener.RigsRefreshedAt = now;
            listener.Rigs = listener.Root != null
                ? listener.Root.GetComponentsInChildren<JiggleRig>(true)
                : Array.Empty<JiggleRig>();
        }

        private static ushort LocalPlayerId()
        {
            BasisJiggleGrabDriver.TryGetLocalPlayerId(out ushort id);
            return id;
        }
    }
}
