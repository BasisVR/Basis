using System;
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// One share kind whose payload travels inside the share message rather than being fetched from a
/// bundle address. The registering package owns what the payload means; the framework only carries
/// it, labels the orb with it and offers it to the player.
/// </summary>
public sealed class BasisContentSharePayloadKind
{
    public ContentShareType Type;

    /// <summary>Shown on the orb and in the dialogue title, e.g. "Dolly Track".</summary>
    public string Name;

    public Color Color;
    public BasisShareableKind ShareableKind;

    /// <summary>
    /// Reads a short label out of a payload for the orb and the Library row, or null when the
    /// payload says nothing worth showing. Never trusted: this runs on a string another client
    /// wrote, so it must not throw.
    /// </summary>
    public Func<string, string> Describe;

    /// <summary>
    /// Takes the payload into the local player's own saved list. Returns the name it was stored
    /// under, or null when it was refused. Also runs on another client's string.
    /// </summary>
    public Func<string, string> Accept;
}

/// <summary>
/// Share kinds that carry their content inline. Content spheres are framework-side and the packages
/// that own these payloads sit above the framework, so they register here on load rather than being
/// referenced: the same inversion <see cref="BasisShareableRegistry"/> uses for the Library.
/// </summary>
public static class BasisContentSharePayloadRegistry
{
    private static readonly Dictionary<ContentShareType, BasisContentSharePayloadKind> Kinds =
        new Dictionary<ContentShareType, BasisContentSharePayloadKind>();

    public static void Register(BasisContentSharePayloadKind kind)
    {
        if (kind == null) return;
        if (!ContentSharePayload.IsPayloadType(kind.Type))
        {
            BasisDebug.LogError($"Content share type {kind.Type} does not carry an inline payload.", BasisDebug.LogTag.Networking);
            return;
        }
        Kinds[kind.Type] = kind;
    }

    public static void Unregister(ContentShareType type) => Kinds.Remove(type);

    public static bool TryGet(ContentShareType type, out BasisContentSharePayloadKind kind) =>
        Kinds.TryGetValue(type, out kind);

    /// <summary>
    /// Whether this build carries the payload inline. A type can be a payload type on the wire
    /// (<see cref="ContentSharePayload.IsPayloadType"/>) while no package here handles it, which is
    /// what a client missing the owning package looks like.
    /// </summary>
    public static bool IsHandled(ContentShareType type) => Kinds.ContainsKey(type);

    /// <summary>
    /// The label for a received payload, guarded: <see cref="BasisContentSharePayloadKind.Describe"/>
    /// is handed a string from the network and a throw there would take the orb down with it.
    /// </summary>
    public static string Describe(ContentShareType type, string payload)
    {
        if (!TryGet(type, out BasisContentSharePayloadKind kind) || kind.Describe == null) return null;

        try
        {
            return kind.Describe(payload);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Content share payload of type {type} could not be described: {ex.Message}", BasisDebug.LogTag.Networking);
            return null;
        }
    }

    /// <summary>Same guard for the save path. Returns the stored name, or null when nothing was saved.</summary>
    public static string Accept(ContentShareType type, string payload)
    {
        if (!TryGet(type, out BasisContentSharePayloadKind kind) || kind.Accept == null) return null;

        try
        {
            return kind.Accept(payload);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Content share payload of type {type} could not be saved: {ex.Message}", BasisDebug.LogTag.Networking);
            return null;
        }
    }
}
