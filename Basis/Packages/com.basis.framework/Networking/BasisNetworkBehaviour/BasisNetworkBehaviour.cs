using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static BasisNetworkCommon;
using Basis.Scripts.BasisSdk;
namespace Basis
{
    public abstract class BasisNetworkBehaviour : BasisNetworkContentBase
    {
        public bool HasNetworkID = false;
        private ushort networkID;
        public ushort NetworkID
        {
            get => networkID;
            private set => networkID = value;
        }
        /// <summary>
        /// only set true when the server approves our ownership
        /// </summary>
        public bool IsOwnedLocallyOnServer = false;
        /// <summary>
        /// this is instantly set when we request ownership.
        /// </summary>
        public bool IsOwnedLocallyOnClient = false;
        public ushort CurrentOwnerId;
        public BasisNetworkPlayer currentOwnedPlayer;
        private bool ownerResolutionPending;

        /// <summary>
        /// the reason its start instead of awake is to make sure propagation occurs to everything no matter the net connect
        /// </summary>
        public virtual void Start()
        {
            BasisNetworkPlayer.OnLocalPlayerJoined += OnLocalPlayerJoined;
            BasisNetworkPlayer.OnPlayerJoined += OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft += OnPlayerLeft;
            if (BasisNetworkConnection.LocalPlayerIsConnected == false)
            {
            }
            else
            {
                OnLocalPlayerJoined(null, null);
            }
        }
        public virtual void OnDestroy()
        {
            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterHandler(NetworkID);
                BasisNetworkGenericMessages.UnregisterDirectHandler(NetworkID);
            }
            BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalPlayerJoined;
            BasisNetworkPlayer.OnOwnershipTransfer -= LowLevelOwnershipTransfer;
            BasisNetworkPlayer.OnOwnershipReleased -= LowLevelOwnershipReleased;
            BasisNetworkPlayer.OnPlayerJoined -= LowLevelResolvePendingOwner;

