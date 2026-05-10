using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HVR.Basis.Comms.OSC;
using HVR.Basis.Comms.OSC.Lyuma;
using UnityEngine;

namespace HVR.Basis.Comms
{
    public static class BasisOscService
    {
        private const string AvatarParametersPrefix = "/avatar/parameters/";
        public static event Action<OscMessage> MessageReceived;

        private sealed class ReceiverRegistration
        {
            public Action<OscMessage> Handler;
            public Action<int, float> AddressHandler;
            public bool ReceiveAll;
            public string[] ExactAddresses = Array.Empty<string>();
            public string[] PrefixAddresses = Array.Empty<string>();
        }

        private readonly struct PrefixRoute
        {
            public PrefixRoute(string prefix, EntityId[] ownerIds)
            {
                Prefix = prefix;
                OwnerIds = ownerIds;
            }

            public string Prefix { get; }
            public EntityId[] OwnerIds { get; }
        }

        private sealed class DispatcherState
        {
            public static readonly DispatcherState Empty = new DispatcherState(
                new Dictionary<EntityId, Action<OscMessage>>(),
                new Dictionary<EntityId, Action<int, float>>(),
                new Dictionary<string, EntityId[]>(StringComparer.Ordinal),
                Array.Empty<PrefixRoute>(),
                Array.Empty<EntityId>());

            public DispatcherState(
                Dictionary<EntityId, Action<OscMessage>> handlers,
                Dictionary<EntityId, Action<int, float>> addressHandlers,
                Dictionary<string, EntityId[]> exactRoutes,
                PrefixRoute[] prefixRoutes,
                EntityId[] receiveAllRoutes)
            {
                Handlers = handlers;
                AddressHandlers = addressHandlers;
                ExactRoutes = exactRoutes;
                PrefixRoutes = prefixRoutes;
                ReceiveAllRoutes = receiveAllRoutes;
                ResolvedPathCache = new ConcurrentDictionary<string, EntityId[]>(StringComparer.Ordinal);
            }

            public Dictionary<EntityId, Action<OscMessage>> Handlers { get; }
            public Dictionary<EntityId, Action<int, float>> AddressHandlers { get; }
            public Dictionary<string, EntityId[]> ExactRoutes { get; }
            public PrefixRoute[] PrefixRoutes { get; }
            public EntityId[] ReceiveAllRoutes { get; }
            public ConcurrentDictionary<string, EntityId[]> ResolvedPathCache { get; }

            public bool HasRoutes =>
                ReceiveAllRoutes.Length > 0 ||
                ExactRoutes.Count > 0 ||
                PrefixRoutes.Length > 0;
        }

        private static readonly object ReceiverLock = new object();
        private static readonly Dictionary<EntityId, ReceiverRegistration> Receivers = new Dictionary<EntityId, ReceiverRegistration>();
        private static readonly ConcurrentDictionary<string, int> RawPathToAddressId = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        // Main-thread dispatch scratch buffer. Rebuilt with receiver capacity whenever subscriptions change.
        private static EntityId[] _dispatchOwnerBuffer = Array.Empty<EntityId>();

        private static DispatcherState _dispatcherState = DispatcherState.Empty;

        public static void EnsureInitialized()
        {
            _ = OSCAcquisitionServer.SceneInstance;
        }

        public static void RegisterReceiver(EntityId ownerId, Action<OscMessage> handler)
        {
            lock (ReceiverLock)
            {
                if (!Receivers.TryGetValue(ownerId, out ReceiverRegistration registration))
                {
                    registration = new ReceiverRegistration();
                    Receivers[ownerId] = registration;
                }

                registration.Handler = handler;
                RebuildDispatcherState_NoLock();
            }
        }

        public static void RegisterAddressReceiver(EntityId ownerId, Action<int, float> handler)
        {
            lock (ReceiverLock)
            {
                if (!Receivers.TryGetValue(ownerId, out ReceiverRegistration registration))
                {
                    registration = new ReceiverRegistration();
                    Receivers[ownerId] = registration;
                }

                registration.AddressHandler = handler;
                RebuildDispatcherState_NoLock();
            }
        }

        public static void UnregisterReceiver(EntityId ownerId)
        {
            lock (ReceiverLock)
            {
                if (Receivers.TryGetValue(ownerId, out ReceiverRegistration registration))
                {
                    registration.Handler = null;
                    if (registration.AddressHandler == null)
                    {
                        Receivers.Remove(ownerId);
                    }

                    RebuildDispatcherState_NoLock();
                }
            }
        }

