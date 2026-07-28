using Basis.Network.Core;

public static partial class SerializableBasis
{
    public struct ClientAvatarChangeMessage
    {
        // Downloading - attempts to download from a URL, make sure a hash also exists.
        // BuiltIn - loads as an addressable in Unity.
        public byte loadMode;
        public byte[] byteArray;
        //we increment this and then wrap around when > 255
        public byte LocalAvatarIndex;

        // ── Body fit (per-segment stretch/collapse; see Basis.IK.BasisBodyFitCore) ──
        // The wearer resizes their own avatar's arm, leg and spine segments to match their real
        // proportions. Bone ROTATIONS on the pose channel are avatar-agnostic, but segment LENGTHS are
        // not sent there at all, so without these a remote renders the wearer at the avatar's authored
        // proportions while the wearer sees themselves fitted. It rides the avatar-change record rather
        // than the per-frame pose because it only changes on calibration, avatar swap or a settings
        // toggle — and because the server already stores that record and replays it to late joiners.
        // 1 = no deformation.
        public float ArmScale;
        public float LegScale;
        public float TorsoScale;

        // Every fit scale is bounded by construction: BasisBodyFitCore clamps to 1 +/- maxDeviation and
        // caps maxDeviation at MaxDeviationCeiling = 0.5, so all three live in [0.5, 1.5]. Quantizing
        // over exactly that range means the wire cannot represent an invalid scale at all — no clamping
        // on read, no hostile value reaching a skeleton — and costs 2 bytes instead of 4 per scale.
        // 16 bits over a range of 1.0 is a 1.5e-5 step: 0.013 mm on a 0.84 m leg span, i.e. lossless in
        // practice. KEEP THESE BOUNDS IN STEP WITH BasisBodyFitCore.MaxDeviationCeiling.
        public const float FitScaleMin = 0.5f;
        public const float FitScaleMax = 1.5f;
        static readonly BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData FitScaleRange =
            new BasisNetworkPrimitiveCompression.BasisRangedUshortFloatData(FitScaleMin, FitScaleMax, 1f / 65535f);

        /// <summary>
        /// Normalises a scale before it is quantized. A default-constructed message carries 0, which is
        /// what makes "unset" mean "no deformation" — and it must be mapped to 1 BEFORE compression,
        /// because the quantizer would otherwise clamp 0 to FitScaleMin and halve every fitted bone.
        /// </summary>
        public static float SanitizeFitScale(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            {
                return 1f;
            }
            return value < FitScaleMin ? FitScaleMin : (value > FitScaleMax ? FitScaleMax : value);
        }

        /// <summary>Quantizes a fit scale to its 2-byte wire form.</summary>
        public static ushort CompressFitScale(float value) => FitScaleRange.Compress(SanitizeFitScale(value));

        /// <summary>Rebuilds a fit scale from the wire. The result is always within the valid band.</summary>
        public static float DecompressFitScale(ushort value) => FitScaleRange.Decompress(value);

        public void SanitizeFit()
        {
            ArmScale = SanitizeFitScale(ArmScale);
            LegScale = SanitizeFitScale(LegScale);
            TorsoScale = SanitizeFitScale(TorsoScale);
        }

        public void Deserialize(NetDataReader Writer)
        {
            // Read the load mode
            loadMode = Writer.GetByte();
            // Initialize the byte array with the specified length
            ushort Length = Writer.GetUShort();
            if (Length == 0)
            {
                byteArray = null;
            }
            else
            {
                if (Length > Writer.AvailableBytes)
                {
                    byteArray = null;
                    throw new System.ArgumentException($"Avatar change length {Length} exceeds available data ({Writer.AvailableBytes} bytes).");
                }
                if (byteArray == null || byteArray.Length != Length)
                {
                    byteArray = new byte[Length];
                }

                // Read each byte manually into the array
                Writer.GetBytes(byteArray, 0, byteArray.Length);
            }
            LocalAvatarIndex = Writer.GetByte();
            // Quantized to the valid band, so nothing here can be out of range — no sanitize needed.
            ArmScale = DecompressFitScale(Writer.GetUShort());
            LegScale = DecompressFitScale(Writer.GetUShort());
            TorsoScale = DecompressFitScale(Writer.GetUShort());
        }
        public void Serialize(NetDataWriter Writer)
        {
            // Write the load mode
            Writer.Put(loadMode);
            if (byteArray == null)
            {
                Writer.Put((ushort)0);
            }
            else
            {
                Writer.Put((ushort)byteArray.Length);
                Writer.Put(byteArray);
            }
            Writer.Put(LocalAvatarIndex);
            // CompressFitScale sanitizes first, so a record built without touching the fit fields (which
            // is most construction sites) puts identity on the wire rather than zero.
            Writer.Put(CompressFitScale(ArmScale));
            Writer.Put(CompressFitScale(LegScale));
            Writer.Put(CompressFitScale(TorsoScale));
        }
    }

    /// <summary>
    /// A body-fit change with no avatar change behind it — the common case, since recalibrating or
    /// nudging the fit sliders resizes segments without touching which avatar is worn. Sent on
    /// AvatarChangeMessageChannel under AvatarChangeKind.BodyFit; the server merges it into the stored
    /// ClientAvatarChangeMessage so late joiners still receive it as part of one record. Kept separate
    /// from the full message so this path never re-sends the avatar bytes and, critically, so receivers
    /// never run their avatar-reload path for what is only a proportion tweak.
    /// </summary>
    public struct ClientBodyFitMessage
    {
        public float ArmScale;
        public float LegScale;
        public float TorsoScale;

        public void Deserialize(NetDataReader Reader)
        {
            ArmScale = ClientAvatarChangeMessage.DecompressFitScale(Reader.GetUShort());
            LegScale = ClientAvatarChangeMessage.DecompressFitScale(Reader.GetUShort());
            TorsoScale = ClientAvatarChangeMessage.DecompressFitScale(Reader.GetUShort());
        }

        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(ClientAvatarChangeMessage.CompressFitScale(ArmScale));
            Writer.Put(ClientAvatarChangeMessage.CompressFitScale(LegScale));
            Writer.Put(ClientAvatarChangeMessage.CompressFitScale(TorsoScale));
        }
    }

    /// <summary>
    /// Server-&gt;client form of <see cref="ClientBodyFitMessage"/>, stamped with whose fit changed.
    /// </summary>
    public struct ServerBodyFitMessage
    {
        public PlayerIdMessage uShortPlayerId;
        public ClientBodyFitMessage bodyFit;

        public void Deserialize(NetDataReader Reader)
        {
            uShortPlayerId.Deserialize(Reader);
            bodyFit.Deserialize(Reader);
        }

        public void Serialize(NetDataWriter Writer)
        {
            uShortPlayerId.Serialize(Writer);
            bodyFit.Serialize(Writer);
        }
    }
}
