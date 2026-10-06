using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Basis.Network.Core;
using Basis.Scripts.Networking.Behaviour;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
using UnityEngine;
using static BasisNetworkCommon;

namespace Basis.Shims
{
    /// <summary>
    /// Native networking host for a cilboxed script that was authored against
    /// <see cref="Basis.BasisNetworkBehaviour"/>.
    /// </summary>
    public sealed class BasisNetworkCilboxBehaviour : Basis.BasisNetworkBehaviour
    {
        [SerializeField] private CilboxProxy target;

        public CilboxProxy Target => target;

        public void Bind(CilboxProxy proxy)
        {
            target = proxy;
        }

        public override void OnNetworkReady()
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnNetworkReady));
        }

        public override void OnServerOwnershipDestroyed()
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnServerOwnershipDestroyed));
        }

        public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnOwnershipTransfer), new object[] { newOwner });
        }

        public override void OnNetworkMessage(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                nameof(OnNetworkMessage),
                new object[] { playerId, buffer, deliveryMethod });
        }

        public override void OnDirectNetworkMessage(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                nameof(OnDirectNetworkMessage),
                new object[] { playerId, buffer, deliveryMethod });
        }

        public override void OnPlayerLeft(BasisNetworkPlayer player)
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnPlayerLeft), new object[] { player });
        }

        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnPlayerJoined), new object[] { player });
        }
    }

    /// <summary>
    /// Native avatar-channel host for a cilboxed script authored against
    /// <see cref="BasisNetworkAvatarBehaviour"/>. This remains a real avatar network
    /// behaviour so BasisNetworkPlayer assigns its MessageIndex and player mapping normally.
    /// </summary>
    public sealed class BasisNetworkAvatarCilboxBehaviour : BasisNetworkAvatarBehaviour
    {
        [SerializeField] private CilboxProxy target;
        [SerializeField] private bool useGenericNetworkBehaviourApi;

        public CilboxProxy Target => target;
        public bool UsesGenericNetworkBehaviourApi => useGenericNetworkBehaviourApi;

        public void Bind(CilboxProxy proxy, bool genericNetworkBehaviourApi = false)
        {
            target = proxy;
            useGenericNetworkBehaviourApi = genericNetworkBehaviourApi;
        }

        private void Start()
        {
            if (!useGenericNetworkBehaviourApi)
            {
                return;
            }

            BasisNetworkPlayer.OnPlayerJoined += OnGenericPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft += OnGenericPlayerLeft;
        }

        private void OnDestroy()
        {
            if (!useGenericNetworkBehaviourApi)
            {
                return;
            }

            BasisNetworkPlayer.OnPlayerJoined -= OnGenericPlayerJoined;
            BasisNetworkPlayer.OnPlayerLeft -= OnGenericPlayerLeft;
        }

        public override void OnNetworkReady(bool isLocallyOwned)
        {
            if (useGenericNetworkBehaviourApi)
            {
                BasisCilboxNetworkDispatch.Invoke(target, "OnNetworkReady");
            }
            else
            {
                BasisCilboxNetworkDispatch.Invoke(target, nameof(OnNetworkReady), new object[] { isLocallyOwned });
            }
        }

        public override void OnNetworkTerminated(bool wasLocallyOwned)
        {
            if (!useGenericNetworkBehaviourApi)
            {
                BasisCilboxNetworkDispatch.Invoke(target, nameof(OnNetworkTerminated), new object[] { wasLocallyOwned });
            }
        }

        public override void OnNetworkMessageReceived(ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                useGenericNetworkBehaviourApi ? "OnNetworkMessage" : nameof(OnNetworkMessageReceived),
                new object[] { remoteUser, buffer, deliveryMethod });
        }

        public override void OnDirectNetworkMessageReceived(ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                useGenericNetworkBehaviourApi ? "OnDirectNetworkMessage" : nameof(OnDirectNetworkMessageReceived),
                new object[] { remoteUser, buffer, deliveryMethod });
        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            if (!useGenericNetworkBehaviourApi)
            {
                BasisCilboxNetworkDispatch.Invoke(
                    target,
                    nameof(OnNetworkMessageServerReductionSystem),
                    new object[] { buffer });
            }
        }

        internal bool IsGenericLocalOwner()
        {
            return NetworkedPlayer != null && NetworkedPlayer.IsLocal;
        }

        internal ushort GenericNetworkId()
        {
            return MessageIndex;
        }

        internal Task<BasisOwnershipResult> GenericTakeOwnershipAsync()
        {
            if (NetworkedPlayer != null && NetworkedPlayer.IsLocal)
            {
                return Task.FromResult(new BasisOwnershipResult(true, NetworkedPlayer.playerId));
            }

            return Task.FromResult(BasisOwnershipResult.Failed);
        }

        internal Task<BasisOwnershipResult> GenericRequestOwnershipAsync()
        {
            return NetworkedPlayer != null
                ? Task.FromResult(new BasisOwnershipResult(true, NetworkedPlayer.playerId))
                : Task.FromResult(BasisOwnershipResult.Failed);
        }

        internal void GenericSendCustomEventDelayedSeconds(Action callback, float delaySeconds, EventTiming timing)
        {
            StartCoroutine(InvokeGenericActionAfterSeconds(callback, delaySeconds, timing));
        }

        internal void GenericSendCustomEventDelayedFrames(Action callback, int delayFrames, EventTiming timing)
        {
            StartCoroutine(InvokeGenericActionAfterFrames(callback, delayFrames, timing));
        }

        private IEnumerator InvokeGenericActionAfterSeconds(Action callback, float delaySeconds, EventTiming timing)
        {
            switch (timing)
            {
                case EventTiming.FixedUpdate:
                    float fixedElapsed = 0f;
                    while (fixedElapsed < delaySeconds)
                    {
                        yield return new WaitForFixedUpdate();
                        fixedElapsed += Time.fixedDeltaTime;
                    }
                    break;
                case EventTiming.LateUpdate:
                    float lateElapsed = 0f;
                    while (lateElapsed < delaySeconds)
                    {
                        yield return new WaitForEndOfFrame();
                        lateElapsed += Time.deltaTime;
                    }
                    break;
                default:
                    yield return new WaitForSeconds(delaySeconds);
                    break;
            }

            callback?.Invoke();
        }

        private IEnumerator InvokeGenericActionAfterFrames(Action callback, int delayFrames, EventTiming timing)
        {
            for (int index = 0; index < delayFrames; index++)
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

        private void OnGenericPlayerJoined(BasisNetworkPlayer player)
        {
            BasisCilboxNetworkDispatch.Invoke(target, "OnPlayerJoined", new object[] { player });
        }

        private void OnGenericPlayerLeft(BasisNetworkPlayer player)
        {
            BasisCilboxNetworkDispatch.Invoke(target, "OnPlayerLeft", new object[] { player });
        }
    }

    internal static class BasisCilboxNetworkDispatch
    {
        public static void Invoke(CilboxProxy proxy, string methodName, object[] arguments = null)
        {
            if (proxy == null || proxy.disabled || !proxy.enabled)
            {
                return;
            }

            proxy.RuntimeProxyLoad();
            CilboxClass cls = proxy.cls;
            if (cls == null || cls.methodNameToIndex == null ||
                !cls.methodNameToIndex.TryGetValue(methodName, out uint methodIndex))
            {
                return;
            }

            CilboxMethod method = cls.methods[methodIndex];
            if (method == null || method.isStatic)
            {
                return;
            }

            int expected = method.signatureParameters != null ? method.signatureParameters.Length : 0;
            int supplied = arguments != null ? arguments.Length : 0;
            if (expected != supplied)
            {
                Debug.LogError(
                    $"[BasisCilboxNetwork] {proxy.className}.{methodName} expected {expected} arguments, got {supplied}.",
                    proxy);
                return;
            }

            try
            {
                method.Interpret(proxy, arguments);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, proxy);
            }
        }
    }

    /// <summary>
    /// Rewrites native calls whose receiver is an interpreted CilboxProxy to the native
    /// networking companion generated for that proxy. The source script still targets the
    /// ordinary Basis networking API.
    /// </summary>
    public static class BasisCilboxNetworkRedirect
    {
        internal static MethodInfo ResolveNormal(
            CilboxUsage usage,
            string name,
            SerializedTypeDescriptor[] parameters)
        {
            string redirectName = name switch
            {
                ".ctor" => nameof(NormalConstructor),
                "Start" => nameof(NormalLifecycleNoop),
                "OnDestroy" => nameof(NormalLifecycleNoop),
                "OnNetworkReady" => nameof(NormalCallbackNoop),
                "OnServerOwnershipDestroyed" => nameof(NormalCallbackNoop),
                "OnOwnershipTransfer" => nameof(NormalOwnershipCallbackNoop),
                "OnNetworkMessage" => nameof(NormalMessageCallbackNoop),
                "OnDirectNetworkMessage" => nameof(NormalMessageCallbackNoop),
                "OnPlayerLeft" => nameof(NormalPlayerCallbackNoop),
                "OnPlayerJoined" => nameof(NormalPlayerCallbackNoop),
                "get_NetworkID" => nameof(GetNetworkID),
                "IsLocalOwner" => nameof(IsLocalOwner),
                "SendCustomNetworkEvent" => nameof(SendCustomNetworkEvent),
                "SendCustomNetworkEventDirect" => nameof(SendCustomNetworkEventDirect),
                "SendCustomEventDelayedSeconds" => nameof(SendCustomEventDelayedSeconds),
                "SendCustomEventDelayedFrames" => nameof(SendCustomEventDelayedFrames),
                "TakeOwnership" => nameof(TakeOwnership),
                "TakeOwnershipAsync" => nameof(TakeOwnershipAsync),
                "RequestWhoIsOwnershipAsync" => nameof(RequestWhoIsOwnershipAsync),
                _ => null,
            };

            return Resolve(usage, redirectName, parameters);
        }

        internal static MethodInfo ResolveAvatar(
            CilboxUsage usage,
            string name,
            SerializedTypeDescriptor[] parameters)
        {
            string redirectName = name switch
            {
                ".ctor" => nameof(AvatarConstructor),

                // Native BasisNetworkAvatarBehaviour API.
                "OnNetworkReady" => parameters != null && parameters.Length == 0
                    ? nameof(AvatarGenericCallbackNoop)
                    : nameof(AvatarReadyCallbackNoop),
                "OnNetworkTerminated" => nameof(AvatarReadyCallbackNoop),
                "OnNetworkMessageReceived" => nameof(AvatarMessageCallbackNoop),
                "OnDirectNetworkMessageReceived" => nameof(AvatarMessageCallbackNoop),
                "OnNetworkMessageServerReductionSystem" => nameof(AvatarReductionCallbackNoop),
                "NetworkMessageSend" => nameof(NetworkMessageSend),
                "NetworkMessageSendDirect" => nameof(NetworkMessageSendDirect),
                "ServerReductionSystemMessageSend" => nameof(ServerReductionSystemMessageSend),

                // BasisNetworkBehaviour source API translated onto avatar MessageIndex routing.
                "Start" => nameof(AvatarGenericLifecycleNoop),
                "OnDestroy" => nameof(AvatarGenericLifecycleNoop),
                "OnServerOwnershipDestroyed" => nameof(AvatarGenericCallbackNoop),
                "OnOwnershipTransfer" => nameof(AvatarGenericOwnershipCallbackNoop),
                "OnNetworkMessage" => nameof(AvatarGenericMessageCallbackNoop),
                "OnDirectNetworkMessage" => nameof(AvatarGenericMessageCallbackNoop),
                "OnPlayerLeft" => nameof(AvatarGenericPlayerCallbackNoop),
                "OnPlayerJoined" => nameof(AvatarGenericPlayerCallbackNoop),
                "get_NetworkID" => nameof(AvatarGenericGetNetworkID),
                "IsLocalOwner" => nameof(AvatarGenericIsLocalOwner),
                "SendCustomNetworkEvent" => nameof(AvatarGenericSendCustomNetworkEvent),
                "SendCustomNetworkEventDirect" => nameof(AvatarGenericSendCustomNetworkEventDirect),
                "SendCustomEventDelayedSeconds" => nameof(AvatarGenericSendCustomEventDelayedSeconds),
                "SendCustomEventDelayedFrames" => nameof(AvatarGenericSendCustomEventDelayedFrames),
                "TakeOwnership" => nameof(AvatarGenericTakeOwnership),
                "TakeOwnershipAsync" => nameof(AvatarGenericTakeOwnershipAsync),
                "RequestWhoIsOwnershipAsync" => nameof(AvatarGenericRequestWhoIsOwnershipAsync),
                _ => null,
            };

            return Resolve(usage, redirectName, parameters);
        }

        private static MethodInfo Resolve(
            CilboxUsage usage,
            string redirectName,
            SerializedTypeDescriptor[] parameters)
        {
            if (usage == null || redirectName == null)
            {
                return null;
            }

            Type[] sourceParameters = usage.DescriptorsToArrayOfNativeTypes(
                parameters ?? Array.Empty<SerializedTypeDescriptor>());
            if (sourceParameters == null)
            {
                return null;
            }

            Type[] redirectParameters = new Type[sourceParameters.Length + 1];
            redirectParameters[0] = typeof(object);
            Array.Copy(sourceParameters, 0, redirectParameters, 1, sourceParameters.Length);

            return typeof(BasisCilboxNetworkRedirect).GetMethod(
                redirectName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                redirectParameters,
                null);
        }

        public static void NormalConstructor(object self) { }
        public static void NormalLifecycleNoop(object self) { }
        public static void NormalCallbackNoop(object self) { }
        public static void NormalOwnershipCallbackNoop(object self, BasisNetworkPlayer player) { }
        public static void NormalMessageCallbackNoop(object self, ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod) { }
        public static void NormalPlayerCallbackNoop(object self, BasisNetworkPlayer player) { }

        public static ushort GetNetworkID(object self)
        {
            return TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host)
                ? host.NetworkID
                : (ushort)0;
        }

        public static bool IsLocalOwner(object self)
        {
            return TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host) &&
                   host.IsLocalOwner();
        }

        public static void SendCustomNetworkEvent(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients)
        {
            if (TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host))
            {
                host.SendCustomNetworkEvent(buffer, deliveryMethod, recipients);
            }
        }

        public static void SendCustomNetworkEventDirect(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients,
            bool allowServerFallback)
        {
            if (TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host))
            {
                host.SendCustomNetworkEventDirect(
                    buffer, deliveryMethod, recipients, allowServerFallback);
            }
        }

        public static void SendCustomEventDelayedSeconds(
            object self,
            Action callback,
            float delaySeconds,
            EventTiming timing)
        {
            if (TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host))
            {
                host.SendCustomEventDelayedSeconds(callback, delaySeconds, timing);
            }
        }

        public static void SendCustomEventDelayedFrames(
            object self,
            Action callback,
            int delayFrames,
            EventTiming timing)
        {
            if (TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host))
            {
                host.SendCustomEventDelayedFrames(callback, delayFrames, timing);
            }
        }

        public static void TakeOwnership(object self)
        {
            if (TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host))
            {
                host.TakeOwnership();
            }
        }

        public static Task<BasisOwnershipResult> TakeOwnershipAsync(object self, int timeout)
        {
            return TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host)
                ? host.TakeOwnershipAsync(timeout)
                : Task.FromResult(BasisOwnershipResult.Failed);
        }

        public static Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(object self, int timeout)
        {
            return TryResolveNormalHost(self, out BasisNetworkCilboxBehaviour host)
                ? host.RequestWhoIsOwnershipAsync(timeout)
                : Task.FromResult(BasisOwnershipResult.Failed);
        }

        public static void AvatarConstructor(object self) { }
        public static void AvatarGenericLifecycleNoop(object self) { }
        public static void AvatarGenericCallbackNoop(object self) { }
        public static void AvatarReadyCallbackNoop(object self, bool locallyOwned) { }
        public static void AvatarGenericOwnershipCallbackNoop(object self, BasisNetworkPlayer player) { }
        public static void AvatarGenericMessageCallbackNoop(
            object self,
            ushort remoteUser,
            byte[] buffer,
            DeliveryMethod deliveryMethod) { }
        public static void AvatarGenericPlayerCallbackNoop(object self, BasisNetworkPlayer player) { }
        public static void AvatarMessageCallbackNoop(
            object self,
            ushort remoteUser,
            byte[] buffer,
            DeliveryMethod deliveryMethod) { }
        public static void AvatarReductionCallbackNoop(object self, byte[] buffer) { }

        public static ushort AvatarGenericGetNetworkID(object self)
        {
            return TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host)
                ? host.GenericNetworkId()
                : (ushort)0;
        }

        public static bool AvatarGenericIsLocalOwner(object self)
        {
            return TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host) &&
                   host.IsGenericLocalOwner();
        }

        public static void AvatarGenericSendCustomNetworkEvent(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.NetworkMessageSend(buffer, deliveryMethod, recipients);
            }
        }

        public static void AvatarGenericSendCustomNetworkEventDirect(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients,
            bool allowServerFallback)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.NetworkMessageSendDirect(buffer, deliveryMethod, recipients, allowServerFallback);
            }
        }

        public static void AvatarGenericSendCustomEventDelayedSeconds(
            object self,
            Action callback,
            float delaySeconds,
            EventTiming timing)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.GenericSendCustomEventDelayedSeconds(callback, delaySeconds, timing);
            }
        }

        public static void AvatarGenericSendCustomEventDelayedFrames(
            object self,
            Action callback,
            int delayFrames,
            EventTiming timing)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.GenericSendCustomEventDelayedFrames(callback, delayFrames, timing);
            }
        }

        public static void AvatarGenericTakeOwnership(object self)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                _ = host.GenericTakeOwnershipAsync();
            }
        }

        public static Task<BasisOwnershipResult> AvatarGenericTakeOwnershipAsync(object self, int timeout)
        {
            return TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host)
                ? host.GenericTakeOwnershipAsync()
                : Task.FromResult(BasisOwnershipResult.Failed);
        }

        public static Task<BasisOwnershipResult> AvatarGenericRequestWhoIsOwnershipAsync(object self, int timeout)
        {
            return TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host)
                ? host.GenericRequestOwnershipAsync()
                : Task.FromResult(BasisOwnershipResult.Failed);
        }

        public static void NetworkMessageSend(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.NetworkMessageSend(buffer, deliveryMethod, recipients);
            }
        }

        public static void NetworkMessageSend(object self, DeliveryMethod deliveryMethod)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.NetworkMessageSend(deliveryMethod);
            }
        }

        public static void NetworkMessageSendDirect(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients,
            bool allowServerFallback)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.NetworkMessageSendDirect(
                    buffer, deliveryMethod, recipients, allowServerFallback);
            }
        }

        public static void ServerReductionSystemMessageSend(object self, byte[] buffer)
        {
            if (TryResolveAvatarHost(self, out BasisNetworkAvatarCilboxBehaviour host))
            {
                host.ServerReductionSystemMessageSend(buffer);
            }
        }

        private static readonly ConditionalWeakTable<object, HashSet<string>> MissingHostLogs =
            new ConditionalWeakTable<object, HashSet<string>>();
        private static readonly object MissingHostLogLock = new object();

        private static bool TryResolveNormalHost(object self, out BasisNetworkCilboxBehaviour host)
        {
            if (self is BasisNetworkCilboxBehaviour direct)
            {
                host = direct;
                return true;
            }

            if (self is CilboxProxy proxy)
            {
                BasisNetworkCilboxBehaviour[] hosts =
                    proxy.GetComponents<BasisNetworkCilboxBehaviour>();
                for (int i = 0; i < hosts.Length; i++)
                {
                    if (hosts[i] != null && hosts[i].Target == proxy)
                    {
                        host = hosts[i];
                        return true;
                    }
                }
            }

            host = null;
            LogMissingHostOnce(self, nameof(BasisNetworkCilboxBehaviour));
            return false;
        }

        private static bool TryResolveAvatarHost(object self, out BasisNetworkAvatarCilboxBehaviour host)
        {
            if (self is BasisNetworkAvatarCilboxBehaviour direct)
            {
                host = direct;
                return true;
            }

            if (self is CilboxProxy proxy)
            {
                BasisNetworkAvatarCilboxBehaviour[] hosts =
                    proxy.GetComponents<BasisNetworkAvatarCilboxBehaviour>();
                for (int i = 0; i < hosts.Length; i++)
                {
                    if (hosts[i] != null && hosts[i].Target == proxy)
                    {
                        host = hosts[i];
                        return true;
                    }
                }
            }

            host = null;
            LogMissingHostOnce(self, nameof(BasisNetworkAvatarCilboxBehaviour));
            return false;
        }

        private static void LogMissingHostOnce(object self, string hostType)
        {
            if (self != null)
            {
                lock (MissingHostLogLock)
                {
                    HashSet<string> logged = MissingHostLogs.GetOrCreateValue(self);
                    if (!logged.Add(hostType))
                    {
                        return;
                    }
                }
            }

            Debug.LogError(
                $"[BasisCilboxNetwork] No {hostType} is bound to this interpreted networking instance; the operation was ignored.",
                self as UnityEngine.Object);
        }
    }
}