            BasisNetworkPlayer.OnPlayerJoined -= OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft -= OnPlayerLeft;
        }
        public bool IsLocalOwner()
        {
            if (HasNetworkID)
            {
                return IsOwnedLocallyOnServer;
            }
            else
            {
                return false;
            }
        }
        private async void OnLocalPlayerJoined(BasisNetworkPlayer NetworkedPlayer, BasisLocalPlayer LocalPlayer)
        {
            if (BasisNetworkConnection.LocalPlayerIsConnected)
            {
                bool wassuccesful = TryGetIdentifier(out var ContentInformation);
                if (wassuccesful == false)//this will happen to anything that has not got a GUID from the server
                {
                    //so if we dont get a GUID from the server lets make one!
                    string FileNamePath = LowLevelGetHierarchyPath(this);

                    this.transform.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);
                    Vector3 Scale = this.transform.localScale;

                    byte Type = 0;
                    if (this.GetType() != typeof(BasisScene))
                    {
                        Type = 1;
                    }
                    BasisContentInformation Content;
                    if (LocalPlayer == null)
                    {
                        Content = new BasisContentInformation
                        {
                            LoadedNetID = FileNamePath,
                            UUIDOfCreator = string.Empty,
                            IsAdminLocked = false,
                            LoadStrategy = 0,
                            PositionX = Position.x,
                            PositionY = Position.y,
                            PositionZ = Position.z,
                            QuaternionW = Rotation.w,
                            QuaternionX = Rotation.x,
                            QuaternionY = Rotation.y,
                            QuaternionZ = Rotation.z,
                            ModifyScale = true,
                            ScaleX = Scale.x,
                            ScaleY = Scale.y,
                            ScaleZ = Scale.z,
                            Mode = Type,
                            Persist = true,
                            Static = false,
                            StaticAdminLocked = false,

                        };
                    }
                    else
                    {
                        Content = new BasisContentInformation
                        {
                            LoadedNetID = FileNamePath,
                            UUIDOfCreator = LocalPlayer.UUID,
                            IsAdminLocked = false,
                            LoadStrategy = 0,
                            PositionX = Position.x,
                            PositionY = Position.y,
                            PositionZ = Position.z,
                            QuaternionW = Rotation.w,
                            QuaternionX = Rotation.x,
                            QuaternionY = Rotation.y,
                            QuaternionZ = Rotation.z,
                            ModifyScale = true,
                            ScaleX = Scale.x,
                            ScaleY = Scale.y,
                            ScaleZ = Scale.z,
                            Mode = Type,
                            Persist = true,
                            Static = false,
                            StaticAdminLocked = false,

                        };
                    }
                    //FileNamePath
                    AssignContentIdentifier(Content);

                    wassuccesful = TryGetIdentifier(out ContentInformation);
                }
                if (!wassuccesful)
                {
                    BasisDebug.LogError("Was not successful at TryGetNetworkGUIDIdentifier NetworkGUID");
                    return;
                }
                // A behaviour that outlives a server switch - anything under DontDestroyOnLoad, or a reconnect
                // with no scene reload - runs this a second time to re-resolve its id against the new server,
                // so remove before adding or the handlers stack one copy per connection.
                BasisNetworkPlayer.OnOwnershipTransfer -= LowLevelOwnershipTransfer;
                BasisNetworkPlayer.OnOwnershipTransfer += LowLevelOwnershipTransfer;
                BasisNetworkPlayer.OnOwnershipReleased -= LowLevelOwnershipReleased;
                BasisNetworkPlayer.OnOwnershipReleased += LowLevelOwnershipReleased;
                BasisNetworkPlayer.OnPlayerJoined -= LowLevelResolvePendingOwner;
                BasisNetworkPlayer.OnPlayerJoined += LowLevelResolvePendingOwner;

                Task<BasisIdResolutionResult> IDResolverAsync = BasisNetworkIdResolver.ResolveAsync(ContentInformation.LoadedNetID);
                Task<BasisOwnershipResult> output = BasisNetworkOwnership.RequestCurrentOwnershipAsync(ContentInformation.LoadedNetID);
                Task[] tasks = new Task[] { IDResolverAsync, output };

                await Task.WhenAll(tasks);

                //convert GUID into Ushort for network transport.
                BasisIdResolutionResult IDResolverResult = await IDResolverAsync;
                var InitialOwnershipStatus = await output;
                if (InitialOwnershipStatus.Success)
                {
                    CurrentOwnerId = InitialOwnershipStatus.PlayerId;
                    BasisNetworkPlayers.GetPlayerById(CurrentOwnerId, out currentOwnedPlayer);
                }
                HasNetworkID = IDResolverResult.Success;
                NetworkID = IDResolverResult.Id;
                if (HasNetworkID)
                {
                    OnNetworkReady();
                    BasisNetworkGenericMessages.RegisterHandler(NetworkID, OnNetworkMessage);
                    BasisNetworkGenericMessages.RegisterDirectHandler(NetworkID, OnDirectNetworkMessage);
                }
            }
            else
            {
                BasisDebug.LogError("LocalPlayer Is Not Connected Behaviour Can't Start");
            }
        }
        private void LowLevelOwnershipReleased(string uniqueEntityID)
        {
            if (uniqueEntityID == clientIdentifier)
            {
                ownerResolutionPending = false;
                OnServerOwnershipDestroyed();
            }
        }
        private void LowLevelOwnershipTransfer(string uniqueEntityID, ushort NetIdNewOwner, bool isOwner)
        {

            if (uniqueEntityID == clientIdentifier)
            {
                IsOwnedLocallyOnServer = isOwner;
                IsOwnedLocallyOnClient = isOwner;
                CurrentOwnerId = NetIdNewOwner;
                if (BasisNetworkPlayers.GetPlayerById(CurrentOwnerId, out currentOwnedPlayer))
                {
                    ownerResolutionPending = false;
                    OnOwnershipTransfer(currentOwnedPlayer);
                }
                else
                {
                    ownerResolutionPending = true;
                    BasisUnInitializedPlayer UnInitializedPlayer = new BasisUnInitializedPlayer(CurrentOwnerId);
                    UnInitializedPlayer.Initialize();
                    currentOwnedPlayer = UnInitializedPlayer;
                    OnOwnershipTransfer(UnInitializedPlayer);
                }
            }
        }
        private void LowLevelResolvePendingOwner(BasisNetworkPlayer joinedPlayer)
        {
            if (ownerResolutionPending == false)
            {
                return;
            }
            if (joinedPlayer == null || joinedPlayer.playerId != CurrentOwnerId)
            {
                return;
            }
            if (BasisNetworkPlayers.GetPlayerById(CurrentOwnerId, out currentOwnedPlayer))
            {
                ownerResolutionPending = false;
                OnOwnershipTransfer(currentOwnedPlayer);
            }
        }
        /// <summary>
        /// this is used for sending Network Messages
        /// very much a data sync that can be used more like a traditional sync method
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self, you can include yourself to make it loop back over the network</param>
        public void SendCustomNetworkEvent(byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null)
        {
            if (HasNetworkID)
            {
               // BasisDebug.Log("Sening Out Custom Network Event");
                BasisNetworkGenericMessages.OnNetworkMessageSend(NetworkID, buffer, DeliveryMethod, Recipients);
            }
            else
            {
                BasisDebug.LogError($"No Network ID Assigned yet for {this.gameObject.name}", BasisDebug.LogTag.Networking);
            }
        }
        /// <summary>
        /// Sends a Network Message over direct peer-to-peer links, falling back to the server
        /// relay for recipients with no direct connection. Received via <see cref="OnDirectNetworkMessage"/>.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="DeliveryMethod"></param>
        /// <param name="Recipients">if null everyone but self</param>
        /// <param name="allowServerFallback">if false, only delivers to directly-connected recipients (best-effort, no server relay)</param>
        public void SendCustomNetworkEventDirect(byte[] buffer = null, DeliveryMethod DeliveryMethod = DeliveryMethod.Unreliable, ushort[] Recipients = null, bool allowServerFallback = true)
        {
            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.OnNetworkMessageSendDirect(NetworkID, buffer, DeliveryMethod, Recipients, allowServerFallback);
            }
            else
            {
                BasisDebug.LogError($"No Network ID Assigned yet for {this.gameObject.name}", BasisDebug.LogTag.Networking);
            }
        }
        public void SendCustomEventDelayedSeconds(Action callback, float delaySeconds, EventTiming timing = EventTiming.Update)
        {
            StartCoroutine(InvokeActionAfterSeconds(callback, delaySeconds, timing));
        }
        public void SendCustomEventDelayedFrames(Action callback, int delayFrames, EventTiming timing = EventTiming.Update)
        {
            StartCoroutine(InvokeActionAfterFrames(callback, delayFrames, timing));
        }
        private IEnumerator InvokeActionAfterSeconds(Action callback, float delaySeconds, EventTiming timing)
        {
            switch (timing)
            {
                case EventTiming.FixedUpdate:
                    yield return WaitForFixedUpdateSeconds(delaySeconds);
                    break;
                case EventTiming.LateUpdate:
                    yield return WaitForLateUpdateSeconds(delaySeconds);
                    break;
                default:
                    yield return new WaitForSeconds(delaySeconds);
                    break;
            }

            callback?.Invoke();
        }
        private IEnumerator InvokeActionAfterFrames(Action callback, int delayFrames, EventTiming timing)
        {
            for (int Index = 0; Index < delayFrames; Index++)
            {
                switch (timing)
                {
                    case EventTiming.FixedUpdate:
                        yield return new WaitForFixedUpdate();
                        break;
                    case EventTiming.LateUpdate:
                        yield return new WaitForEndOfFrame();
                        break;
                    default:
                        yield return null;
                        break;
                }
            }

            callback?.Invoke();
        }
        private IEnumerator WaitForFixedUpdateSeconds(float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
            }
        }
        private IEnumerator WaitForLateUpdateSeconds(float seconds)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                yield return new WaitForEndOfFrame();
                elapsed += Time.deltaTime;
            }
        }
        public static string LowLevelGetHierarchyPath(BasisNetworkContentBase obj)
        {
            StringBuilder pathBuilder = new StringBuilder();
            // Get the index of the component on the GameObject
            Component[] components = obj.gameObject.GetComponents(obj.GetType());
            int index = System.Array.IndexOf(components, obj);

            pathBuilder.Append($"{obj.gameObject.name}{SiblingIndexIfNeeded(obj.transform)}:{obj.GetType().FullName}_{index}");
            Transform current = obj.transform.parent;

            while (current != null)
            {
                pathBuilder.Insert(0, $"{current.name}{SiblingIndexIfNeeded(current)}/");
                current = current.parent;
            }

            return pathBuilder.ToString();
        }
        private static string SiblingIndexIfNeeded(Transform t)
        {
            Transform parent = t.parent;
            string name = t.name;
            if (parent == null)
            {
                foreach (var go in t.gameObject.scene.GetRootGameObjects())
                {
                    if (go != t.gameObject && go.name == name)
                    {
                        return $"[{t.GetSiblingIndex()}]";
                    }
                }
            }
            else
            {
                int childCount = parent.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling != t && sibling.name == name)
                    {
                        return $"[{t.GetSiblingIndex()}]";
                    }
                }
            }
            return string.Empty;
        }
        public async void TakeOwnership()
        {
            //no need to use await ownership will get back here from lower level.
            await TakeOwnershipAsync();
        }
        /// <summary>
        /// actively takes ownership from another player
        /// </summary>
        /// <param name="Timeout"></param>
        /// <returns></returns>
        public async Task<BasisOwnershipResult> TakeOwnershipAsync(int Timeout = 5000)
        {
            IsOwnedLocallyOnClient = true;
            BasisNetworkPlayer LocalPlayer = BasisNetworkPlayer.LocalPlayer;

            if (!HasNetworkID)
            {
                CurrentOwnerId = LocalPlayer != null ? LocalPlayer.playerId : (ushort)0;
                currentOwnedPlayer = LocalPlayer;
                return new BasisOwnershipResult(true, CurrentOwnerId);
            }

            if (LocalPlayer == null || !BasisNetworkConnection.TryGetLocalPlayerID(out ushort LocalId))
            {
                return BasisOwnershipResult.Failed;
            }

            CurrentOwnerId = LocalPlayer.playerId;
            currentOwnedPlayer = LocalPlayer;
            BasisOwnershipResult Result = await BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, LocalId, Timeout);
            return Result;
        }
        /// <summary>
        /// requests who is the owner
        /// </summary>
        /// <param name="Timeout"></param>
        /// <returns></returns>
        public async Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(int Timeout = 5000)
        {
            BasisOwnershipResult Result = await BasisNetworkOwnership.RequestCurrentOwnershipAsync(clientIdentifier, Timeout);
            return Result;
        }
        public virtual void OnNetworkReady()
        {

        }
        /// <summary>
        /// back to no one owning it, (item no longer exists for example)
        /// </summary>
        public virtual void OnServerOwnershipDestroyed()
        {

        }
        public virtual void OnOwnershipTransfer(BasisNetworkPlayer NetIdNewOwner)
        {

        }
        public virtual void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {

        }
        public virtual void OnDirectNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {

        }
        public virtual void OnPlayerLeft(BasisNetworkPlayer player)
        {

        }
        public virtual void OnPlayerJoined(BasisNetworkPlayer player)
        {

        }
    }
}
