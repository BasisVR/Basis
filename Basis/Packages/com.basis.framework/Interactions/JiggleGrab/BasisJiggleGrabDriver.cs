using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.TransformBinders.BoneControl;
using GatorDragonGames.JigglePhysics;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.BasisSdk.Interactions
{
    [AutoStaticsCleanup]
    public static partial class BasisJiggleGrabDriver
    {
        public const int MaxAnnouncedGrabs = 2048;
        public const int MaxAppliedGrabs = JiggleGrabConstraint.MaxTotalGrabs;
        public const int MaxGrabsPerTree = JiggleGrabConstraint.MaxGrabsPerTree;
        public const float GrabStrength = 1f;
        // Measured palm to bone ORIGIN. This was three times larger while the search still ran from
        // the wrist, where the slack was really paying for the offset to the palm rather than for
        // any reach; from the palm itself a hand-sized volume is enough and is far less grabby.
        public const float GrabSearchRadius = 0.0875f;
        // How far a hand may point to grab a chain it is not touching.
        public const float GrabRayLength = 3f;
        // Analog triggers rarely report a clean 1, so the press is a threshold rather than equality.
        public const float MaxTargetScaleRadiusMultiplier = 2f;
        // A chain this much further than the pick radius still counts as being against the hand, and
        // is taken in preference to anything the hand merely points at.
        public const float ReachIntentRadiusMultiplier = 3f;
        public const float ReleaseDistance = 1.25f;
        public const float TargetClampDistance = 2f;
        public const float ObserverSkipDistance = 3f;
        public const float ReassertIntervalSeconds = 5f;
        public const float AnnouncedTimeToLiveSeconds = 15f;
        public const float UnresolvedReleaseSeconds = 2f;
        public const int DormantPromotionsPerFrame = 8;

        public class GrabState
        {
            public ushort grabberId;
            public ushort targetId;
            public byte rigIndex;
            public ushort pointIndex;
            public byte hand;
            public Vector3 grabOffset;
            public uint boneNameHash;
            public float lastSeenTime;
            public float lastAssertTime;
            public float unresolvedSince;
            public bool isLocalGrab;
            public bool isEditorGrab;
            public Vector3 editorTarget;
            public float editorMaxStretchFactor;
            public bool applied;
            public bool reportedToListeners;
            public BasisInput localInput;

            public JiggleTree tree;
            public int resolvedRootID;
            public int resolvedPointIndex = -1;
            public Animator cachedGrabberAnimator;
            public Transform cachedGrabberHand;
        }

        private static readonly Dictionary<ulong, GrabState> announced = new Dictionary<ulong, GrabState>();
        private static readonly List<GrabState> allGrabs = new List<GrabState>();
        private static readonly List<GrabState> applied = new List<GrabState>();
        private static readonly List<GrabState> removalScratch = new List<GrabState>();
        private static readonly List<GrabState> demotionScratch = new List<GrabState>();
        [NoAutoStaticsCleanup] private static readonly JiggleGrabConstraint[] constraintScratch = new JiggleGrabConstraint[MaxAppliedGrabs];
        private static int lastPushedCount;
        private static int promotionCursor;
        private static bool initialized;

        private static ulong Key(ushort targetId, byte rigIndex, ushort pointIndex)
        {
            return ((ulong)targetId << 32) | ((ulong)rigIndex << 16) | pointIndex;
        }

        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }
            BasisNetworkPlayer.OnPlayerLeft += OnPlayerLeft;
            initialized = true;
        }

        public static void Shutdown()
        {
            if (!initialized)
            {
                return;
            }
            BasisNetworkPlayer.OnPlayerLeft -= OnPlayerLeft;
            announced.Clear();
            allGrabs.Clear();
            applied.Clear();
            BasisJiggleGrabPermissions.Clear();
            if (lastPushedCount > 0)
            {
                lastPushedCount = 0;
                JigglePhysics.SetGrabConstraints(constraintScratch, 0);
            }
            initialized = false;
        }

        public static bool TryGetLocalPlayerId(out ushort id)
        {
            return BasisNetworkConnection.TryGetLocalPlayerID(out id);
        }

        public static uint HashBoneName(string name)
        {
            unchecked
            {
                uint hash = 2166136261;
                int length = name.Length;
                for (int Index = 0; Index < length; Index++)
                {
                    hash = (hash ^ name[Index]) * 16777619;
                }
                return hash;
            }
        }

        public static bool TryBeginGrab(BasisInput input, bool freshPress)
        {
            if (!freshPress || input == null)
            {
                return false;
            }
            if (!BasisJiggleGrabPermissions.MasterEnabled)
            {
                RecordAttempt("grabbing disabled in settings");
                return false;
            }
            if (input.BasisUIRaycast != null && input.BasisUIRaycast.HadRaycastUITarget)
            {
                RecordAttempt("pointing at UI");
                return false;
            }
            if (!input.TryGetRole(out BasisBoneTrackedRole role))
            {
                RecordAttempt("input has no bone role");
                return false;
            }
            // Role alone is not enough: VR also has a CenterEye device, and it must not ray-grab
            // from the player's face.
            bool desktop = BasisPlayerInteract.IsDesktopCenterEye(input);
            if (!desktop && role != BasisBoneTrackedRole.LeftHand && role != BasisBoneTrackedRole.RightHand)
            {
                return false;
            }
            if (!desktop && !input.HasControl)
            {
                RecordAttempt("hand input has no bone control");
                return false;
            }

            TryGetLocalPlayerId(out ushort localId);
            byte hand = desktop
                ? (BasisDominantHand.IsLeftHanded ? (byte)0 : (byte)1)
                : (role == BasisBoneTrackedRole.LeftHand ? (byte)0 : (byte)1);
            if (IsHandBusy(localId, hand))
            {
                RecordAttempt("that hand is already grabbing");
                return false;
            }

            JiggleRig bestRig = null;
            BasisRemotePlayer bestTarget = null;
            byte bestRigIndex = 0;
            int bestPointIndex = -1;
            Vector3 bestPointPosition = default;
            float bestScore = float.MaxValue;
            bool pointed = desktop;
            bool includeLocalAvatar = CanGrabLocalAvatar(desktop);

            float radius = BasisPlayerInteract.AvatarScaledRange(GrabSearchRadius);
            GrabQuery grasp = default;
            if (desktop)
            {
                SearchRigs(localId, includeLocalAvatar, GrabQuery.Pointing(input.RaycastCoord.position,
                        input.RaycastCoord.rotation * Vector3.forward,
                        BasisPlayerInteract.AvatarScaledRange(BasisPlayerInteract.raycastDistance), radius),
                    ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);
            }
            else
            {
                GetHandGrasp(input, hand, out Vector3 palm, out Vector3 fingerTip);
                grasp = GrabQuery.Grasp(palm, fingerTip, radius);
                SearchRigs(localId, includeLocalAvatar, grasp,
                    ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);

                // Missed the tight volume, but a chain is still against the hand — take that one.
                // Whatever the hand is in contact with is what the player believes they are grabbing,
                // so refusing here (or worse, letting the ray pick something across the room) both
                // read as the grab being broken.
                if (bestPointIndex < 0)
                {
                    SearchRigs(localId, includeLocalAvatar, grasp.WithRadius(radius * ReachIntentRadiusMultiplier),
                        ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);
                }

                // Only with nothing whatsoever in the hand does pointing get a say.
                if (bestPointIndex < 0)
                {
                    pointed = true;
                    SearchRigs(localId, includeLocalAvatar, GrabQuery.Pointing(input.RaycastCoord.position,
                            input.RaycastCoord.rotation * Vector3.forward,
                            BasisPlayerInteract.AvatarScaledRange(GrabRayLength), radius),
                        ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);
                }
            }

            if (bestPointIndex < 0)
            {
                RecordMissedReach(grasp, desktop);
                return false;
            }

            var tree = bestRig.GetJiggleTree();
            if (tree == null || tree.bones == null || bestPointIndex >= tree.bones.Length || !tree.bones[bestPointIndex])
            {
                RecordAttempt("rig has no built tree yet");
                return false;
            }

            // Never fall back to "me" when a remote's id will not resolve. Two players in the same
            // avatar have the same rig at the same index, so a silent fallback grabs the identical
            // chain on your OWN body and looks like the grab landed on the wrong person.
            ushort targetId;
            if (bestTarget != null)
            {
                if (!BasisNetworkPlayers.PlayerToNetworkedPlayer(bestTarget, out BasisNetworkPlayer targetNet))
                {
                    RecordAttempt("could not resolve that player's network id");
                    return false;
                }
                targetId = targetNet.playerId;
            }
            else
            {
                targetId = localId;
            }

            Animator grabAnimatorCache = null;
            Transform grabHandCache = null;
            if (!TryGetHandBonePose(localId, hand, out Vector3 handPos, out Quaternion handRot, ref grabAnimatorCache, ref grabHandCache))
            {
                RecordAttempt("could not resolve the avatar hand bone");
                return false;
            }

            var state = new GrabState
            {
                grabberId = localId,
                targetId = targetId,
                rigIndex = bestRigIndex,
                pointIndex = (ushort)bestPointIndex,
                hand = hand,
                grabOffset = Quaternion.Inverse(handRot) * (bestPointPosition - handPos),
                boneNameHash = HashBoneName(tree.bones[bestPointIndex].name),
                lastSeenTime = Time.unscaledTime,
                lastAssertTime = Time.unscaledTime,
                isLocalGrab = true,
                localInput = input,
                cachedGrabberAnimator = grabAnimatorCache,
                cachedGrabberHand = grabHandCache,
            };

            if (!TryInsert(state))
            {
                RecordAttempt("someone else already holds that point");
                return false;
            }
            RecordAttempt(bestTarget != null
                ? $"grabbed {tree.bones[bestPointIndex].name} on #{targetId}"
                : $"grabbed {tree.bones[bestPointIndex].name} on yourself");
            BasisNetworkHandleJiggleGrab.SendGrabStart(state.targetId, state.rigIndex, state.pointIndex, state.hand, state.boneNameHash, state.grabOffset);
            return true;
        }

        public static string LastAttemptResult { get; private set; } = "no grab attempted yet";
        public static float LastAttemptTime { get; private set; }

        private static void RecordAttempt(string result)
        {
            LastAttemptResult = result;
            LastAttemptTime = Time.unscaledTime;
        }

        private struct GrabQuery
        {
            public bool IsPointing;
            public Vector3 Origin;
            public Vector3 Target;
            public float MaxDistance;
            public float Radius;

            public static GrabQuery Grasp(Vector3 palm, Vector3 fingerTip, float radius) => new GrabQuery
            {
                IsPointing = false, Origin = palm, Target = fingerTip, Radius = radius,
            };

            public static GrabQuery Pointing(Vector3 origin, Vector3 direction, float maxDistance, float radius) => new GrabQuery
            {
                IsPointing = true, Origin = origin, Target = direction, MaxDistance = maxDistance, Radius = radius,
            };

            public GrabQuery WithRadius(float radius)
            {
                GrabQuery copy = this;
                copy.Radius = radius;
                return copy;
            }

            public bool TryScore(Vector3 candidate, out float score)
            {
                return IsPointing
                    ? BasisJiggleGrabPicker.TryScorePointing(candidate, Origin, Target, MaxDistance, Radius, out score)
                    : BasisJiggleGrabPicker.TryScoreGrasp(candidate, Origin, Target, Radius, out score);
            }
        }

        private static void RecordMissedReach(GrabQuery grasp, bool desktop)
        {
            if (desktop)
            {
                RecordAttempt("nothing grabbable along the aim ray");
                return;
            }

            JiggleRig probeRig = null;
            BasisRemotePlayer probeTarget = null;
            byte probeRigIndex = 0;
            int probePointIndex = -1;
            Vector3 probePosition = default;
            float probeScore = float.MaxValue;
            SearchRigs(0, true, grasp.WithRadius(2f),
                ref probeRig, ref probeTarget, ref probeRigIndex, ref probePointIndex, ref probePosition, ref probeScore);

            if (probePointIndex < 0)
            {
                RecordAttempt("no jiggle rig within 2m of the hand");
                return;
            }
            RecordAttempt($"nearest chain was {probeScore:0.00}m from your grip, needs {grasp.Radius * ReachIntentRadiusMultiplier:0.00}m");
        }

        internal static bool CanGrabLocalAvatar(bool desktopInput)
        {
            return !desktopInput;
        }

        private static void SearchRigs(ushort localId, bool includeLocalAvatar, GrabQuery query,
            ref JiggleRig bestRig, ref BasisRemotePlayer bestTarget, ref byte bestRigIndex, ref int bestPointIndex,
            ref Vector3 bestPointPosition, ref float bestScore)
        {
            float candidateRange = BasisPlayerInteract.AvatarScaledRange(3f) + query.MaxDistance;
            float candidateRangeSq = candidateRange * candidateRange;
            foreach (KeyValuePair<ushort, BasisRemotePlayer> pair in BasisNetworkPlayers.RemotePlayers)
            {
                BasisRemotePlayer remote = pair.Value;
                if (remote == null || remote.IsDestroyed || !BasisJiggleGrabPermissions.CanLocalGrab(remote))
                {
                    continue;
                }
                // Proximity pre-filter so a crowd costs one distance check per player rather than a
                // walk of everyone's chains — but a remote whose anchor cannot be read is NOT
                // rejected. Silently skipping them would leave only the local rigs in the running,
                // and in a lobby where two people wear the same avatar that reads as the grab
                // landing on your own body.
                Transform anchor = remote.AvatarAnimatorTransform != null ? remote.AvatarAnimatorTransform : remote.AvatarTransform;
                if (anchor != null && (anchor.position - query.Origin).sqrMagnitude > candidateRangeSq)
                {
                    continue;
                }
                ScoreRigArray(remote.RemoteAvatarDriver?.JiggleRigs, remote, query,
                    ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);
            }

            // A desktop pointer originates at the face. Falling back to the local rigs makes an
            // ordinary click or click-and-drag catch hair and collar chains surrounding that ray.
            // Hands can deliberately touch their own avatar in VR, so keep self-grabbing there.
            if (includeLocalAvatar && bestPointIndex < 0)
            {
                ScoreRigArray(BasisLocalAvatarDriver.JiggleRigs, null, query,
                    ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);
            }
        }

        public static float GetAvatarScaleFactor(IBasisPlayer player)
        {
            float scale = player?.BasisAvatar != null ? player.BasisAvatar.HumanScale : 1f;
            return scale > 0.0001f && !float.IsInfinity(scale) && !float.IsNaN(scale) ? scale : 1f;
        }

        private static void ScoreRigArray(JiggleRig[] rigs, BasisRemotePlayer owner, GrabQuery query,
            ref JiggleRig bestRig, ref BasisRemotePlayer bestTarget, ref byte bestRigIndex, ref int bestPointIndex,
            ref Vector3 bestPointPosition, ref float bestScore)
        {
            if (rigs == null)
            {
                return;
            }
            if (owner != null)
            {
                float localFactor = GetAvatarScaleFactor(BasisLocalPlayer.Instance);
                float targetFactor = GetAvatarScaleFactor(owner);
                if (targetFactor > localFactor && localFactor > 0.0001f)
                {
                    // Hard ceiling as well as a sane measure: this only ever widens the tolerance a
                    // little for a larger avatar, and can never turn a hand-sized pick into a reach.
                    query = query.WithRadius(query.Radius * Mathf.Min(targetFactor / localFactor, MaxTargetScaleRadiusMultiplier));
                }
            }
            int count = Mathf.Min(rigs.Length, byte.MaxValue);
            for (int Index = 0; Index < count; Index++)
            {
                JiggleRig rig = rigs[Index];
                if (rig == null || !rig.isActiveAndEnabled || rig.GetLockedFromGrabbing())
                {
                    continue;
                }
                JiggleTree tree = rig.GetJiggleTree();
                if (tree == null || tree.dirty || tree.bones == null || tree.points == null)
                {
                    continue;
                }
                // Scored here rather than through the rig's own nearest-point helper so every
                // candidate goes through the same picker the tests exercise, and so a grasp can be
                // measured against the whole hand instead of a single point.
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
                    if (!query.TryScore(bonePosition, out float score) || score >= bestScore)
                    {
                        continue;
                    }
                    bestScore = score;
                    bestRig = rig;
                    bestTarget = owner;
                    bestRigIndex = (byte)Index;
                    bestPointIndex = pointIndex;
                    bestPointPosition = bonePosition;
                }
            }
        }

        private static void GetHandGrasp(BasisInput input, byte hand, out Vector3 palm, out Vector3 fingerTip)
        {
            if (TryGetGraspFromMapping(BasisLocalPlayer.Instance?.LocalRigDriver?.basisTransformMapping, hand, out palm, out fingerTip))
            {
                return;
            }
            palm = GetSearchHandPosition(input, hand);
            fingerTip = palm;
        }

        public static bool TryGetPlayerGrasp(IBasisPlayer player, byte hand, out Vector3 palm, out Vector3 fingerTip)
        {
            palm = default;
            fingerTip = default;
            if (player == null || hand > 1)
            {
                return false;
            }
            BasisTransformMapping mapping = player is BasisRemotePlayer remote
                ? remote.RemoteAvatarDriver?.References
                : BasisLocalPlayer.Instance?.LocalRigDriver?.basisTransformMapping;
            return TryGetGraspFromMapping(mapping, hand, out palm, out fingerTip);
        }

        private static bool TryGetGraspFromMapping(BasisTransformMapping mapping, byte hand, out Vector3 palm, out Vector3 fingerTip)
        {
            palm = default;
            fingerTip = default;
            if (mapping == null)
            {
                return false;
            }
            Transform wrist = hand == 0 ? mapping.leftHand : mapping.rightHand;
            if (wrist == null)
            {
                return false;
            }
            Transform[] middle = hand == 0 ? mapping.LeftMiddle : mapping.RightMiddle;
            Transform knuckle = middle != null && middle.Length > 0 ? middle[0] : null;
            palm = knuckle != null ? Vector3.Lerp(wrist.position, knuckle.position, 0.5f) : wrist.position;

            Transform tip = null;
            if (middle != null)
            {
                for (int Index = middle.Length - 1; Index >= 0 && tip == null; Index--)
                {
                    tip = middle[Index];
                }
            }
            fingerTip = tip != null ? tip.position : palm + (palm - wrist.position);
            return true;
        }

        public static bool TryGetHandSearchPosition(byte hand, out Vector3 position)
        {
            position = default;
            if (hand > 1)
            {
                return false;
            }
            BasisTransformMapping mapping = BasisLocalPlayer.Instance?.LocalRigDriver?.basisTransformMapping;
            if (mapping == null)
            {
                return false;
            }
            Transform wrist = hand == 0 ? mapping.leftHand : mapping.rightHand;
            if (wrist == null)
            {
                return false;
            }
            Transform[] middle = hand == 0 ? mapping.LeftMiddle : mapping.RightMiddle;
            Transform knuckle = middle != null && middle.Length > 0 ? middle[0] : null;
            position = knuckle != null ? Vector3.Lerp(wrist.position, knuckle.position, 0.5f) : wrist.position;
            return true;
        }

        private static Vector3 GetSearchHandPosition(BasisInput input, byte hand)
        {
            if (TryGetHandSearchPosition(hand, out Vector3 palm))
            {
                return palm;
            }
            BasisLocalBoneControl bone = input.Control;
            if (bone != null)
            {
                var ik = bone.IKWorldData;
                if (ik.rotation.x != 0f || ik.rotation.y != 0f || ik.rotation.z != 0f || ik.rotation.w != 0f)
                {
                    return ik.position;
                }
                return bone.OutgoingWorldData.position;
            }
            return input.RaycastCoord.position;
        }

        public static bool IsInputGrabbing(BasisInput input)
        {
            if (input == null)
            {
                return false;
            }
            int count = allGrabs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                GrabState state = allGrabs[Index];
                if (state.isLocalGrab && !state.isEditorGrab && ReferenceEquals(state.localInput, input))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsHandBusy(ushort localId, byte hand)
        {
            int count = allGrabs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                GrabState state = allGrabs[Index];
                if (state.isLocalGrab && state.grabberId == localId && state.hand == hand)
                {
                    return true;
                }
            }
            return false;
        }

        public static void OnRemoteGrabEvent(byte op, ushort senderId, ushort targetId, byte rigIndex, ushort pointIndex, byte hand, uint boneNameHash, Vector3 grabOffset)
        {
            TryGetLocalPlayerId(out ushort localId);
            switch (op)
            {
                case BasisNetworkHandleJiggleGrab.OpStart:
                {
                    if (senderId == localId)
                    {
                        return;
                    }
                    if (targetId == localId && BasisJiggleGrabPermissions.LocalPlayerDenies(senderId))
                    {
                        if (!BasisJiggleGrabPermissions.IsDenied(senderId, localId))
                        {
                            BasisJiggleGrabPermissions.RegisterDeny(senderId, localId);
                            BasisNetworkHandleJiggleGrab.SendGrabDeny(senderId);
                        }
                        return;
                    }
                    if (!BasisJiggleGrabPermissions.ObserverAllows(senderId, targetId, localId))
                    {
                        return;
                    }
                    var state = new GrabState
                    {
                        grabberId = senderId,
                        targetId = targetId,
                        rigIndex = rigIndex,
                        pointIndex = pointIndex,
                        hand = hand,
                        grabOffset = grabOffset,
                        boneNameHash = boneNameHash,
                        lastSeenTime = Time.unscaledTime,
                        isLocalGrab = false,
                    };
                    TryInsert(state);
                    break;
                }
                case BasisNetworkHandleJiggleGrab.OpStop:
                {
                    ulong key = Key(targetId, rigIndex, pointIndex);
                    if (announced.TryGetValue(key, out GrabState state) && state.grabberId == senderId)
                    {
                        Remove(state);
                    }
                    break;
                }
                case BasisNetworkHandleJiggleGrab.OpDeny:
                {
                    // senderId is the denying target; the payload names the denied grabber.
                    ushort deniedGrabberId = targetId;
                    BasisJiggleGrabPermissions.RegisterDeny(deniedGrabberId, senderId);
                    RemoveMatching(state => state.grabberId == deniedGrabberId && state.targetId == senderId);
                    break;
                }
            }
        }

        private static bool TryInsert(GrabState state)
        {
            ulong key = Key(state.targetId, state.rigIndex, state.pointIndex);
            if (announced.TryGetValue(key, out GrabState existing))
            {
                if (existing.grabberId == state.grabberId)
                {
                    existing.lastSeenTime = Time.unscaledTime;
                    existing.grabOffset = state.grabOffset;
                    existing.hand = state.hand;
                    return existing.isLocalGrab;
                }
                if (existing.grabberId <= state.grabberId)
                {
                    return false;
                }
                Remove(existing);
            }
            if (announced.Count >= MaxAnnouncedGrabs)
            {
                return false;
            }
            announced[key] = state;
            allGrabs.Add(state);
            return true;
        }

        private static void Remove(GrabState state)
        {
            announced.Remove(Key(state.targetId, state.rigIndex, state.pointIndex));
            allGrabs.Remove(state);
            if (state.applied)
            {
                state.applied = false;
                applied.Remove(state);
            }
            if (state.reportedToListeners)
            {
                state.reportedToListeners = false;
                if (BasisJiggleInteractionEvents.HasListeners
                    && TryResolveRigForState(state, out JiggleRig rig))
                {
                    BasisJiggleInteractionEvents.ReportGrab(rig, state.grabberId, state.hand, BonePositionOf(state), false);
                }
            }
        }

        private static bool TryResolveRigForState(GrabState state, out JiggleRig rig)
        {
            TryGetLocalPlayerId(out ushort localId);
            return TryResolveRig(state.targetId, localId, state.rigIndex, out rig);
        }

        private static void RemoveMatching(System.Predicate<GrabState> predicate)
        {
            removalScratch.Clear();
            int count = allGrabs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                if (predicate(allGrabs[Index]))
                {
                    removalScratch.Add(allGrabs[Index]);
                }
            }
            count = removalScratch.Count;
            for (int Index = 0; Index < count; Index++)
            {
                Remove(removalScratch[Index]);
            }
        }

        private static void OnPlayerLeft(BasisNetworkPlayer player)
        {
            if (player == null)
            {
                return;
            }
            ushort id = player.playerId;
            RemoveMatching(state => state.grabberId == id || state.targetId == id);
        }

        public static void DropGrabsForPlayer(IBasisPlayer player)
        {
            if (player == null || !BasisNetworkPlayers.PlayerToNetworkedPlayer(player, out BasisNetworkPlayer networkPlayer) || networkPlayer == null)
            {
                return;
            }
            ushort id = networkPlayer.playerId;
            RemoveMatching(state =>
            {
                if (state.isEditorGrab || (state.grabberId != id && state.targetId != id))
                {
                    return false;
                }
                if (state.isLocalGrab)
                {
                    BasisNetworkHandleJiggleGrab.SendGrabStop(state.targetId, state.rigIndex, state.pointIndex);
                }
                return true;
            });
        }

        public static void ReleaseLocalGrabs()
        {
            RemoveMatching(state =>
            {
                if (state.isLocalGrab)
                {
                    if (!state.isEditorGrab)
                    {
                        BasisNetworkHandleJiggleGrab.SendGrabStop(state.targetId, state.rigIndex, state.pointIndex);
                    }
                    return true;
                }
                return false;
            });
        }

        public static void RevokePlayer(ushort playerId)
        {
            TryGetLocalPlayerId(out ushort localId);
            RemoveMatching(state =>
            {
                if (state.isEditorGrab)
                {
                    return false;
                }
                if (state.isLocalGrab && state.targetId == playerId)
                {
                    BasisNetworkHandleJiggleGrab.SendGrabStop(state.targetId, state.rigIndex, state.pointIndex);
                    return true;
                }
                if (state.grabberId == playerId && state.targetId == localId)
                {
                    return true;
                }
                return false;
            });
            BasisJiggleGrabPermissions.RegisterDeny(playerId, localId);
            BasisNetworkHandleJiggleGrab.SendGrabDeny(playerId);
        }

        private static bool TryGetHandBonePose(ushort grabberId, byte hand, out Vector3 position, out Quaternion rotation, ref Animator animatorCache, ref Transform handCache)
        {
            position = default;
            rotation = Quaternion.identity;
            Animator animator = null;
            TryGetLocalPlayerId(out ushort localId);
            if (grabberId == localId)
            {
                animator = BasisLocalPlayer.Instance != null && BasisLocalPlayer.Instance.BasisAvatar != null
                    ? BasisLocalPlayer.Instance.BasisAvatar.Animator
                    : null;
            }
            else if (BasisNetworkPlayers.RemotePlayers.TryGetValue(grabberId, out BasisRemotePlayer remote))
            {
                animator = remote != null && remote.BasisAvatar != null ? remote.BasisAvatar.Animator : null;
            }
            if (animator == null)
            {
                return false;
            }
            // An avatar that is mid load (or is not humanoid at all) has an Animator with no rig on
            // it yet, and GetBoneTransform throws instead of returning null for that case. Treat it
            // as unresolved so the grab parks or releases on its own.
            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                return false;
            }
            if (animatorCache != animator || handCache == null)
            {
                handCache = animator.GetBoneTransform(hand == 0 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                animatorCache = animator;
            }
            if (handCache == null)
            {
                return false;
            }
            handCache.GetPositionAndRotation(out position, out rotation);
            return true;
        }

        private static bool TryResolveRig(ushort targetId, ushort localId, byte rigIndex, out JiggleRig rig)
        {
            rig = null;
            JiggleRig[] rigs;
            if (targetId == localId)
            {
                rigs = BasisLocalAvatarDriver.JiggleRigs;
            }
            else if (BasisNetworkPlayers.RemotePlayers.TryGetValue(targetId, out BasisRemotePlayer remote) && remote != null && !remote.IsDestroyed)
            {
                rigs = remote.RemoteAvatarDriver != null ? remote.RemoteAvatarDriver.JiggleRigs : null;
            }
            else
            {
                return false;
            }
            if (rigs == null || rigIndex >= rigs.Length)
            {
                return false;
            }
            rig = rigs[rigIndex];
            return rig != null && rig.isActiveAndEnabled && !rig.GetLockedFromGrabbing();
        }

        private static bool TryResolvePoint(GrabState state, JiggleRig rig)
        {
            JiggleTree tree = rig.GetJiggleTree();
            if (tree == null || tree.dirty || tree.bones == null || tree.points == null)
            {
                return false;
            }
            if (ReferenceEquals(state.tree, tree) && state.resolvedRootID == tree.rootID && state.resolvedPointIndex >= 0)
            {
                return true;
            }
            Transform[] bones = tree.bones;
            int resolved = -1;
            if (state.pointIndex < bones.Length && bones[state.pointIndex]
                && HashBoneName(bones[state.pointIndex].name) == state.boneNameHash)
            {
                resolved = state.pointIndex;
            }
            else
            {
                int length = bones.Length;
                for (int Index = 1; Index < length; Index++)
                {
                    if (bones[Index] && HashBoneName(bones[Index].name) == state.boneNameHash)
                    {
                        resolved = Index;
                        break;
                    }
                }
            }
            if (resolved < 0 || resolved >= tree.points.Length || !tree.points[resolved].hasTransform)
            {
                return false;
            }
            state.tree = tree;
            state.resolvedRootID = tree.rootID;
            state.resolvedPointIndex = resolved;
            return true;
        }

        private static int AppliedCountForTree(int rootID)
        {
            int count = 0;
            int appliedCount = applied.Count;
            for (int Index = 0; Index < appliedCount; Index++)
            {
                if (applied[Index].resolvedRootID == rootID)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool TryActivate(GrabState state, ushort localId)
        {
            if (applied.Count >= MaxAppliedGrabs)
            {
                return false;
            }
            if (!BasisJiggleGrabPermissions.ObserverAllows(state.grabberId, state.targetId, localId))
            {
                return false;
            }
            if (!TryResolveRig(state.targetId, localId, state.rigIndex, out JiggleRig rig) || !TryResolvePoint(state, rig))
            {
                return false;
            }
            if (AppliedCountForTree(state.resolvedRootID) >= MaxGrabsPerTree)
            {
                return false;
            }
            state.applied = true;
            state.unresolvedSince = 0f;
            applied.Add(state);
            // Reported from here rather than from the press so that a remote's grab, a re-assert and
            // a locally started grab all announce the same way, once, when it actually takes hold.
            if (BasisJiggleInteractionEvents.HasListeners && !state.reportedToListeners)
            {
                state.reportedToListeners = true;
                BasisJiggleInteractionEvents.ReportGrab(rig, state.grabberId, state.hand, BonePositionOf(state), true);
            }
            return true;
        }

        private static Vector3 BonePositionOf(GrabState state)
        {
            JiggleTree tree = state.tree;
            int index = state.resolvedPointIndex;
            if (tree == null || tree.bones == null || index < 0 || index >= tree.bones.Length || !tree.bones[index])
            {
                return default;
            }
            return tree.bones[index].position;
        }

        public static void FrameTick()
        {
            if (allGrabs.Count == 0)
            {
                if (lastPushedCount > 0)
                {
                    lastPushedCount = 0;
                    JigglePhysics.SetGrabConstraints(constraintScratch, 0);
                }
                return;
            }

            float now = Time.unscaledTime;
            TryGetLocalPlayerId(out ushort localId);
            BasisJiggleGrabPermissions.PruneDenies();

            removalScratch.Clear();
            int total = allGrabs.Count;
            for (int Index = 0; Index < total; Index++)
            {
                GrabState state = allGrabs[Index];
                if (!state.isLocalGrab && now - state.lastSeenTime > AnnouncedTimeToLiveSeconds)
                {
                    removalScratch.Add(state);
                }
                else if (state.isLocalGrab && !state.isEditorGrab && now - state.lastAssertTime > ReassertIntervalSeconds)
                {
                    state.lastAssertTime = now;
                    BasisNetworkHandleJiggleGrab.SendGrabStart(state.targetId, state.rigIndex, state.pointIndex, state.hand, state.boneNameHash, state.grabOffset);
                }
            }
            int removeCount = removalScratch.Count;
            for (int Index = 0; Index < removeCount; Index++)
            {
                Remove(removalScratch[Index]);
            }

            int dormantBudget = DormantPromotionsPerFrame;
            int grabCount = allGrabs.Count;
            for (int step = 0; step < grabCount && dormantBudget > 0; step++)
            {
                promotionCursor = (promotionCursor + 1) % grabCount;
                GrabState state = allGrabs[promotionCursor];
                if (!state.applied)
                {
                    dormantBudget--;
                    TryActivate(state, localId);
                }
            }

            int constraintCount = 0;
            removalScratch.Clear();
            demotionScratch.Clear();
            if (CollectGizmoSamples)
            {
                gizmoSamples.Clear();
            }
            int appliedCount = applied.Count;
            for (int Index = 0; Index < appliedCount; Index++)
            {
                GrabState state = applied[Index];

                // Editor-driven grabs keep their tree resolved from the scene-view pick and take a
                // world target straight from the mouse, so they exercise the constraint pipeline
                // without a player id, a hand bone or a network peer.
                if (state.isEditorGrab)
                {
                    if (state.tree == null || state.tree.bones == null || state.resolvedPointIndex < 0
                        || state.resolvedPointIndex >= state.tree.bones.Length
                        || !state.tree.bones[state.resolvedPointIndex])
                    {
                        removalScratch.Add(state);
                        continue;
                    }
                    if (constraintCount < MaxAppliedGrabs)
                    {
                        constraintScratch[constraintCount++] = new JiggleGrabConstraint
                        {
                            rootID = state.resolvedRootID,
                            pointIndex = state.resolvedPointIndex,
                            targetPosition = state.editorTarget,
                            strength = GrabStrength,
                            maxStretchFactor = state.editorMaxStretchFactor,
                        };
                    }
                    continue;
                }

                if (!BasisJiggleGrabPermissions.ObserverAllows(state.grabberId, state.targetId, localId)
                    || !TryResolveRig(state.targetId, localId, state.rigIndex, out JiggleRig rig)
                    || !TryResolvePoint(state, rig))
                {
                    HandleUnresolved(state, now);
                    continue;
                }

                if (state.isLocalGrab && !IsLocalHoldHeld(state))
                {
                    removalScratch.Add(state);
                    BasisNetworkHandleJiggleGrab.SendGrabStop(state.targetId, state.rigIndex, state.pointIndex);
                    continue;
                }

                if (!TryGetHandBonePose(state.grabberId, state.hand, out Vector3 handPos, out Quaternion handRot,
                        ref state.cachedGrabberAnimator, ref state.cachedGrabberHand))
                {
                    HandleUnresolved(state, now);
                    continue;
                }
                state.unresolvedSince = 0f;

                Transform bone = state.tree.bones[state.resolvedPointIndex];
                if (!bone)
                {
                    HandleUnresolved(state, now);
                    continue;
                }
                Vector3 bonePosition = bone.position;
                float handToBone = Vector3.Distance(handPos, bonePosition);

                // A grab made by pointing starts with the hand already away from the bone, so the
                // slack has to include that reach or it would release on the very next frame. The
                // offset carries the reach on the wire, so every client derives the same number.
                float slack = state.grabOffset.magnitude + BasisPlayerInteract.AvatarScaledRange(ReleaseDistance);
                if (state.isLocalGrab && handToBone > slack)
                {
                    removalScratch.Add(state);
                    BasisNetworkHandleJiggleGrab.SendGrabStop(state.targetId, state.rigIndex, state.pointIndex);
                    continue;
                }
                if (handToBone > slack + BasisPlayerInteract.AvatarScaledRange(ObserverSkipDistance))
                {
                    continue;
                }

                Vector3 target = handPos + handRot * state.grabOffset;
                float clamp = BasisPlayerInteract.AvatarScaledRange(TargetClampDistance);
                Vector3 fromBone = target - bonePosition;
                float fromBoneLength = fromBone.magnitude;
                if (fromBoneLength > clamp)
                {
                    target = bonePosition + fromBone * (clamp / fromBoneLength);
                }

                float stretchFactor = rig.GetMaxGrabStretch();
                if (constraintCount < MaxAppliedGrabs)
                {
                    constraintScratch[constraintCount++] = new JiggleGrabConstraint
                    {
                        rootID = state.resolvedRootID,
                        pointIndex = state.resolvedPointIndex,
                        targetPosition = target,
                        strength = GrabStrength,
                        maxStretchFactor = stretchFactor,
                    };
                }
                if (CollectGizmoSamples)
                {
                    AddGizmoSample(state, bonePosition, target, stretchFactor);
                }
            }

            removeCount = removalScratch.Count;
            for (int Index = 0; Index < removeCount; Index++)
            {
                Remove(removalScratch[Index]);
            }
            int demoteCount = demotionScratch.Count;
            for (int Index = 0; Index < demoteCount; Index++)
            {
                GrabState state = demotionScratch[Index];
                state.applied = false;
                state.tree = null;
                state.resolvedPointIndex = -1;
                state.unresolvedSince = 0f;
                applied.Remove(state);
            }

            if (constraintCount > 0 || lastPushedCount > 0)
            {
                JigglePhysics.SetGrabConstraints(constraintScratch, constraintCount);
                lastPushedCount = constraintCount;
            }
        }

        public struct GrabGizmoSample
        {
            public ushort GrabberId;
            public ushort TargetId;
            public Vector3 BonePosition;
            public Vector3 TargetPosition;
            public float MaxStretch;
            public bool IsLocalGrab;
            public string BoneName;
        }

        private static readonly List<GrabGizmoSample> gizmoSamples = new List<GrabGizmoSample>();

        private static void AddGizmoSample(GrabState state, Vector3 bonePosition, Vector3 target, float stretchFactor)
        {
            JiggleTree tree = state.tree;
            int index = state.resolvedPointIndex;
            if (tree == null || tree.points == null || index < 0 || index >= tree.points.Length)
            {
                return;
            }
            JiggleSimulatedPoint point = tree.points[index];
            float stretchScale = Mathf.Max(point.distanceFromRoot, point.desiredLengthToParent);
            gizmoSamples.Add(new GrabGizmoSample
            {
                GrabberId = state.grabberId,
                TargetId = state.targetId,
                BonePosition = bonePosition,
                TargetPosition = target,
                MaxStretch = stretchFactor > 0f ? stretchFactor * stretchScale : 0f,
                IsLocalGrab = state.isLocalGrab,
                BoneName = tree.bones != null && index < tree.bones.Length && tree.bones[index] ? tree.bones[index].name : "?",
            });
        }

        public static IReadOnlyList<GrabGizmoSample> GizmoSamples => gizmoSamples;

        public static bool CollectGizmoSamples;

        private static void HandleUnresolved(GrabState state, float now)
        {
            if (state.unresolvedSince == 0f)
            {
                state.unresolvedSince = now;
                return;
            }
            if (now - state.unresolvedSince <= UnresolvedReleaseSeconds)
            {
                return;
            }
            if (state.isLocalGrab)
            {
                BasisNetworkHandleJiggleGrab.SendGrabStop(state.targetId, state.rigIndex, state.pointIndex);
                removalScratch.Add(state);
                return;
            }
            // Remote grab on a target we cannot resolve (LOD culled, mid swap): park it dormant
            // and let promotion pick it back up when the tree returns.
            demotionScratch.Add(state);
        }

#if UNITY_EDITOR
        // ---- Editor scene-view tester (BasisJiggleGrabTesterWindow) ----
        // Local only: these never send network events, and they carry their own resolved tree so a
        // plain scene JiggleRig works without a player, an avatar or a connection.

        private static GrabState editorGrab;
        private static JiggleRig[] editorSceneRigs;
        private static float editorSceneRigsRefreshedAt = float.NegativeInfinity;
        private const float EditorSceneRigCacheSeconds = 0.5f;

        public static bool HasEditorGrab => editorGrab != null;
        public static Vector3 EditorGrabPoint { get; private set; }
        public static Vector3 EditorGrabTarget => editorGrab != null ? editorGrab.editorTarget : Vector3.zero;
        public static int AnnouncedGrabCount => allGrabs.Count;
        public static int AppliedGrabCount => applied.Count;
        public static int PushedConstraintCount => lastPushedCount;

        public static bool TryFindEditorGrabPoint(Ray ray, float rayLength, float radius, out Vector3 point)
        {
            return TryPickAlongRay(ray, rayLength, radius, out _, out point, out _);
        }

        public static bool BeginEditorGrab(Ray ray, float rayLength, float radius)
        {
            EndEditorGrab();
            if (!TryPickAlongRay(ray, rayLength, radius, out JiggleRig rig, out Vector3 point, out int pointIndex))
            {
                return false;
            }
            JiggleTree tree = rig.GetJiggleTree();
            if (tree == null || tree.bones == null || pointIndex >= tree.bones.Length)
            {
                return false;
            }
            editorGrab = new GrabState
            {
                isEditorGrab = true,
                isLocalGrab = true,
                applied = true,
                pointIndex = (ushort)pointIndex,
                editorTarget = point,
                lastSeenTime = Time.unscaledTime,
                lastAssertTime = Time.unscaledTime,
                tree = tree,
                resolvedRootID = tree.rootID,
                resolvedPointIndex = pointIndex,
                editorMaxStretchFactor = rig.GetMaxGrabStretch(),
            };
            allGrabs.Add(editorGrab);
            applied.Add(editorGrab);
            EditorGrabPoint = point;
            return true;
        }

        public static void SetEditorGrabTarget(Vector3 worldPosition)
        {
            if (editorGrab != null)
            {
                editorGrab.editorTarget = worldPosition;
            }
        }

        public static void EndEditorGrab()
        {
            if (editorGrab == null)
            {
                return;
            }
            allGrabs.Remove(editorGrab);
            applied.Remove(editorGrab);
            editorGrab = null;
        }

        private static bool TryPickAlongRay(Ray ray, float rayLength, float radius, out JiggleRig rig, out Vector3 point, out int pointIndex)
        {
            rig = null;
            point = default;
            pointIndex = -1;
            float step = Mathf.Max(radius, 0.02f);
            int samples = Mathf.Clamp(Mathf.CeilToInt(rayLength / step), 1, 256);
            // Refreshed on a cadence: the hover preview picks every scene-view repaint, and a full
            // scene scan per repaint is felt in a populated scene.
            if (editorSceneRigs == null || Time.realtimeSinceStartup - editorSceneRigsRefreshedAt > EditorSceneRigCacheSeconds)
            {
                editorSceneRigs = Object.FindObjectsByType<JiggleRig>(FindObjectsInactive.Exclude);
                editorSceneRigsRefreshedAt = Time.realtimeSinceStartup;
            }
            JiggleRig[] sceneRigs = editorSceneRigs;
            for (int sample = 0; sample <= samples; sample++)
            {
                Vector3 position = ray.origin + ray.direction * (rayLength * sample / samples);

                JiggleRig bestRig = null;
                BasisRemotePlayer bestTarget = null;
                byte bestRigIndex = 0;
                int bestPointIndex = -1;
                Vector3 bestPointPosition = default;
                float bestScore = float.MaxValue;
                TryGetLocalPlayerId(out ushort localId);
                SearchRigs(localId, true, GrabQuery.Grasp(position, position, radius),
                    ref bestRig, ref bestTarget, ref bestRigIndex, ref bestPointIndex, ref bestPointPosition, ref bestScore);
                if (bestPointIndex >= 0)
                {
                    rig = bestRig;
                    point = bestPointPosition;
                    pointIndex = bestPointIndex;
                    return true;
                }

                int count = sceneRigs.Length;
                for (int Index = 0; Index < count; Index++)
                {
                    JiggleRig sceneRig = sceneRigs[Index];
                    if (sceneRig == null || !sceneRig.isActiveAndEnabled)
                    {
                        continue;
                    }
                    if (sceneRig.TryGetClosestGrabPoint(position, radius, out int scenePointIndex, out Vector3 scenePoint))
                    {
                        rig = sceneRig;
                        point = scenePoint;
                        pointIndex = scenePointIndex;
                        return true;
                    }
                }
            }
            return false;
        }
#endif

        private static bool IsLocalHoldHeld(GrabState state)
        {
            BasisInput input = state.localInput;
            if (input == null)
            {
                return false;
            }
            bool triggerHeld = input.CurrentInputState.Trigger >= BasisTriggerPressure.TriggerDownJiggleThreshold;
            if (input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye)
            {
                return triggerHeld;
            }
            return triggerHeld || input.CurrentInputState.GripButton;
        }
    }
}
