using System;

namespace Basis.Network.Core
{
    public static class BasisScreenShareProtocol
    {
        public const string ControlMessage = "basis.screenshare.control";
        public const string MediaMessage = "basis.screenshare.media";
        public const DeliveryMethod ControlDelivery = DeliveryMethod.ReliableOrdered;
        public const DeliveryMethod MediaDelivery = DeliveryMethod.ReliableOrdered;
        public const int MaxChunkPayload = 1100;
        public const int MaxFrameBytes = 4 * 1024 * 1024;
        public const int MaxChunksPerFrame = (MaxFrameBytes + MaxChunkPayload - 1) / MaxChunkPayload;
        public const int MaxTitleLength = 64;
        public const int MaxDimension = 4096;

        public const byte StartSub = 0;
        public const byte StopSub = 1;
        public const byte PoseSub = 2;
        public const byte WatchSub = 3;
        public const byte KeyframeSub = 4;
        public const byte InfoSub = 5;
        public const byte LockSub = 6;
        public const byte MoveSub = 7;
        public const byte AcceptedSub = 10;
        public const byte RefusedSub = 11;
        public const byte AnnounceSub = 12;
        public const byte EndedSub = 13;
        public const byte ViewersSub = 16;
        public const byte PolicySub = 17;

        public const byte RefusedNoPermission = 1;
        public const byte RefusedTooMany = 2;
        public const byte RefusedInvalid = 3;
        public const byte RefusedLocked = 4;

        public const byte KindVideo = 0;
        public const byte KindAudio = 1;
        public const byte FlagKey = 1;

        public struct StreamInfo
        {
            public ushort Width, Height;
            public byte Fps;
            public bool HasAudio;
            public string Title;

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(Width);
                writer.Put(Height);
                writer.Put(Fps);
                writer.Put(HasAudio);
                writer.Put(Title ?? string.Empty, MaxTitleLength);
            }

            public bool TryDeserialize(NetDataReader reader)
            {
                if (!reader.TryGetUShort(out Width) || !reader.TryGetUShort(out Height) || !reader.TryGetByte(out Fps) || !reader.TryGetBool(out HasAudio) || !reader.TryGetString(out Title)) return false;
                if (Title.Length > MaxTitleLength) Title = Title.Substring(0, MaxTitleLength);
                return IsValid;
            }

            public bool IsValid => Width >= 16 && Height >= 16 && Width <= MaxDimension && Height <= MaxDimension && Fps >= 1 && Fps <= 120;
        }

        public struct ScreenPose
        {
            public float PX, PY, PZ, RX, RY, RZ, RW, Scale;

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(PX);
                writer.Put(PY);
                writer.Put(PZ);
                writer.Put(RX);
                writer.Put(RY);
                writer.Put(RZ);
                writer.Put(RW);
                writer.Put(Scale);
            }

            public bool TryDeserialize(NetDataReader reader)
            {
                if (!reader.TryGetFloat(out PX) || !reader.TryGetFloat(out PY) || !reader.TryGetFloat(out PZ) || !reader.TryGetFloat(out RX) || !reader.TryGetFloat(out RY) || !reader.TryGetFloat(out RZ) || !reader.TryGetFloat(out RW) || !reader.TryGetFloat(out Scale)) return false;
                return IsValid;
            }

            public bool IsValid
            {
                get
                {
                    if (!Finite(PX) || !Finite(PY) || !Finite(PZ) || !Finite(RX) || !Finite(RY) || !Finite(RZ) || !Finite(RW) || !Finite(Scale)) return false;
                    if (Math.Abs(PX) > 100000f || Math.Abs(PY) > 100000f || Math.Abs(PZ) > 100000f) return false;
                    float len = RX * RX + RY * RY + RZ * RZ + RW * RW;
                    return len > 0.5f && len < 1.5f && Scale >= 0.05f && Scale <= 20f;
                }
            }

            static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public struct ChunkHeader
        {
            public uint Sequence;
            public byte Kind, Flags;
            public ushort Index, Count;
            public long PtsUs;
            public int TotalLength;

            public bool IsKey => (Flags & FlagKey) != 0;
            public bool IsFirst => Index == 0;

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(Sequence);
                writer.Put(Kind);
                writer.Put(Flags);
                writer.Put(Index);
                writer.Put(Count);
                if (Index == 0)
                {
                    writer.Put(PtsUs);
                    writer.Put(TotalLength);
                }
            }

            public bool TryDeserialize(NetDataReader reader)
            {
                if (!reader.TryGetUInt(out Sequence) || !reader.TryGetByte(out Kind) || !reader.TryGetByte(out Flags) || !reader.TryGetUShort(out Index) || !reader.TryGetUShort(out Count)) return false;
                if (Kind > KindAudio || Count == 0 || Count > MaxChunksPerFrame || Index >= Count) return false;
                if (Index == 0)
                {
                    if (!reader.TryGetLong(out PtsUs) || !reader.TryGetInt(out TotalLength)) return false;
                    if (TotalLength <= 0 || TotalLength > MaxFrameBytes || (TotalLength + MaxChunkPayload - 1) / MaxChunkPayload != Count) return false;
                }
                return reader.AvailableBytes > 0 && reader.AvailableBytes <= MaxChunkPayload;
            }
        }

        public static int ChunkCount(int length) => (length + MaxChunkPayload - 1) / MaxChunkPayload;
    }
}
