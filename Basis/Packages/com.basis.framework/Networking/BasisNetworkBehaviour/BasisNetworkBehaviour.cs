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

public enum NetworkOwnershipState
{
    /// <summary>
    /// Ownership state is unknown/uninitialized or server-owned.
    /// </summary>
    Unknown,
    /// <summary>
    /// Owned by a remote source.
    /// </summary>
    Remote,
    /// <summary>
    /// Ownership request sent, awaiting confirmation.
    /// </summary>
    PendingClaim,
    /// <summary>
    /// Owned locally.
    /// </summary>
    Local,
    /// <summary>
    /// Ownership release requested, awaiting confirmation.
    /// </summary>
    PendingRelease,
}

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
        public NetworkOwnershipState OwnershipState = NetworkOwnershipState.Unknown;
        /// <summary>
        /// True after server has confirmed ownership.
        /// Equivalent to OwnershipState == Local.
        /// </summary>
        public bool IsOwnedLocallyOnServer => OwnershipState == NetworkOwnershipState.Local;
        /// <summary>
        /// True from claim ownership start (PendingClaim) and
        /// while the server agrees (Local).
        /// </summary>
        public bool IsOwnedLocallyOnClient =>
            OwnershipState == NetworkOwnershipState.Local
            || OwnershipState == NetworkOwnershipState.PendingClaim;
        
        public ushort CurrentOwnerId;
        public BasisNetworkPlayer currentOwnedPlayer;
        private Task<BasisOwnershipResult> _pendingClaim;
        private Task<BasisOwnershipResult> _pendingRelease;
        private readonly CancellationTokenSource _destroyCts = new();

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
            _destroyCts.Cancel();
            if (HasNetworkID)
            {
                BasisNetworkGenericMessages.UnregisterHandler(NetworkID);
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
                bool wassuccesful = TryGetNetworkGUIDIdentifier(out string NetworkGuidID);
                if (wassuccesful == false)//this will happen to anything that has not got a GUID from the server
                {
                    //so if we dont get a GUID from the server lets make one!
                    string FileNamePath = LowLevelGetHierarchyPath(this);
                    AssignNetworkGUIDIdentifier(FileNamePath);

                    wassuccesful = TryGetNetworkGUIDIdentifier(out NetworkGuidID);
                }
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
            if (uniqueEntityID != clientIdentifier) return;
            Transition(isOwner ? NetworkOwnershipState.Local : NetworkOwnershipState.Remote);
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

        private void Transition(NetworkOwnershipState newState)
        {
            if (OwnershipState == newState) return;
            BasisDebug.Log($"[ownership] {clientIdentifier}: {OwnershipState} -> {newState}", BasisDebug.LogTag.Networking);
            OwnershipState = newState;
            OnOwnershipStateChanged();
        }
        /// <summary>
        /// Called whenever <see cref="OwnershipState"/> changes (including optimistic
        /// transitions and rollbacks on failed requests). Subclasses override to react
        /// without depending on the server-driven <see cref="OnOwnershipTransfer"/>.
        /// </summary>
        protected virtual void OnOwnershipStateChanged() { }
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
            await ClaimOwnership();
        }

        public Task<BasisOwnershipResult> ClaimOwnership(int timeoutMs = 5000)
        {
            if (OwnershipState == NetworkOwnershipState.Local)
                return Task.FromResult(new BasisOwnershipResult(true, BasisNetworkPlayer.LocalPlayer.playerId));
            if (_pendingClaim != null)
                return _pendingClaim;
            return _pendingClaim = InternalClaim(timeoutMs);
        }

        private async Task<BasisOwnershipResult> InternalClaim(int timeoutMs)
        {
            var prev = OwnershipState;
            Transition(NetworkOwnershipState.PendingClaim);
            CurrentOwnerId = BasisNetworkPlayer.LocalPlayer.playerId;
            currentOwnedPlayer = BasisNetworkPlayer.LocalPlayer;
            try
            {
                var result = await BasisNetworkOwnership.TakeOwnershipAsync(
                    clientIdentifier,
                    BasisNetworkConnection.LocalPlayerPeer.RemoteId,
                    timeoutMs,
                    _destroyCts.Token);
                // On failure, the server-driven OnOwnershipTransfer never fires, so revert
                // the optimistic transition. Caller is responsible for any retry.
                if (!result.Success && OwnershipState == NetworkOwnershipState.PendingClaim)
                {
                    Transition(prev);
                }
                return result;
            }
            finally { _pendingClaim = null; }
        }

        public Task<BasisOwnershipResult> ReleaseOwnership(int timeoutMs = 5000)
        {
            if (OwnershipState == NetworkOwnershipState.Unknown || OwnershipState == NetworkOwnershipState.Remote)
                return Task.FromResult(new BasisOwnershipResult(true, CurrentOwnerId));
            if (_pendingRelease != null)
                return _pendingRelease;
            return _pendingRelease = InternalRelease(timeoutMs);
        }

        private async Task<BasisOwnershipResult> InternalRelease(int timeoutMs)
        {
            var prev = OwnershipState;
            Transition(NetworkOwnershipState.PendingRelease);
            try
            {
                var result = await BasisNetworkOwnership.RemoveOwnershipAsync(
                    clientIdentifier,
                    timeoutMs,
                    _destroyCts.Token);
                if (!result.Success && OwnershipState == NetworkOwnershipState.PendingRelease)
                {
                    Transition(prev);
                }
                return result;
            }
            finally { _pendingRelease = null; }
        }

        /// <summary>
        /// Polls ownership state from the server. Triggers OnOwnershipTransfer.
        /// </summary>
        public async Task<BasisOwnershipResult> PollOwnership(int timeoutMs = 5000)
        {
            return await BasisNetworkOwnership.RequestCurrentOwnershipAsync(clientIdentifier, timeoutMs, _destroyCts.Token);
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