        public static void UnregisterAddressReceiver(EntityId ownerId)
        {
            lock (ReceiverLock)
            {
                if (Receivers.TryGetValue(ownerId, out ReceiverRegistration registration))
                {
                    registration.AddressHandler = null;
                    if (registration.Handler == null)
                    {
                        Receivers.Remove(ownerId);
                    }

                    RebuildDispatcherState_NoLock();
                }
            }
        }

        internal static void Publish(OscMessage message)
        {
            if (message == null)
            {
                return;
            }

            MessageReceived?.Invoke(message);

            DispatcherState snapshot = Volatile.Read(ref _dispatcherState);
            DeliverMessage(snapshot, message, MatchOwners(snapshot, message.Path ?? string.Empty));
        }

        internal static void SubmitRawMessages(List<SimpleOSC.OSCMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            DispatcherState snapshot = Volatile.Read(ref _dispatcherState);
            bool hasAddressConsumers = snapshot.AddressHandlers.Count > 0;
            bool hasOscConsumers = snapshot.Handlers.Count > 0 || MessageReceived != null;
            if (!snapshot.HasRoutes && !hasAddressConsumers && !hasOscConsumers)
            {
                return;
            }

            if (hasAddressConsumers)
            {
                DispatchAddressValues(snapshot, messages);
            }

            if (!hasOscConsumers)
            {
                return;
            }

            DispatchRawMessages(snapshot, messages);
        }

        public static void PublishValue(string address, OscData value)
        {
            PublishValues(address, value == null ? Array.Empty<OscData>() : new[] { value });
        }

        public static void PublishValues(string address, OscData[] values)
        {
            OSCAcquisitionServer.SceneInstance.PublishValues(address, values);
        }

        public static void UpdateSubscriptions(EntityId ownerId, bool receiveAll, IEnumerable<string> subscribedAddresses, IEnumerable<string> subscribedPrefixes)
        {
            string[] exactAddresses = ToSortedUniqueArray(subscribedAddresses);
            string[] prefixAddresses = ToSortedUniqueArray(subscribedPrefixes);

            lock (ReceiverLock)
            {
                if (!Receivers.TryGetValue(ownerId, out ReceiverRegistration registration))
                {
                    registration = new ReceiverRegistration();
                    Receivers[ownerId] = registration;
                }

                registration.ReceiveAll = receiveAll;
                registration.ExactAddresses = exactAddresses;
                registration.PrefixAddresses = prefixAddresses;
                RebuildDispatcherState_NoLock();
            }
            // (eventual consistency is acceptable) and this avoids a potential deadlock if the acquisition server needs to call back into this service while processing the subscription update.
            OSCAcquisitionServer.SceneInstance.UpdateSubscriptions(ownerId, exactAddresses, prefixAddresses);
        }

        public static void UpdateSubscriptions(EntityId ownerId, IEnumerable<string> subscribedAddresses, IEnumerable<string> subscribedPrefixes)
        {
            UpdateSubscriptions(ownerId, false, subscribedAddresses, subscribedPrefixes);
        }

        public static void ClearSubscriptions(EntityId ownerId)
        {
            lock (ReceiverLock)
            {
                if (Receivers.TryGetValue(ownerId, out ReceiverRegistration registration))
                {
                    registration.ReceiveAll = false;
                    registration.ExactAddresses = Array.Empty<string>();
                    registration.PrefixAddresses = Array.Empty<string>();
                    RebuildDispatcherState_NoLock();
                }
            }

            OSCAcquisitionServer.SceneInstance.ClearSubscriptions(ownerId);
        }

