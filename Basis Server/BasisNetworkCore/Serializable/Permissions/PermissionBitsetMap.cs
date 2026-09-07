using System;
using System.Collections.Generic;

namespace Basis.Network.Core
{
    /// <summary>
    /// Maps well-known permission nodes to bit indices for compact bitset serialization.
    /// APPEND ONLY — never reorder or remove entries, or you break wire compatibility.
    /// Values mirror PermNodes constants from the server package.
    /// </summary>
    public static class PermissionBitsetMap
    {
        private static readonly string[] IndexToNode =
        {
            "*",                              // 0
            "basis.server.stats",             // 1
            "basis.resource.load.world",      // 2
            "basis.resource.unload.world",    // 3
            "basis.resource.load.prop",       // 4
            "basis.resource.unload.prop",     // 5
            "basis.resource.load.avatar",     // 6
            "basis.resource.unload.avatar",   // 7
            "basis.ownership.transfer",       // 8
            "basis.ownership.remove",         // 9
            "basis.ownership.get",            // 10
            "basis.contentshare.delete",      // 11
            "basis.contentshare.create",      // 12
            "basis.protection",               // 13
            "basis.configuration",            // 14
            "basis.moderation",               // 15
            "basis.moderation.ban",           // 16
            "basis.moderation.kick",          // 17
            "basis.moderation.ipban",         // 18
            "basis.moderation.unban",         // 19
            "basis.moderation.unbanip",       // 20
            "basis.moderation.message",       // 21
            "basis.moderation.messageall",    // 22
            "basis.moderation.teleport",      // 23
            "basis.moderation.announce",      // 24
            "basis.permissions.view",         // 25
            "basis.permissions.edit",         // 26
            "basis.moderation.headlessaudio", // 27
        };

        private static readonly Dictionary<string, int> NodeToIndex;

        static PermissionBitsetMap()
        {
            NodeToIndex = new Dictionary<string, int>(IndexToNode.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < IndexToNode.Length; i++)
            {
                NodeToIndex[IndexToNode[i]] = i;
            }
        }

        public static int KnownCount => IndexToNode.Length;

        /// <summary>Minimum bytes needed to represent all known nodes as bits.</summary>
        public static int ByteCount => (IndexToNode.Length + 7) >> 3;

        /// <summary>
        /// Splits allowed permission nodes into a compact bitset (known nodes)
        /// and a string array (unknown/dynamic nodes that don't have a bit index).
        /// When "*" (wildcard) is in allowedNodes, all known bits are set.
        /// Any nodes in deniedNodes are cleared from the bitset.
        /// </summary>
        public static void Encode(IReadOnlyCollection<string> allowedNodes, out byte[] bitset, out string[] extras,
                                   IReadOnlyCollection<string> deniedNodes = null)
        {
            bitset = new byte[ByteCount];
            List<string> extraList = null;
            bool hasWildcard = false;

            foreach (string node in allowedNodes)
            {
                if (string.Equals(node, "*", StringComparison.Ordinal))
                {
                    hasWildcard = true;
                }

                if (NodeToIndex.TryGetValue(node, out int idx))
                {
                    bitset[idx >> 3] |= (byte)(1 << (idx & 7));
                }
                else
                {
                    if (extraList == null)
                    {
                        extraList = new List<string>();
                    }
                    extraList.Add(node);
                }
            }

            // Wildcard: set every known permission bit so the client sees all nodes explicitly
            if (hasWildcard)
            {
                for (int i = 0; i < KnownCount; i++)
                {
                    bitset[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            // Clear any explicitly denied nodes
            if (deniedNodes != null)
            {
                foreach (string node in deniedNodes)
                {
                    if (NodeToIndex.TryGetValue(node, out int idx))
                    {
                        bitset[idx >> 3] &= (byte)~(1 << (idx & 7));
                    }
                }
            }

            extras = extraList != null ? extraList.ToArray() : Array.Empty<string>();
        }

        /// <summary>
        /// Reconstructs the full set of allowed permission strings from bitset + extras.
        /// </summary>
        public static HashSet<string> Decode(byte[] bitset, string[] extras)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (bitset != null)
            {
                int maxBit = Math.Min(KnownCount, bitset.Length << 3);
                for (int i = 0; i < maxBit; i++)
                {
                    if ((bitset[i >> 3] & (1 << (i & 7))) != 0)
                    {
                        result.Add(IndexToNode[i]);
                    }
                }
            }

            if (extras != null)
            {
                for (int i = 0; i < extras.Length; i++)
                {
                    result.Add(extras[i]);
                }
            }

            return result;
        }
    }
}
