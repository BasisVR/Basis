using Basis.Scripts.BasisSdk;
using LiteNetLib;
using System;
using System.Collections;
using UnityEngine;
using static BasisNetworkCommon;
namespace Basis
{
    public abstract class BasisNetworkBehaviour : BasisContentBase
    {
        [NonSerialized]
        public bool HasNetworkID = false;
        private ushort networkID;

        public ushort NetworkID { get => networkID; private set => networkID = value; }

        /// <summary>
        /// the reason its start instead of awake is to make sure progation occurs to everything no matter the net connect
        /// </summary>
        public async void Start()
        {
            bool wassuccesful = TryGetNetworkGUIDIdentifier(out string NetworkGuidID);
            if (wassuccesful)
            {
                BasisIdResolutionResult ThisNetworkBehavioursID = await BasisNetworkIdResolver.ResolveAsync(NetworkGuidID);
                HasNetworkID = ThisNetworkBehavioursID.Success;
                NetworkID = ThisNetworkBehavioursID.Id;
                if (HasNetworkID)
                {
                    OnNetworkReady();
                }
            }
        }
        public virtual void OnNetworkReady()
        {

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
            BasisScene.OnNetworkMessageSend?.Invoke(NetworkID, buffer, DeliveryMethod, Recipients);
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
        /*
        public virtual void Interact() { }
        public virtual void OnAvatarChanged(BasisPlayer player) { }
        public virtual void OnAvatarEyeHeightChanged(BasisPlayer player, float prevEyeHeightAsMeters) { }
        public virtual void OnOwnershipTransferred(BasisPlayer player) { }

        public virtual void OnPlayerRespawn(BasisPlayer player) { }
        public virtual bool OnOwnershipRequest(BasisPlayer requestingPlayer, BasisPlayer requestedOwner) => true;
        // public virtual void OnLanguageChanged(string language) { }

        public virtual void OnDrop() { }
        public virtual void OnPickup() { }
        public virtual void OnPickupUseDown() { }
        public virtual void OnPickupUseUp() { }
        */
    }
}