        private static void RebuildDispatcherState_NoLock()
        {
            EnsureDispatchOwnerBufferCapacity_NoLock(Math.Max(Receivers.Count, 4));

            Dictionary<EntityId, Action<OscMessage>> handlers = new Dictionary<EntityId, Action<OscMessage>>();
            Dictionary<EntityId, Action<int, float>> addressHandlers = new Dictionary<EntityId, Action<int, float>>();
            Dictionary<string, List<EntityId>> exactRouteLists = new Dictionary<string, List<EntityId>>(StringComparer.Ordinal);
            Dictionary<string, List<EntityId>> prefixRouteLists = new Dictionary<string, List<EntityId>>(StringComparer.Ordinal);
            List<EntityId> receiveAllOwners = new List<EntityId>();

            foreach (KeyValuePair<EntityId, ReceiverRegistration> pair in Receivers)
            {
                EntityId ownerId = pair.Key;
                ReceiverRegistration registration = pair.Value;
                bool hasMessageHandler = registration?.Handler != null;
                bool hasAddressHandler = registration?.AddressHandler != null;
                if (!hasMessageHandler && !hasAddressHandler)
                {
                    continue;
                }

                if (hasMessageHandler)
                {
                    handlers[ownerId] = registration.Handler;
                }

                if (hasAddressHandler)
                {
                    addressHandlers[ownerId] = registration.AddressHandler;
                }

                if (registration.ReceiveAll)
                {
                    receiveAllOwners.Add(ownerId);
                }

                AddOwnerToRoutes(exactRouteLists, registration.ExactAddresses, ownerId);
                AddOwnerToRoutes(prefixRouteLists, registration.PrefixAddresses, ownerId);
            }

            Dictionary<string, EntityId[]> exactRoutes = new Dictionary<string, EntityId[]>(exactRouteLists.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<EntityId>> pair in exactRouteLists)
            {
                exactRoutes[pair.Key] = pair.Value.ToArray();
            }

            PrefixRoute[] prefixRoutes = new PrefixRoute[prefixRouteLists.Count];
            int prefixIndex = 0;
            foreach (KeyValuePair<string, List<EntityId>> pair in prefixRouteLists)
            {
                prefixRoutes[prefixIndex++] = new PrefixRoute(pair.Key, pair.Value.ToArray());
            }

            Array.Sort(prefixRoutes, ComparePrefixRoutes);
            Volatile.Write(ref _dispatcherState, new DispatcherState(handlers, addressHandlers, exactRoutes, prefixRoutes, receiveAllOwners.ToArray()));
        }

        private static void EnsureDispatchOwnerBufferCapacity_NoLock(int requiredCapacity)
        {
            if (_dispatchOwnerBuffer.Length >= requiredCapacity)
            {
                return;
            }

            _dispatchOwnerBuffer = new EntityId[requiredCapacity];
        }

        private static int ComparePrefixRoutes(PrefixRoute left, PrefixRoute right)
        {
            return string.CompareOrdinal(left.Prefix, right.Prefix);
        }

        private static void AddOwnerToRoutes(Dictionary<string, List<EntityId>> routes, string[] paths, EntityId ownerId)
        {
            if (paths == null)
            {
                return;
            }

            int pathCount = paths.Length;
            for (int i = 0; i < pathCount; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                if (!routes.TryGetValue(path, out List<EntityId> owners))
                {
                    owners = new List<EntityId>();
                    routes[path] = owners;
                }

                owners.Add(ownerId);
            }
        }

        private static string[] ToSortedUniqueArray(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return Array.Empty<string>();
            }

