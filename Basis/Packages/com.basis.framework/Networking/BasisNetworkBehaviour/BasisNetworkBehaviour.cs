using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using LiteNetLib;
using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using static BasisNetworkCommon;
namespace Basis
{
    public abstract class BasisNetworkBehaviour : BasisContentBase
    {
        private bool HasNetworkID = false;
        private ushort networkID;
        public ushort NetworkID
        {
            get => networkID;
            private set => networkID = value;
        }
        public BasisOwnershipResult CurrentOwnershipStatus;
        public bool IsOwnedLocally = false;
        /// <summary>
        /// the reason its start instead of awake is to make sure progation occurs to everything no matter the net connect
        /// </summary>
        public void Start()
        {
            if (BasisNetworkManagement.LocalPlayerIsConnected == false)
            {
                BasisNetworkPlayer.OnLocalPlayerJoined += OnLocalPlayerJoined;
            }
            else
            {
                OnLocalPlayerJoined(null, null);
            }
        }
        public void OnDestroy()
        {
            BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalPlayerJoined;
            BasisNetworkPlayer.OnOwnershipTransfer -= LowLevelOwnershipTransfer;
            BasisNetworkPlayer.OnOwnershipReleased -= LowLevelOwnershipReleased;
            BasisScene.OnNetworkMessageReceived -= LowLevelNetworkMessageReceived;
        }
        private async void OnLocalPlayerJoined(BasisNetworkPlayer player1, BasisLocalPlayer player2)
        {
            if (BasisNetworkManagement.LocalPlayerIsConnected)
            {
                bool wassuccesful = TryGetNetworkGUIDIdentifier(out string NetworkGuidID);
                if (wassuccesful == false)//this will happen to anything that has not got a GUID from the server
                {
                    //so if we dont get a GUID from the server lets make one!
                    string FileNamePath = LowLevelGetHierarchyPath(this);
                    AssignNetworkGUIDIdentifier(FileNamePath);

                    wassuccesful = TryGetNetworkGUIDIdentifier(out NetworkGuidID);
                }
                if (wassuccesful)
                {
                    BasisNetworkPlayer.OnOwnershipTransfer += LowLevelOwnershipTransfer;
                    BasisNetworkPlayer.OnOwnershipReleased += LowLevelOwnershipReleased;
                    BasisScene.OnNetworkMessageReceived += LowLevelNetworkMessageReceived;


                    Task<BasisIdResolutionResult> IDResolverAsync = BasisNetworkIdResolver.ResolveAsync(NetworkGuidID);
                    Task<BasisOwnershipResult> output = BasisNetworkOwnership.RequestCurrentOwnershipAsync(NetworkGuidID);
                    Task[] tasks = new Task[] { IDResolverAsync, output };

                    await Task.WhenAll(tasks);

                    //convert GUID into Ushort for network transport.
                    BasisIdResolutionResult IDResolverResult = await IDResolverAsync;
                    CurrentOwnershipStatus = await output;
                    HasNetworkID = IDResolverResult.Success;
                    NetworkID = IDResolverResult.Id;
                    if (HasNetworkID)
                    {
                        OnNetworkReady();
                    }
                }
            }
        }
        public void LowLevelNetworkMessageReceived(ushort PlayerID, ushort messageIndex, byte[] buffer, DeliveryMethod DeliveryMethod)
        {
            if (HasNetworkID && messageIndex == NetworkID)
            {
                OnNetworkMessage(PlayerID, buffer, DeliveryMethod);
            }
        }
        private void LowLevelOwnershipReleased(string uniqueEntityID)
        {
            if (uniqueEntityID == clientIdentifier)
            {
                ServerOwnershipDestroyed();
            }
        }
        private void LowLevelOwnershipTransfer(string uniqueEntityID, ushort NetIdNewOwner, bool isOwner)
        {
            if (uniqueEntityID == clientIdentifier)
            {
                IsOwnedLocally = isOwner;
                BasisDebug.Log("Owner set to " + IsOwnedLocally);
                OnOwnershipTransfer(NetIdNewOwner);
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
                BasisScene.OnNetworkMessageSend?.Invoke(NetworkID, buffer, DeliveryMethod, Recipients);
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
        public static string LowLevelGetHierarchyPath(BasisContentBase obj)
        {
            // Get the index of the component on the GameObject
            Component[] components = obj.gameObject.GetComponents(obj.GetType());
            int index = System.Array.IndexOf(components, obj);

            string path = obj.gameObject.name + obj.GetType() + index;
            Transform current = obj.transform.parent;

            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }
        public virtual void OnNetworkReady()
        {

        }
        /// <summary>
        /// back to no one owning it, (item no longer exists for example)
        /// </summary>
        public virtual void ServerOwnershipDestroyed()
        {

        }
        public virtual void OnOwnershipTransfer(ushort NetIdNewOwner)
        {

        }
        public virtual void OnNetworkMessage(ushort PlayerID, byte[] buffer, DeliveryMethod DeliveryMethod)
        {

        }
    }
}
