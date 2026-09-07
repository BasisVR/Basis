using Basis.Network.Core;
using Basis.Scripts.Networking;
using System.Collections.Concurrent;
using System.Collections.Generic;

public delegate void BasisClientMessageHandler(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);

/// <summary>
/// Table-driven inbound dispatch for the client. Core messages bind to their dedicated
/// channel (0-59); multiplexed plugin messages bind to a ushort id read from the 61-63
/// channel payload. Lets handlers be added or removed without editing a shared constant table.
/// </summary>
public static class BasisClientMessageRegistry
{
    private static readonly BasisClientMessageHandler[] CoreHandlers = new BasisClientMessageHandler[BasisNetworkCommons.TotalChannels];
    private static readonly ConcurrentDictionary<ushort, BasisClientMessageHandler> PluginHandlers = new();

    /// <summary>The descriptors from the most recent server Supply, for introspection / plugin binding.</summary>
    public static SerializableBasis.BasisMessageDescriptor[] LastSupply { get; private set; }

    private static readonly ConcurrentDictionary<string, BasisClientMessageHandler> PluginHandlersByName = new();
    private static readonly ConcurrentDictionary<string, SerializableBasis.BasisMessageDescriptor> PluginDescriptorsByName = new();

    public static void RegisterCore(byte channel, BasisClientMessageHandler handler) => CoreHandlers[channel] = handler;

    public static BasisClientMessageHandler ResolveCore(byte channel) => channel < CoreHandlers.Length ? CoreHandlers[channel] : null;

    /// <summary>Bind a multiplexed plugin message id (carried on channels 61-63) to a handler.</summary>
    public static void RegisterPlugin(ushort id, BasisClientMessageHandler handler) => PluginHandlers[id] = handler;

    /// <summary>Remove a plugin message handler. Returns true if one was bound.</summary>
    public static bool UnregisterPlugin(ushort id) => PluginHandlers.TryRemove(id, out _);

    /// <summary>
    /// Reads the leading ushort message id from a plugin channel payload and dispatches it.
    /// Returns false (leaving the caller to recycle and log) when the id is unknown or the
    /// payload is too short to carry an id.
    /// </summary>
    public static bool DispatchPlugin(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (!reader.TryGetUShort(out ushort id))
        {
            return false;
        }
        if (PluginHandlers.TryGetValue(id, out BasisClientMessageHandler handler))
        {
            handler(peer, reader, channel, deliveryMethod);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Apply a server Supply manifest: record it, then reply with the message ids this client
    /// can actually handle (core ids with a bound handler, plus plugin ids it has registered).
    /// </summary>
    public static void ApplySupply(SerializableBasis.BasisMessageSupply supply, NetPeer peer)
    {
        LastSupply = supply.Descriptors ?? System.Array.Empty<SerializableBasis.BasisMessageDescriptor>();

        PluginDescriptorsByName.Clear();
        List<ushort> handled = new List<ushort>(LastSupply.Length);
        foreach (SerializableBasis.BasisMessageDescriptor descriptor in LastSupply)
        {
            if (string.IsNullOrEmpty(descriptor.Name))
            {
                continue;
            }
            if (BasisNetworkCommons.IsPluginChannel(descriptor.Channel))
            {
                PluginDescriptorsByName[descriptor.Name] = descriptor;
                if (PluginHandlersByName.TryGetValue(descriptor.Name, out BasisClientMessageHandler handler))
                {
                    PluginHandlers[descriptor.Id] = handler;
                    handled.Add(descriptor.Id);
                }
            }
            else if (ResolveCore(descriptor.Channel) != null)
            {
                handled.Add(descriptor.Id);
            }
        }

        SerializableBasis.BasisMessageSubscribe subscribe = new SerializableBasis.BasisMessageSubscribe { Ids = handled.ToArray() };
        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.RegistrySub_Subscribe);
        subscribe.Serialize(writer);
        peer.Send(writer, BasisNetworkCommons.RegistryControlChannel, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>
    /// Register a plugin handler by name. The server-assigned id is bound when the next Supply
    /// manifest arrives, or immediately if one was already received this session.
    /// </summary>
    public static void RegisterClientPlugin(string name, BasisClientMessageHandler handler)
    {
        PluginHandlersByName[name] = handler;
        if (PluginDescriptorsByName.TryGetValue(name, out SerializableBasis.BasisMessageDescriptor descriptor))
        {
            PluginHandlers[descriptor.Id] = handler;
        }
    }

    /// <summary>Remove a plugin handler registered by name.</summary>
    public static void UnregisterClientPlugin(string name)
    {
        PluginHandlersByName.TryRemove(name, out _);
        if (PluginDescriptorsByName.TryGetValue(name, out SerializableBasis.BasisMessageDescriptor descriptor))
        {
            PluginHandlers.TryRemove(descriptor.Id, out _);
        }
    }

    /// <summary>
    /// Send a plugin message to the server by name: prepends the server-assigned id and uses the
    /// descriptor's channel. Returns false if the server has not advertised this plugin or there is
    /// no live connection.
    /// </summary>
    public static bool Send(string name, System.Action<NetDataWriter> writePayload)
    {
        if (!PluginDescriptorsByName.TryGetValue(name, out SerializableBasis.BasisMessageDescriptor descriptor))
        {
            return false;
        }
        NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
        if (peer == null)
        {
            return false;
        }
        NetDataWriter writer = new NetDataWriter();
        writer.Put(descriptor.Id);
        writePayload?.Invoke(writer);
        peer.Send(writer, descriptor.Channel, BasisNetworkCommons.GetDeliveryForPluginChannel(descriptor.Channel));
        return true;
    }
}
