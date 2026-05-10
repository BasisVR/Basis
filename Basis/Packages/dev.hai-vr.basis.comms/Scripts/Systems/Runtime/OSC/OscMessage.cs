using System;
using HVR.Basis.Comms.OSC.Lyuma;

namespace HVR.Basis.Comms.OSC
{
    public sealed class OscMessage
    {
        private const string AvatarParametersPrefix = "/avatar/parameters/";

        public string Path { get; private set; }
        public string NormalizedPath { get; private set; }
        public string TypeTag { get; private set; }
        public OscData[] Arguments { get; private set; } = Array.Empty<OscData>();
        public string SenderAddress { get; private set; }
        public int SenderPort { get; private set; }
        public uint BundleId { get; private set; }
        public bool HasTimeTag { get; private set; }
        public uint TimeTagSeconds { get; private set; }
        public uint TimeTagNanoseconds { get; private set; }

        public static OscMessage FromRaw(SimpleOSC.OSCMessage message)
        {
            string path = message.path ?? string.Empty;
            string normalizedPath = path.StartsWith(AvatarParametersPrefix, StringComparison.Ordinal)
                ? path.Substring(AvatarParametersPrefix.Length)
                : path;

            return new OscMessage
            {
                Path = path,
                NormalizedPath = normalizedPath,
                TypeTag = message.typeTag ?? string.Empty,
                Arguments = OscData.ConvertArguments(message.arguments) ?? Array.Empty<OscData>(),
                SenderAddress = message.sender?.Address?.ToString() ?? string.Empty,
                SenderPort = message.sender?.Port ?? 0,
                BundleId = message.bundleId,
                HasTimeTag = message.time.secs != 0 || message.time.nsecs != 0,
                TimeTagSeconds = message.time.secs,
                TimeTagNanoseconds = message.time.nsecs,
            };
        }
    }
}
