using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static BasisNetworkCommon;
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
        private CancellationTokenSource destroyCancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// the reason its start instead of awake is to make sure progation occurs to everything no matter the net connect
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
            if (!destroyCancellationTokenSource.IsCancellationRequested)
            {
                destroyCancellationTokenSource.Cancel();
            }

            destroyCancellationTokenSource.Dispose();

            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterHandler(NetworkID, OnNetworkMessage);
            }
            BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalPlayerJoined;
            BasisNetworkPlayer.OnOwnershipTransfer -= LowLevelOwnershipTransfer;
            BasisNetworkPlayer.OnOwnershipReleased -= LowLevelOwnershipReleased;

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
                bool wassuccesful = await TryEnsureNetworkGUIDIdentifierAsync();
                string NetworkGuidID = clientIdentifier;
                if (!wassuccesful)
                {
                    BasisDebug.LogError("Was not sucessful at TryGetNetworkGUIDIdentifier NetworkGUID");
                    return;
                }
                BasisNetworkPlayer.OnOwnershipTransfer += LowLevelOwnershipTransfer;
                BasisNetworkPlayer.OnOwnershipReleased += LowLevelOwnershipReleased;

                Task<BasisIdResolutionResult> IDResolverAsync = BasisNetworkIdResolver.ResolveAsync(NetworkGuidID);
                Task<BasisOwnershipResult> output = BasisNetworkOwnership.RequestCurrentOwnershipAsync(NetworkGuidID);
                Task[] tasks = new Task[] { IDResolverAsync, output };

                await Task.WhenAll(tasks);

                //convert GUID into Ushort for network transport.
                BasisIdResolutionResult IDResolverResult = await IDResolverAsync;
                var InitalOwnershipStatus = await output;
                if (InitalOwnershipStatus.Success)
                {
                    CurrentOwnerId = InitalOwnershipStatus.PlayerId;
                    BasisNetworkPlayers.GetPlayerById(CurrentOwnerId, out currentOwnedPlayer);
                }
                HasNetworkID = IDResolverResult.Success;
                NetworkID = IDResolverResult.Id;
                if (HasNetworkID)
                {
                    OnNetworkReady();
                    BasisNetworkGenericMessages.RegisterHandler(NetworkID, OnNetworkMessage);
                }
            }
            else
            {
                BasisDebug.LogError("LocalPlayer Is Not Connected Behaviour Cant Start");
            }
        }
        private void LowLevelOwnershipReleased(string uniqueEntityID)
        {
            if (uniqueEntityID == clientIdentifier)
            {
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
                    OnOwnershipTransfer(currentOwnedPlayer);
                }
                else
                {
                    BasisUnInitalizedPlayer UnInitalizedPlayer = new BasisUnInitalizedPlayer(CurrentOwnerId);
                    BasisDebug.LogError($"No Owner for Id {CurrentOwnerId} Creating Fake {nameof(BasisUnInitalizedPlayer)} this should only occur rarely");
                    UnInitalizedPlayer.Initialize();
                    OnOwnershipTransfer(UnInitalizedPlayer);
                }
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
        private async Task<bool> TryEnsureNetworkGUIDIdentifierAsync()
        {
            if (TryGetNetworkGUIDIdentifier(out _))
            {
                return true;
            }
            // We use as avatar scoped identifier if possible to ensure consistency across sessions and clients, as the avatar's network GUID is guaranteed to be consistent for the same avatar regardless of load order or other factors, while a hierarchy-based identifier could change if the hierarchy changes or if objects are loaded in a different order on different clients.
            BasisAvatar basisAvatar = BasisAvatar.GetGameObject(this)?.GetComponent<BasisAvatar>();
            if (basisAvatar != null)
            {
                if (!await WaitForAvatarNetworkGUIDIdentifierAsync(basisAvatar))
                {
                    BasisDebug.LogError($"Stopped waiting for avatar network identifier while building avatar-scoped network identifier for {this.gameObject.name}.", BasisDebug.LogTag.Networking);
                    return false;
                }

                if (TryBuildAvatarScopedIdentifier(basisAvatar, this, out string avatarScopedIdentifier))
                {
                    AssignNetworkGUIDIdentifier(avatarScopedIdentifier);
                    return TryGetNetworkGUIDIdentifier(out _);
                }
                else
                {
                    BasisDebug.LogError($"Failed to build avatar-scoped identifier for {this.gameObject.name}", BasisDebug.LogTag.Networking);
                    return false;
                }
            }

            AssignNetworkGUIDIdentifier(LowLevelGetHierarchyPath(this));
            return TryGetNetworkGUIDIdentifier(out _);
        }
        private async Task<bool> WaitForAvatarNetworkGUIDIdentifierAsync(BasisAvatar basisAvatar)
        {
            if (basisAvatar.TryGetNetworkGUIDIdentifier(out _))
            {
                return true;
            }

            TaskCompletionSource<bool> avatarIdentifierAssigned = new TaskCompletionSource<bool>();
            void OnClientIdentifierAssigned(string _)
            {
                avatarIdentifierAssigned.TrySetResult(true);
            }

            basisAvatar.OnClientIdentifierAssigned += OnClientIdentifierAssigned;
            using (destroyCancellationTokenSource.Token.Register(() => avatarIdentifierAssigned.TrySetResult(false)))
            {
                try
                {
                    if (basisAvatar.TryGetNetworkGUIDIdentifier(out _))
                    {
                        return true;
                    }

                    bool assigned = await avatarIdentifierAssigned.Task;
                    return assigned && basisAvatar.TryGetNetworkGUIDIdentifier(out _);
                }
                finally
                {
                    basisAvatar.OnClientIdentifierAssigned -= OnClientIdentifierAssigned;
                }
            }
        }

        private static bool TryBuildAvatarScopedIdentifier(BasisAvatar basisAvatar, BasisNetworkContentBase behaviour, out string identifier)
        {
            if (!basisAvatar.TryGetNetworkGUIDIdentifier(out string avatarIdentifier) || string.IsNullOrWhiteSpace(avatarIdentifier))
            {
                identifier = string.Empty;
                return false;
            }

            StringBuilder pathBuilder = new StringBuilder();
            pathBuilder.Append(avatarIdentifier);
            if (behaviour.transform != null && behaviour.transform != basisAvatar.transform)
            {
                pathBuilder.Append('/');
                AppendRelativeAvatarPath(pathBuilder, basisAvatar.transform, behaviour.transform);
                if (pathBuilder[pathBuilder.Length - 1] == '/')
                {
                    pathBuilder.Length--;
                }
            }
            AppendComponentIdentifier(pathBuilder, behaviour);

            identifier = pathBuilder.ToString();
            return true;
        }
        private static void AppendRelativeAvatarPath(StringBuilder pathBuilder, Transform avatarRoot, Transform current)
        {
            if (current == null || current == avatarRoot)
            {
                return;
            }

            AppendRelativeAvatarPath(pathBuilder, avatarRoot, current.parent);

            if (current == avatarRoot)
            {
                return;
            }

            pathBuilder.Append(current.name);
            pathBuilder.Append(SiblingIndexIfNeeded(current));
            pathBuilder.Append('/');
        }
        private static void AppendComponentIdentifier(StringBuilder pathBuilder, BasisNetworkContentBase behaviour)
        {
            Component[] components = behaviour.gameObject.GetComponents(behaviour.GetType());
            int index = Array.IndexOf(components, behaviour);
            pathBuilder.Append(':');
            pathBuilder.Append(behaviour.GetType().FullName);
            pathBuilder.Append('_');
            pathBuilder.Append(index);
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
        /// <param name="Timout"></param>
        /// <returns></returns>
        public async Task<BasisOwnershipResult> TakeOwnershipAsync(int Timout = 5000)
        {
            IsOwnedLocallyOnClient = true;
            CurrentOwnerId = BasisNetworkPlayer.LocalPlayer.playerId;
            currentOwnedPlayer = BasisNetworkPlayer.LocalPlayer;
            BasisOwnershipResult Result = await BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, BasisNetworkConnection.LocalPlayerPeer.RemoteId, Timout);
            return Result;
        }
        /// <summary>
        /// requests who is the owner
        /// </summary>
        /// <param name="Timout"></param>
        /// <returns></returns>
        public async Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(int Timout = 5000)
        {
            BasisOwnershipResult Result = await BasisNetworkOwnership.RequestCurrentOwnershipAsync(clientIdentifier, Timout);
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
        public virtual void OnPlayerLeft(BasisNetworkPlayer player)
        {

        }
        public virtual void OnPlayerJoined(BasisNetworkPlayer player)
        {

        }
    }
}
