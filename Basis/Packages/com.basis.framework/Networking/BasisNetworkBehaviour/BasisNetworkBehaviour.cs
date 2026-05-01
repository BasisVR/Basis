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
using System.Runtime.CompilerServices;
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
        private bool isDestroyed;
        private bool ownershipEventsRegistered;

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
            isDestroyed = true;

            if (destroyCancellationTokenSource != null && !destroyCancellationTokenSource.IsCancellationRequested)
            {
                destroyCancellationTokenSource.Cancel();
            }

            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterHandler(NetworkID, OnNetworkMessage);
                HasNetworkID = false;
            }
            UnregisterOwnershipEvents();

            destroyCancellationTokenSource?.Dispose();

            BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalPlayerJoined;

            BasisNetworkPlayer.OnPlayerJoined -= OnPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft -= OnPlayerLeft;
        }
        public bool IsLocalOwner()
        {
            if (HasNetworkID && !IsTornDown())
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
            if (!TryGetDestroyCancellationToken(out CancellationToken destroyToken))
            {
                return;
            }

            try
            {
                if (!BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    BasisDebug.LogError("LocalPlayer Is Not Connected Behaviour Cant Start");
                    return;
                }
                BasisAvatar localPlayerAvatar = LocalPlayer?.BasisAvatar;
                bool wassuccesful = await TryEnsureNetworkGUIDIdentifierAsync(destroyToken, localPlayerAvatar);
                ThrowIfTornDown(destroyToken);
                string NetworkGuidID = clientIdentifier;
                if (!wassuccesful)
                {
                    BasisDebug.LogError("Was not sucessful at TryGetNetworkGUIDIdentifier NetworkGUID");
                    return;
                }
                Task<BasisIdResolutionResult> IDResolverAsync = BasisNetworkIdResolver.ResolveAsync(NetworkGuidID);
                Task<BasisOwnershipResult> output = BasisNetworkOwnership.RequestCurrentOwnershipAsync(NetworkGuidID);
                Task[] tasks = new Task[] { IDResolverAsync, output };

                await Task.WhenAll(tasks);
                ThrowIfTornDown(destroyToken);

                //convert GUID into Ushort for network transport.
                BasisIdResolutionResult IDResolverResult = IDResolverAsync.Result;
                BasisOwnershipResult InitalOwnershipStatus = output.Result;
                if (InitalOwnershipStatus.Success)
                {
                    CurrentOwnerId = InitalOwnershipStatus.PlayerId;
                    BasisNetworkPlayers.GetPlayerById(CurrentOwnerId, out currentOwnedPlayer);
                }
                else
                {
                    CurrentOwnerId = 0;
                    currentOwnedPlayer = null;
                }

                if (IDResolverResult.Success)
                {
                    NetworkID = IDResolverResult.Id;
                    HasNetworkID = true;
                    RegisterOwnershipEvents();
                    OnNetworkReady();
                    ThrowIfTornDown(destroyToken);
                    BasisNetworkGenericMessages.RegisterHandler(NetworkID, OnNetworkMessage);
                }
                else
                {
                    ResetNetworkInitializationState();
                }
            }
            catch (OperationCanceledException)
            {
                CleanupNetworkInitialization();
            }
            catch (Exception exception)
            {
                CleanupNetworkInitialization();
                if (!IsTornDown())
                {
                    BasisDebug.LogError($"Network behaviour initialization failed: {exception}", BasisDebug.LogTag.Networking);
                }
            }
        }
        private void LowLevelOwnershipReleased(string uniqueEntityID)
        {
            if (IsTornDown())
            {
                return;
            }

            if (uniqueEntityID == clientIdentifier)
            {
                OnServerOwnershipDestroyed();
            }
        }
        private void LowLevelOwnershipTransfer(string uniqueEntityID, ushort NetIdNewOwner, bool isOwner)
        {
            if (IsTornDown())
            {
                return;
            }

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
            if (HasNetworkID && !IsTornDown())
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
        private async Task<bool> TryEnsureNetworkGUIDIdentifierAsync(CancellationToken cancellationToken, BasisAvatar localPlayer = null)
        {
            if (TryGetNetworkGUIDIdentifier(out _))
            {
                return true;
            }
            if (IsUnderLocalPlayerAvatar(localPlayer))
            {
                if (!await WaitForAvatarNetworkGUIDIdentifierAsync(localPlayer, cancellationToken))
                {
                    BasisDebug.LogError($"Stopped waiting for avatar network identifier while building avatar-scoped network identifier for {this.gameObject.name}.", BasisDebug.LogTag.Networking);
                    return false;
                }
                ThrowIfTornDown(cancellationToken);

                if (TryBuildAvatarScopedIdentifier(localPlayer, this, out string avatarScopedIdentifier))
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
        private bool IsUnderLocalPlayerAvatar(BasisAvatar basisAvatar) => basisAvatar != null && (basisAvatar.gameObject == this.gameObject|| basisAvatar.transform.IsChildOf(this.transform));
        private async Task<bool> WaitForAvatarNetworkGUIDIdentifierAsync(BasisAvatar basisAvatar, CancellationToken cancellationToken)
        {
            if (basisAvatar.TryGetNetworkGUIDIdentifier(out _))
            {
                return true;
            }

            TaskCompletionSource<bool> avatarIdentifierAssigned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClientIdentifierAssigned(string _)
            {
                avatarIdentifierAssigned.TrySetResult(true);
            }

            basisAvatar.OnClientIdentifierAssigned += OnClientIdentifierAssigned;
            using (cancellationToken.Register(() => avatarIdentifierAssigned.TrySetCanceled(cancellationToken)))
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

            pathBuilder.Append(current.name).
            Append(SiblingIndexIfNeeded(current)).
            Append('/');
        }
        private static void AppendComponentIdentifier(StringBuilder pathBuilder, BasisNetworkContentBase behaviour)
        {
            Component[] components = behaviour.gameObject.GetComponents(behaviour.GetType());
            int index = Array.IndexOf(components, behaviour);
            pathBuilder.Append(':').
            Append(behaviour.GetType().FullName).
            Append('_').
            Append(index);
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
            try
            {
                await TakeOwnershipAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
        /// <summary>
        /// actively takes ownership from another player
        /// </summary>
        /// <param name="Timout"></param>
        /// <returns></returns>
        public async Task<BasisOwnershipResult> TakeOwnershipAsync(int Timout = 5000, CancellationToken cancellationToken = default)
        {
            if (!TryGetDestroyCancellationToken(out CancellationToken destroyToken))
            {
                throw new OperationCanceledException();
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfTornDown(destroyToken);
            IsOwnedLocallyOnClient = true;
            CurrentOwnerId = BasisNetworkPlayer.LocalPlayer.playerId;
            currentOwnedPlayer = BasisNetworkPlayer.LocalPlayer;
            BasisOwnershipResult Result = await BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, BasisNetworkConnection.LocalPlayerPeer.RemoteId, Timout);
            ThrowIfTornDown(destroyToken);
            return Result;
        }
        /// <summary>
        /// requests who is the owner
        /// </summary>
        /// <param name="Timout"></param>
        /// <returns></returns>
        public async Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(int Timout = 5000, CancellationToken cancellationToken = default)
        {
            if (!TryGetDestroyCancellationToken(out CancellationToken destroyToken))
            {
                throw new OperationCanceledException();
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfTornDown(destroyToken);
            BasisOwnershipResult Result = await BasisNetworkOwnership.RequestCurrentOwnershipAsync(clientIdentifier, Timout);
            ThrowIfTornDown(destroyToken);
            return Result;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsTornDown(CancellationToken cancellationToken = default)
        {
            return isDestroyed || cancellationToken.IsCancellationRequested || this == null;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfTornDown(CancellationToken cancellationToken)
        {
            if (IsTornDown(cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        private bool TryGetDestroyCancellationToken(out CancellationToken cancellationToken)
        {
            cancellationToken = default;
            if (isDestroyed || destroyCancellationTokenSource == null)
            {
                return false;
            }

            try
            {
                cancellationToken = destroyCancellationTokenSource.Token;
                return !cancellationToken.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        private void RegisterOwnershipEvents()
        {
            if (ownershipEventsRegistered)
            {
                return;
            }

            BasisNetworkPlayer.OnOwnershipTransfer += LowLevelOwnershipTransfer;
            BasisNetworkPlayer.OnOwnershipReleased += LowLevelOwnershipReleased;
            ownershipEventsRegistered = true;
        }
        private void UnregisterOwnershipEvents()
        {
            if (!ownershipEventsRegistered)
            {
                return;
            }

            BasisNetworkPlayer.OnOwnershipTransfer -= LowLevelOwnershipTransfer;
            BasisNetworkPlayer.OnOwnershipReleased -= LowLevelOwnershipReleased;
            ownershipEventsRegistered = false;
        }
        private void CleanupNetworkInitialization()
        {
            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterHandler(NetworkID, OnNetworkMessage);
            }
            UnregisterOwnershipEvents();
            ResetNetworkInitializationState();
        }
        private void ResetNetworkInitializationState()
        {
            HasNetworkID = false;
            NetworkID = 0;
            CurrentOwnerId = 0;
            currentOwnedPlayer = null;
            IsOwnedLocallyOnServer = false;
            IsOwnedLocallyOnClient = false;
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