            HashSet<string> normalized = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    normalized.Add(path);
                }
            }

            string[] result = new string[normalized.Count];
            normalized.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        private static EntityId[] MatchOwners(DispatcherState snapshot, string path)
        {
            if (snapshot == null || !snapshot.HasRoutes)
            {
                return Array.Empty<EntityId>();
            }

            if (snapshot.PrefixRoutes.Length == 0)
            {
                if (snapshot.ReceiveAllRoutes.Length == 0)
                {
                    return snapshot.ExactRoutes.TryGetValue(path, out EntityId[] exactOnlyOwners)
                        ? exactOnlyOwners
                        : Array.Empty<EntityId>();
                }

                if (snapshot.ExactRoutes.Count == 0)
                {
                    return snapshot.ReceiveAllRoutes;
                }
            }

            if (snapshot.ResolvedPathCache.TryGetValue(path, out EntityId[] cachedOwners))
            {
                return cachedOwners;
            }

            EntityId[] resolvedOwners = ResolveOwners(snapshot, path);
            return snapshot.ResolvedPathCache.GetOrAdd(path, resolvedOwners);
        }

        private static EntityId[] ResolveOwners(DispatcherState snapshot, string path)
        {
            EntityId[] matchesBuffer = null;
            int matchCount = 0;

            try
            {
                AddOwners(ref matchesBuffer, ref matchCount, snapshot.ReceiveAllRoutes);

                if (snapshot.ExactRoutes.TryGetValue(path, out EntityId[] exactOwners))
                {
                    AddOwners(ref matchesBuffer, ref matchCount, exactOwners);
                }

                PrefixRoute[] prefixRoutes = snapshot.PrefixRoutes;
                int prefixRouteCount = prefixRoutes.Length;
                for (int i = 0; i < prefixRouteCount; i++)
                {
                    PrefixRoute route = prefixRoutes[i];
                    if (path.StartsWith(route.Prefix, StringComparison.Ordinal))
                    {
                        AddOwners(ref matchesBuffer, ref matchCount, route.OwnerIds);
                    }
                }

                if (matchCount == 0)
                {
                    return Array.Empty<EntityId>();
                }

                EntityId[] resolvedOwners = new EntityId[matchCount];
                Array.Copy(matchesBuffer, 0, resolvedOwners, 0, matchCount);
                return resolvedOwners;
            }
            finally
            {
                if (matchesBuffer != null)
                {
                    ArrayPool<EntityId>.Shared.Return(matchesBuffer, false);
                }
            }
        }

        private static void AddOwners(ref EntityId[] matchesBuffer, ref int matchCount, EntityId[] owners)
        {
            if (owners == null || owners.Length == 0)
            {
                return;
            }

            int ownerCount = owners.Length;
            EnsureMatchCapacity(ref matchesBuffer, matchCount, matchCount + ownerCount);
            for (int ownerIndex = 0; ownerIndex < ownerCount; ownerIndex++)
            {
                EntityId ownerId = owners[ownerIndex];
                bool alreadyAdded = false;
                for (int matchIndex = 0; matchIndex < matchCount; matchIndex++)
                {
                    if (matchesBuffer[matchIndex].Equals(ownerId))
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                {
                    matchesBuffer[matchCount++] = ownerId;
                }
            }
        }

        private static void EnsureMatchCapacity(ref EntityId[] matchesBuffer, int matchCount, int requiredCapacity)
        {
            if (matchesBuffer == null)
            {
                matchesBuffer = ArrayPool<EntityId>.Shared.Rent(Math.Max(requiredCapacity, 4));
                return;
            }

            if (matchesBuffer.Length >= requiredCapacity)
            {
                return;
            }

            int expandedCapacity = Math.Max(requiredCapacity, matchesBuffer.Length * 2);
            EntityId[] expandedBuffer = ArrayPool<EntityId>.Shared.Rent(expandedCapacity);
            Array.Copy(matchesBuffer, 0, expandedBuffer, 0, matchCount);
            ArrayPool<EntityId>.Shared.Return(matchesBuffer, false);
            matchesBuffer = expandedBuffer;
        }

        private static void DispatchRawMessages(DispatcherState snapshot, List<SimpleOSC.OSCMessage> messages)
        {
            int messageCount = messages.Count;
            for (int messageIndex = 0; messageIndex < messageCount; messageIndex++)
            {
                SimpleOSC.OSCMessage rawMessage = messages[messageIndex];
                int matchedOwnerCount = CollectMatchedOwners(snapshot, rawMessage.path ?? string.Empty, _dispatchOwnerBuffer);
                bool hasMatchedOwners = matchedOwnerCount > 0;
                bool hasGlobalListeners = MessageReceived != null;
                if (!hasMatchedOwners && !hasGlobalListeners)
                {
                    continue;
                }

                OscMessage convertedMessage = null;
                if (hasGlobalListeners)
                {
                    try
                    {
                        convertedMessage = OscMessage.FromRaw(rawMessage);
                        MessageReceived?.Invoke(convertedMessage);
                    }
                    catch (Exception e)
                    {
                        BasisDebug.LogWarning($"Failed to convert inbound OSC message {rawMessage.path} ({e.Message})", BasisDebug.LogTag.LocalNetwork);
                        continue;
                    }
                }

                if (!hasMatchedOwners)
                {
                    continue;
                }

                for (int ownerIndex = 0; ownerIndex < matchedOwnerCount; ownerIndex++)
                {
                    EntityId ownerId = _dispatchOwnerBuffer[ownerIndex];
                    if (!snapshot.Handlers.TryGetValue(ownerId, out Action<OscMessage> handler) || handler == null)
                    {
                        continue;
                    }

                    if (convertedMessage == null)
                    {
                        try
                        {
                            convertedMessage = OscMessage.FromRaw(rawMessage);
                        }
                        catch (Exception e)
                        {
                            BasisDebug.LogWarning($"Failed to convert inbound OSC message {rawMessage.path} ({e.Message})", BasisDebug.LogTag.LocalNetwork);
                            break;
                        }
                    }

                    handler(convertedMessage);
                }
            }
        }

        private static void DeliverMessage(DispatcherState currentState, OscMessage message, EntityId[] matchedOwners)
        {
            if (currentState == null || matchedOwners == null || matchedOwners.Length == 0)
            {
                return;
            }

            int matchedOwnerCount = matchedOwners.Length;
            for (int ownerIndex = 0; ownerIndex < matchedOwnerCount; ownerIndex++)
            {
                if (currentState.Handlers.TryGetValue(matchedOwners[ownerIndex], out Action<OscMessage> handler) && handler != null)
                {
                    handler(message);
                }
            }
        }

        private static void DispatchAddressValues(DispatcherState snapshot, List<SimpleOSC.OSCMessage> messages)
        {
            int messageCount = messages.Count;
            for (int messageIndex = 0; messageIndex < messageCount; messageIndex++)
            {
                SimpleOSC.OSCMessage rawMessage = messages[messageIndex];
                int matchedOwnerCount = CollectMatchedOwners(snapshot, rawMessage.path ?? string.Empty, _dispatchOwnerBuffer);
                if (matchedOwnerCount == 0)
                {
                    continue;
                }

                if (!TryResolveAddressValue(rawMessage, out int addressId, out float value))
                {
                    continue;
                }

                for (int ownerIndex = 0; ownerIndex < matchedOwnerCount; ownerIndex++)
                {
                    EntityId ownerId = _dispatchOwnerBuffer[ownerIndex];
                    if (snapshot.AddressHandlers.TryGetValue(ownerId, out Action<int, float> addressHandler) && addressHandler != null)
                    {
                        addressHandler(addressId, value);
                    }
                }
            }
        }

        private static int CollectMatchedOwners(DispatcherState snapshot, string path, EntityId[] ownerBuffer)
        {
            if (snapshot == null || !snapshot.HasRoutes || ownerBuffer == null || ownerBuffer.Length == 0)
            {
                return 0;
            }

            int ownerCount = 0;
            AddMatchedOwnersNoAlloc(ownerBuffer, ref ownerCount, snapshot.ReceiveAllRoutes);

            if (snapshot.ExactRoutes.TryGetValue(path, out EntityId[] exactOwners))
            {
                AddMatchedOwnersNoAlloc(ownerBuffer, ref ownerCount, exactOwners);
            }

            PrefixRoute[] prefixRoutes = snapshot.PrefixRoutes;
            int prefixRouteCount = prefixRoutes.Length;
            for (int i = 0; i < prefixRouteCount; i++)
            {
                PrefixRoute route = prefixRoutes[i];
                if (path.StartsWith(route.Prefix, StringComparison.Ordinal))
                {
                    AddMatchedOwnersNoAlloc(ownerBuffer, ref ownerCount, route.OwnerIds);
                }
            }

            return ownerCount;
        }

        private static void AddMatchedOwnersNoAlloc(EntityId[] ownerBuffer, ref int ownerCount, EntityId[] owners)
        {
            if (owners == null || owners.Length == 0)
            {
                return;
            }

            int sourceOwnerCount = owners.Length;
            for (int sourceIndex = 0; sourceIndex < sourceOwnerCount; sourceIndex++)
            {
                EntityId ownerId = owners[sourceIndex];
                bool alreadyAdded = false;
                for (int existingIndex = 0; existingIndex < ownerCount; existingIndex++)
                {
                    if (ownerBuffer[existingIndex].Equals(ownerId))
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                {
                    ownerBuffer[ownerCount++] = ownerId;
                }
            }
        }

        private static bool TryResolveAddressValue(SimpleOSC.OSCMessage message, out int addressId, out float value)
        {
            addressId = 0;
            value = 0f;

            object[] arguments = message.arguments;
            if (arguments == null || arguments.Length == 0 || !(arguments[0] is float floatValue))
            {
                return false;
            }

            addressId = ResolveAddressId(message.path ?? string.Empty);
            value = floatValue;
            return true;
        }

        private static int ResolveAddressId(string rawPath)
        {
            if (RawPathToAddressId.TryGetValue(rawPath, out int addressId))
            {
                return addressId;
            }

            string normalizedPath = rawPath.StartsWith(AvatarParametersPrefix, StringComparison.Ordinal)
                ? rawPath.Substring(AvatarParametersPrefix.Length)
                : rawPath;

            addressId = HVRAddress.AddressToId(normalizedPath);
            RawPathToAddressId.TryAdd(rawPath, addressId);
            return addressId;
        }
    }
}
