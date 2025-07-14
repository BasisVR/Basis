using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Profiler;
using Basis.Scripts.TransformBinders.BoneControl;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Threading.Tasks;
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
        private readonly object _lock = new object(); // Lock object for thread-safety
        private bool _hasReasonToSendAudio;
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        [SerializeField]
        public HumanPose HumanPose = new HumanPose();
        [SerializeField]
        public HumanPoseHandler PoseHandler;
        public BasisPlayer Player;
        [SerializeField]
        public PlayerIdMessage PlayerIDMessage = new PlayerIdMessage();
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
        public ushort playerId
        {
            get
            {
                if (hasID)
                {
                    return PlayerIDMessage.playerID;
                }
                else
                {
                    BasisDebug.LogError("Missing Network ID!");
                    return 0;
                }
            }
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
                AvatarCalibrationSetup();
            }
            else
            {
                if (BasisNetworkManagement.MainThreadContext == null)
                {
                    BasisDebug.LogError("Main thread context is not set. Ensure this script is started on the main thread.");
                    return;
                }

                // Post the task to the main thread
                BasisNetworkManagement.MainThreadContext.Post(_ =>
                {
                    AvatarCalibrationSetup();
                }, null);
            }
        }
        public void AvatarCalibrationSetup()
        {
            if (CheckForAvatar())
            {
                BasisAvatar basisAvatar = Player.BasisAvatar;
                // All checks passed
                PoseHandler = new HumanPoseHandler(
                    basisAvatar.Animator.avatar,
                    Player.BasisAvatarTransform
                );
                PoseHandler.GetHumanPose(ref HumanPose);
                if (!basisAvatar.HasSendEvent)
                {
                    basisAvatar.OnNetworkMessageSend += OnNetworkMessageSend;
                    basisAvatar.OnServerReductionSystemMessageSend += OnServerReductionSystemMessageSend;
                    basisAvatar.HasSendEvent = true;
                }

                basisAvatar.LinkedPlayerID = playerId;
                basisAvatar.OnAvatarNetworkReady?.Invoke(Player.IsLocal);
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
        private void OnServerReductionSystemMessageSend(byte MessageIndex, byte[] buffer = null)
        {
            if (BasisNetworkManagement.Instance != null && BasisNetworkManagement.Transmitter != null)
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
        private void OnNetworkMessageSend(byte MessageIndex, byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Sequenced, ushort[] Recipients = null)
        {
            // Handle cases based on presence of Recipients and buffer
            AvatarDataMessage AvatarDataMessage = new AvatarDataMessage
            {
                messageIndex = MessageIndex,
                payload = buffer,
                recipients = Recipients,
                PlayerIdMessage = new PlayerIdMessage() { playerID = playerId },
            };
            NetDataWriter netDataWriter = new NetDataWriter();
            if (DeliveryMethod == DeliveryMethod.Unreliable)
            {
                netDataWriter.Put(BasisNetworkCommons.AvatarChannel);
                AvatarDataMessage.Serialize(netDataWriter);
                BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.FallChannel, DeliveryMethod);
            }
            else
            {
                AvatarDataMessage.Serialize(netDataWriter);
                BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.AvatarChannel, DeliveryMethod);
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarDataMessage, netDataWriter.Length);
        }
        public static bool AvatarToPlayer(BasisAvatar Avatar, out BasisPlayer BasisPlayer)
        {
            return BasisNetworkManagement.AvatarToPlayer(Avatar, out BasisPlayer);
        }
        public static bool PlayerToNetworkedPlayer(BasisPlayer BasisPlayer, out BasisNetworkPlayer BasisNetworkPlayer)
        {
            return BasisNetworkManagement.PlayerToNetworkedPlayer(BasisPlayer, out BasisNetworkPlayer);
        }
        public static BasisNetworkPlayer LocalPlayer => BasisNetworkManagement.Transmitter as BasisNetworkPlayer;
        public static bool GetPlayerById(ushort allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer)
        {
            return BasisNetworkManagement.GetPlayerById(allowedPlayer, out BasisNetworkPlayer);
        }
        public static BasisNetworkPlayer GetPlayerById(ushort allowedPlayer)
        {
            BasisNetworkManagement.GetPlayerById(allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer);
            return BasisNetworkPlayer;
        }
        public static bool GetPlayerById(int allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer)
        {
            return BasisNetworkManagement.GetPlayerById((ushort)allowedPlayer, out BasisNetworkPlayer);
        }
        public static BasisNetworkPlayer GetPlayerById(int allowedPlayer)
        {
            BasisNetworkManagement.GetPlayerById((ushort)allowedPlayer, out BasisNetworkPlayer BasisNetworkPlayer);
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
          return  await BasisNetworkPlayer.LocalPlayer.IsOwner(IOwnThis);
        }

        public static async Task<BasisOwnershipResult> SetOwnerAsync(BasisNetworkPlayer FutureOwner, string IOwnThis)
        {
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
                if (BasisNetworkManagement.GetPlayerById(Current.PlayerId, out BasisNetworkPlayer Player))
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
                return BasisLocalAvatarDriver.References.GetBoneLocalPositionRotation(bone, out position, out rotation);
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
                var MovementLock = BasisLocks.GetContext(BasisLocks.Movement);
                var CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);

                if (Immobilize)
                {
                    MovementLock.Add(nameof(BasisNetworkPlayer));
                    CrouchingLock.Add(nameof(BasisNetworkPlayer));
                }
                else
                {
                    MovementLock.Remove(nameof(BasisNetworkPlayer));
                    CrouchingLock.Remove(nameof(BasisNetworkPlayer));
                }
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetTrackingData", BasisDebug.LogTag.Networking);
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
                BasisLocalAvatarDriver.References.GetBonePosition(bone, out Vector3 position);
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
                BasisLocalAvatarDriver.References.GetBoneRotation(bone, out Quaternion rotation);
                return rotation;
            }
            else
            {
                BasisDebug.LogError("Not Implemented Remote GetBonePosition", BasisDebug.LogTag.Networking);
                return Quaternion.identity;
            }
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
                if (Player != null)
                {
                    return Player.DisplayName;
                }
                else { return string.Empty; }
            }
        }
    }
}
