using System;
using System.Reflection;
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

        public CilboxProxy Target => target;

        public void Bind(CilboxProxy proxy)
        {
            target = proxy;
        }

        public override void OnNetworkReady(bool isLocallyOwned)
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnNetworkReady), new object[] { isLocallyOwned });
        }

        public override void OnNetworkTerminated(bool wasLocallyOwned)
        {
            BasisCilboxNetworkDispatch.Invoke(target, nameof(OnNetworkTerminated), new object[] { wasLocallyOwned });
        }

        public override void OnNetworkMessageReceived(ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                nameof(OnNetworkMessageReceived),
                new object[] { remoteUser, buffer, deliveryMethod });
        }

        public override void OnDirectNetworkMessageReceived(ushort remoteUser, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                nameof(OnDirectNetworkMessageReceived),
                new object[] { remoteUser, buffer, deliveryMethod });
        }

        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            BasisCilboxNetworkDispatch.Invoke(
                target,
                nameof(OnNetworkMessageServerReductionSystem),
                new object[] { buffer });
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
                "OnNetworkReady" => nameof(AvatarReadyCallbackNoop),
                "OnNetworkTerminated" => nameof(AvatarReadyCallbackNoop),
                "OnNetworkMessageReceived" => nameof(AvatarMessageCallbackNoop),
                "OnDirectNetworkMessageReceived" => nameof(AvatarMessageCallbackNoop),
                "OnNetworkMessageServerReductionSystem" => nameof(AvatarReductionCallbackNoop),
                "NetworkMessageSend" => nameof(NetworkMessageSend),
                "NetworkMessageSendDirect" => nameof(NetworkMessageSendDirect),
                "ServerReductionSystemMessageSend" => nameof(ServerReductionSystemMessageSend),
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
            return ResolveNormalHost(self).NetworkID;
        }

        public static bool IsLocalOwner(object self)
        {
            return ResolveNormalHost(self).IsLocalOwner();
        }

        public static void SendCustomNetworkEvent(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients)
        {
            ResolveNormalHost(self).SendCustomNetworkEvent(buffer, deliveryMethod, recipients);
        }

        public static void SendCustomNetworkEventDirect(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients,
            bool allowServerFallback)
        {
            ResolveNormalHost(self).SendCustomNetworkEventDirect(
                buffer, deliveryMethod, recipients, allowServerFallback);
        }

        public static void SendCustomEventDelayedSeconds(
            object self,
            Action callback,
            float delaySeconds,
            EventTiming timing)
        {
            ResolveNormalHost(self).SendCustomEventDelayedSeconds(callback, delaySeconds, timing);
        }

        public static void SendCustomEventDelayedFrames(
            object self,
            Action callback,
            int delayFrames,
            EventTiming timing)
        {
            ResolveNormalHost(self).SendCustomEventDelayedFrames(callback, delayFrames, timing);
        }

        public static void TakeOwnership(object self)
        {
            ResolveNormalHost(self).TakeOwnership();
        }

        public static Task<BasisOwnershipResult> TakeOwnershipAsync(object self, int timeout)
        {
            return ResolveNormalHost(self).TakeOwnershipAsync(timeout);
        }

        public static Task<BasisOwnershipResult> RequestWhoIsOwnershipAsync(object self, int timeout)
        {
            return ResolveNormalHost(self).RequestWhoIsOwnershipAsync(timeout);
        }

        public static void AvatarConstructor(object self) { }
        public static void AvatarReadyCallbackNoop(object self, bool locallyOwned) { }
        public static void AvatarMessageCallbackNoop(
            object self,
            ushort remoteUser,
            byte[] buffer,
            DeliveryMethod deliveryMethod) { }
        public static void AvatarReductionCallbackNoop(object self, byte[] buffer) { }

        public static void NetworkMessageSend(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients)
        {
            ResolveAvatarHost(self).NetworkMessageSend(buffer, deliveryMethod, recipients);
        }

        public static void NetworkMessageSend(object self, DeliveryMethod deliveryMethod)
        {
            ResolveAvatarHost(self).NetworkMessageSend(deliveryMethod);
        }

        public static void NetworkMessageSendDirect(
            object self,
            byte[] buffer,
            DeliveryMethod deliveryMethod,
            ushort[] recipients,
            bool allowServerFallback)
        {
            ResolveAvatarHost(self).NetworkMessageSendDirect(
                buffer, deliveryMethod, recipients, allowServerFallback);
        }

        public static void ServerReductionSystemMessageSend(object self, byte[] buffer)
        {
            ResolveAvatarHost(self).ServerReductionSystemMessageSend(buffer);
        }

        private static BasisNetworkCilboxBehaviour ResolveNormalHost(object self)
        {
            if (self is BasisNetworkCilboxBehaviour direct)
            {
                return direct;
            }

            if (self is CilboxProxy proxy)
            {
                BasisNetworkCilboxBehaviour[] hosts =
                    proxy.GetComponents<BasisNetworkCilboxBehaviour>();
                for (int i = 0; i < hosts.Length; i++)
                {
                    if (hosts[i] != null && hosts[i].Target == proxy)
                    {
                        return hosts[i];
                    }
                }
            }

            throw new InvalidOperationException(
                "No BasisNetworkCilboxBehaviour is bound to the interpreted networking instance.");
        }

        private static BasisNetworkAvatarCilboxBehaviour ResolveAvatarHost(object self)
        {
            if (self is BasisNetworkAvatarCilboxBehaviour direct)
            {
                return direct;
            }

            if (self is CilboxProxy proxy)
            {
                BasisNetworkAvatarCilboxBehaviour[] hosts =
                    proxy.GetComponents<BasisNetworkAvatarCilboxBehaviour>();
                for (int i = 0; i < hosts.Length; i++)
                {
                    if (hosts[i] != null && hosts[i].Target == proxy)
                    {
                        return hosts[i];
                    }
                }
            }

            throw new InvalidOperationException(
                "No BasisNetworkAvatarCilboxBehaviour is bound to the interpreted avatar networking instance.");
        }
    }
}
