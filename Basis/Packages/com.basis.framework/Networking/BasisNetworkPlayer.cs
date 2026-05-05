using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Behaviour;
using Basis.Scripts.Networking.Behaviour;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using static BasisNetworkGenericMessages;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// the goal of this script is to be the glue of consistent data between remote and local
    /// </summary>
    [System.Serializable]
    public abstract class BasisNetworkPlayer
    {
        /// <summary>
        /// only changes when additional avatar data is in play!
        /// </summary>
        public byte LastLinkedAvatarIndex = 0;
        private readonly object _lock = new object(); // Lock object for thread-safety
        private bool _hasReasonToSendAudio;
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        public const int MuscleCount = 95;
        // Backing field for Player, exposed internally so hot loops in this
        // assembly (gaze loop, SimulateNetworkApply, AvatarLoadComplete, etc.)
        // can read it as a plain ldfld instead of paying for a property getter
        // call that Mono in the editor doesn't reliably inline at scale.
        //
        // The Player property below is preserved so existing compiled scripts
        // (e.g. Cilbox-sandboxed avatars whose IL emits `callvirt get_Player()`)
        // keep working. AggressiveInlining lets the JIT collapse the property to
        // a single ldfld in retail builds.
        //
        // [NonSerialized] preserves the auto-property's "Unity won't serialize this"
        // behavior — _player is always assigned at runtime by the player factory.
        //
        // Invariant: non-null for any BasisNetworkPlayer that is currently in
        // BasisNetworkPlayers.Players (i.e. published into the dictionary or in
        // ReceiversSnapshot). The factory writes Player before publishing the
        // entry, and the entry is removed before Player is cleared on teardown.
        // Hot loops iterating ReceiversSnapshot can read Player.X directly without
        // a null check; only initialization, UI, or async-lookup paths that may
        // race a despawn need <see cref="TryGetPlayer"/> (the safe accessor).
        [System.NonSerialized] internal BasisPlayer _player;

        public BasisPlayer Player
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _player;
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            set => _player = value;
        }

        /// <summary>
        /// Safe accessor for callers that may run before the receiver is published
        /// or after it has started teardown — UI, async ownership lookups, etc.
        /// Hot per-frame paths should read <see cref="Player"/> directly.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool TryGetPlayer(out BasisPlayer player)
        {
            player = _player;
            return player != null;
        }
        public bool hasID = false;
        public bool HasReasonToSendAudio
        {
            get
            {
                lock (_lock)
                {
                    return _hasReasonToSendAudio;
                }
            }
            set
            {
                lock (_lock)
                {
                    _hasReasonToSendAudio = value;
                }
            }
        }
        // Plain field, not an auto-property: this is read thousands of times per
        // frame in hot loops (SimulateNetworkApply, SetSkipMuscles, nameplate
        // gather, etc.). Auto-property compiles to a get_playerId() method call
        // that Mono in the editor does not inline, showing up in the profiler as
        // a measurable per-call cost at 1k+ remotes. Subclasses still assign it
        // directly (Receiver/Transmitter/UnInitalized constructors).
        public ushort playerId;
        /// <summary>
        /// Local realtime (Time.realtimeSinceStartup) at which this player was constructed.
        /// Used to sort the players UI by arrival order and to display "joined Xs ago".
        /// Captured here because the server doesn't send a join timestamp.
        /// </summary>
        public float JoinTime = Time.realtimeSinceStartup;
        public Dictionary<byte, ServerAvatarDataMessageQueue> NextMessages = new Dictionary<byte, ServerAvatarDataMessageQueue>();
        public struct ServerAvatarDataMessageQueue
        {
            public ServerAvatarDataMessage ServerAvatarDataMessage;
            public DeliveryMethod Method;
        }
        public abstract void Initialize();
        public abstract void DeInitialize();
        public void OnAvatarCalibrationLocal()
        {
            OnAvatarCalibration();
        }
        public void OnAvatarCalibrationRemote()
        {
            OnAvatarCalibration();
        }
        public void OnAvatarCalibration()
        {
            if (BasisNetworkManagement.IsMainThread())
            {
                AvatarLoadComplete();
            }
            else
            {
                // Post the task to the main thread
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    AvatarLoadComplete();
                });
            }
        }
        public int NetworkBehaviourCount = 0;
        public BasisNetworkAvatarBehaviour[] NetworkBehaviours;
        /// <summary>
        /// Fires <see cref="BasisNetworkAvatarBehaviour.OnNetworkTerminated"/> on every behaviour
        /// from the currently tracked avatar and clears the array. Call before the avatar
        /// GameObject is destroyed (avatar swap, disconnect) so subclasses can release any
        /// network-owned state while the references are still valid.
        /// </summary>
        public void NotifyNetworkBehavioursTerminated()
        {
            if (NetworkBehaviours == null)
            {
                return;
            }
            int length = NetworkBehaviours.Length;
            for (int Index = 0; Index < length; Index++)
            {
                BasisNetworkAvatarBehaviour behaviour = NetworkBehaviours[Index];
                if (behaviour != null)
                {
                    behaviour.OnNetworkUnassign();
                }
            }
            NetworkBehaviours = null;
            NetworkBehaviourCount = 0;
        }
        public void AvatarLoadComplete()
        {
            // Avatar swap: terminate behaviours from the prior avatar before re-collecting.
            NotifyNetworkBehavioursTerminated();
            if (CheckForAvatar())
            {
                BasisAvatar basisAvatar = Player.BasisAvatar;
               // PoseHandler.GetHumanPose(ref HumanPose);
                basisAvatar.LinkedPlayerID = playerId;
                NetworkBehaviours = Player.BasisAvatar.GetComponentsInChildren<BasisNetworkAvatarBehaviour>(true);
                NetworkBehaviourCount = NetworkBehaviours.Length;
                int length = NetworkBehaviours.Length;
                if (length > 256)
                {
                    BasisDebug.LogError($"To Many Mono Behaviours on this Avatar only supports up to 256 was {length}!");
                    return;
                }
                for (byte Index = 0; Index < length; Index++)
                {
                    NetworkBehaviours[Index].OnNetworkAssign(Index, this);
                }
            }
            else
            {
                BasisDebug.LogError("Unable to proceed with Avatar Load Complete!");
            }
        }
        public bool CheckForAvatar()
        {
            if (Player == null)
            {
                BasisDebug.LogError("NetworkedPlayer.Player is null! Cannot compute HumanPose.");
                return false;
            }

            if (Player.BasisAvatar == null)
            {
                BasisDebug.LogError("BasisAvatar is null! Cannot compute HumanPose.");
                return false;
            }
            return true;
        }
        public void OnAvatarServerReductionSystemMessageSend(byte MessageIndex, byte[] buffer = null)
        {
            if (BasisNetworkManagement.Transmitter != null)
            {
                AdditionalAvatarData AAD = new AdditionalAvatarData
                {
                    array = buffer,
                    messageIndex = MessageIndex
                };
                BasisNetworkManagement.Transmitter.AddAdditional(AAD);
            }
            else
            {
                BasisDebug.LogError("Missing Transmitter or Network Management", BasisDebug.LogTag.Networking);
            }
        }
        public void OnAvatarNetworkMessageSend(byte MessageIndex, byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced, ushort[] Recipients = null)
        {
            // Handle cases based on presence of Recipients and buffer
            AvatarDataMessage AvatarDataMessage = new AvatarDataMessage
            {
                PlayerIdMessage = new PlayerIdMessage() { playerID = playerId },
                messageIndex = MessageIndex,
                payload = buffer,
                recipients = Recipients,
                AvatarLinkIndex = LastLinkedAvatarIndex,
                recipientsSize = 0,
            };
            NetDataWriter netDataWriter = new NetDataWriter();
            AvatarDataMessage.Serialize(netDataWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AvatarChannel, DeliveryMethod);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarDataMessage, netDataWriter.Length);
        }
        public static bool AvatarToPlayer(BasisAvatar Avatar, out BasisPlayer BasisPlayer)
        {
            return BasisNetworkPlayers.AvatarToPlayer(Avatar, out BasisPlayer);
        }
        public static bool PlayerToNetworkedPlayer(BasisPlayer BasisPlayer, out BasisNetworkPlayer BasisNetworkPlayer)
        {
            return BasisNetworkPlayers.PlayerToNetworkedPlayer(BasisPlayer, out BasisNetworkPlayer);
        }
        public static BasisNetworkPlayer LocalPlayer => BasisNetworkManagement.Transmitter as BasisNetworkPlayer;
        public static bool GetPlayerById(ushort allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer)
        {
            return BasisNetworkPlayers.GetPlayerById(allowedPlayer, out BasisNetworkPlayer);
        }
        public static BasisNetworkPlayer GetPlayerById(ushort allowedPlayer)
        {
            BasisNetworkPlayers.GetPlayerById(allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer);
            return BasisNetworkPlayer;
        }
        public static bool GetPlayerById(int allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer)
        {
            return BasisNetworkPlayers.GetPlayerById((ushort)allowedPlayer, out BasisNetworkPlayer);
        }
        public static BasisNetworkPlayer GetPlayerById(int allowedPlayer)
        {
            BasisNetworkPlayers.GetPlayerById((ushort)allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer);
            return BasisNetworkPlayer;
        }
        /// <summary>
        /// this is slow right now, needs improvement! - dooly
        /// might be bad!
        /// </summary>
        /// <param name="Position"></param>
        /// <param name="Rotation"></param>
        public void GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation)
        {
            Position = Player.BasisAvatar.Animator.rootPosition;
            Rotation = Player.BasisAvatar.Animator.rootRotation;
        }

        public async Task<bool> IsOwner(string IOwnThis)
        {
            if (hasID)
            {
                BasisOwnershipResult output = await BasisNetworkOwnership.RequestCurrentOwnershipAsync(IOwnThis);
                if (output.Success && output.PlayerId == playerId)
                {
                    return true;
                }
            }
            return false;
        }
        public bool IsOwnerCached(string UniqueNetworkId)
        {
            return BasisNetworkOwnership.IsOwnerLocalValidation(UniqueNetworkId);
        }
        public static async Task<bool> IsOwnerLocal(string IOwnThis)
        {
            return await BasisNetworkPlayer.LocalPlayer.IsOwner(IOwnThis);
        }

        public static async Task<BasisOwnershipResult> SetOwnerAsync(BasisNetworkPlayer FutureOwner, string IOwnThis)
        {
            if (FutureOwner == null)
            {
                BasisDebug.LogError("Missing Future Player!");
                return new(false, 0);
            }
            if (FutureOwner.hasID)
            {
                return await BasisNetworkOwnership.TakeOwnershipAsync(IOwnThis, FutureOwner.playerId);
            }
            else
            {
                return new(false, 0);
            }
        }
        public static async Task<BasisOwnershipResult> GetOwnerPlayerIDAsync(string UniqueID)
        {
            return await BasisNetworkOwnership.RequestCurrentOwnershipAsync(UniqueID);
        }
        public static async Task<(bool, BasisNetworkPlayer)> GetOwnerPlayerAsync(string UniqueID)
        {
            BasisOwnershipResult Current = await BasisNetworkOwnership.RequestCurrentOwnershipAsync(UniqueID);
            if (Current.Success)
            {
                if (BasisNetworkPlayers.GetPlayerById(Current.PlayerId, out BasisNetworkPlayer Player))
                {
                    return new(Current.Success, Player);
                }
            }
            return new(false, null);
        }

        public bool IsUserInVR()
        {
            if (Player.IsLocal)
            {
                return BasisDeviceManagement.IsUserInDesktop() == false;
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote IsUserVR", BasisDebug.LogTag.Networking);
                return false;
            }
        }
        public bool IsLocal => Player.IsLocal;
        //this is slow use a faster way! (but you can use it of course)
        public bool GetBonePositionAndRotation(HumanBodyBones bone, out Vector3 position, out Quaternion rotation)
        {
            if (Player.IsLocal)
            {
                return BasisLocalAvatarDriver.Mapping.GetBoneLocalPositionRotation(bone, out position, out rotation);
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetBonePosition", BasisDebug.LogTag.Networking);
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
        }

        public bool GetTrackingData(BasisBoneTrackedRole Role, out Vector3 position, out Quaternion rotation)
        {
            if (Player.IsLocal)
            {
                if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl Control, Role))
                {
                    position = Control.OutgoingWorldData.position;
                    rotation = Control.OutgoingWorldData.rotation;
                    return true;
                }
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetTrackingData", BasisDebug.LogTag.Networking);
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }
        public BasisCalibratedCoords GetTrackingData(BasisBoneTrackedRole Role)
        {
            if (Player.IsLocal)
            {
                if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl Control, Role))
                {
                    return Control.OutgoingWorldData;
                }
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetTrackingData", BasisDebug.LogTag.Networking);
            }
            return new BasisCalibratedCoords();
        }
        public bool GetTrackingData(BasisBoneTrackedRole Role, out BasisCalibratedCoords outgoing)
        {
            if (Player.IsLocal)
            {
                if (BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl Control, Role))
                {
                    outgoing = Control.OutgoingWorldData;
                    return true;
                }
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetTrackingData", BasisDebug.LogTag.Networking);
            }
            outgoing = new BasisCalibratedCoords();
            return false;
        }
        /// <summary>
        /// Duration only works on steamvr.
        /// Todo: add openxr duration manual tracking,
        /// </summary>
        /// <param name="Role"></param>
        /// <param name="duration"></param>
        /// <param name="amplitude"></param>
        /// <param name="frequency"></param>
        /// <exception cref="NotImplementedException"></exception>
        public void PlayHaptic(BasisBoneTrackedRole Role, float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f)
        {
            if (BasisDeviceManagement.Instance.FindDevice(out BasisInput Input, Role))
            {
                Input.PlayHaptic(duration, amplitude, frequency);
            }
            else
            {
                BasisDebug.LogError("Missing Haptic Input For Device Type " + Role);
            }
        }

        public void Immobilize(bool Immobilize)
        {
            if (Player.IsLocal)
            {
                if (BasisLocalPlayer.Instance != null)
                {
                    BasisLocalPlayer.Instance.Immobilize(Immobilize);
                }
                else
                {
                    BasisDebug.LogError("Not Implemented Remote GetTrackingData", BasisDebug.LogTag.Networking);
                }
            }
        }

        public Vector3 GetPosition()
        {
            return Player.BasisAvatar.Animator.rootPosition;
        }

        public Vector3 GetBonePosition(HumanBodyBones bone)
        {
            if (Player.IsLocal)
            {
                BasisLocalAvatarDriver.Mapping.GetBonePosition(bone, out Vector3 position);
                return position;
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetBonePosition", BasisDebug.LogTag.Networking);
                return new Vector3();
            }
        }
        public Quaternion GetBoneRotation(HumanBodyBones bone)
        {
            if (Player.IsLocal)
            {
                BasisLocalAvatarDriver.Mapping.GetBoneRotation(bone, out Quaternion rotation);
                return rotation;
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetBonePosition", BasisDebug.LogTag.Networking);
                return Quaternion.identity;
            }
        }
        // Plain fields — read once per receiver per frame in
        // BasisNetworkManagement.SimulateNetworkApply (and SetFilteredHipsOverride).
        // Same reasoning as playerId: auto-property getters were showing up in
        // the profiler at scale because Mono editor builds don't inline them.
        // Writes are still funneled through OverridenDestinationOfRoot /
        // ProvidedDestinationOfRoot, so the "private set" intent is preserved
        // by convention even though the field is technically writable.
        public bool HasOverridenDestination = false;
        public float3 OverridenPosition = float3.zero;
        public Quaternion OverridenRotation = Quaternion.identity;
        public void OverridenDestinationOfRoot(bool hasOverridenDestination)
        {
            if (Player.IsLocal)
            {
                BasisDebug.LogError("cant set root for localplayer use  BasisLocalPlayer.Instance.LocalRigDriver.SetOverrideUsage(HumanBodyBones.Hips, enabled);", BasisDebug.LogTag.Networking);
            }
            else
            {
                HasOverridenDestination = hasOverridenDestination;
            }
        }
        public void ProvidedDestinationOfRoot(float3 Position,Quaternion Rotation)
        {
            if (Player.IsLocal)
            {
                BasisDebug.LogError("cant set root for localplayer use BasisLocalPlayer.Instance.LocalRigDriver.SetOverrideData(Overidenbone, Position, Rotation);", BasisDebug.LogTag.Networking);
            }
            else
            {
                OverridenPosition = Position;
                OverridenRotation = Rotation;
            }
        }

        public static BasisNetworkPlayer[] GetAllPlayers()
        {
            return BasisNetworkPlayers.Players.Values.ToArray();
        }

        public static int GetPlayerCount()
        {
            return BasisNetworkPlayers.Players.Count;
        }
        public static bool PlayerToName(string name, out BasisNetworkPlayer NetworkPlayer)
        {
            foreach (var player in BasisNetworkPlayers.Players.Values)
            {
                if (player != null)
                {
                    if (player.displayName == name)
                    {
                        NetworkPlayer = player;
                        return true;
                    }
                }
            }
            NetworkPlayer = null;
            return false;
        }
        /// <summary>
        /// this occurs after the localplayer has been approved by the network and setup
        /// </summary>
        public static Action<BasisNetworkPlayer, BasisLocalPlayer> OnLocalPlayerJoined;
        public static OnNetworkMessageReceiveOwnershipTransfer OnOwnershipTransfer;
        public static OnNetworkMessageReceiveOwnershipRemoved OnOwnershipReleased;
        /// <summary>
        /// this occurs after a player has been removed.
        /// </summary>
        public static Action<BasisNetworkPlayer> OnPlayerLeft;
        /// <summary>
        /// this occurs after a local or remote user has been authenticated and joined & spawned
        /// </summary>
        public static Action<BasisNetworkPlayer> OnPlayerJoined;
        /// <summary>
        /// this occurs after a remote user has been authenticated and joined & spawned
        /// </summary>
        public static Action<BasisNetworkPlayer, BasisRemotePlayer> OnRemotePlayerJoined;
        /// <summary>
        /// this occurs after the localplayer has removed
        /// </summary>
        public static Action<BasisNetworkPlayer, BasisLocalPlayer> OnLocalPlayerLeft;
        /// <summary>
        /// this occurs after a remote user has removed
        /// </summary>
        public static Action<BasisNetworkPlayer, BasisRemotePlayer> OnRemotePlayerLeft;

        public string displayName
        {
            get
            {
                var p = _player;
                return p != null ? p.DisplayName : string.Empty;
            }
        }

        public string SafeDisplayName
        {
            get
            {
                var p = _player;
                return p != null ? p.SafeDisplayName : string.Empty;
            }
        }
    }
}
