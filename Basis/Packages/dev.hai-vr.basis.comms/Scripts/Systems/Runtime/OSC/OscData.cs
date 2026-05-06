using System;
using HVR.Basis.Comms.OSC.Lyuma;

namespace HVR.Basis.Comms.OSC
{
    public enum OscDataKind
    {
        Boolean,
        Nil,
        Impulse,
        Int32,
        Float32,
        TimeTag,
        String,
        Symbol,
        Blob,
        Color,
        Int64,
        Float64,
        Char,
        Midi,
        Array,
    }

    public sealed class OscData
    {
        public OscDataKind Kind { get; private set; }
        public bool BoolValue { get; private set; }
        public int IntValue { get; private set; }
        public long LongValue { get; private set; }
        public uint UIntValue { get; private set; }
        public float FloatValue { get; private set; }
        public double DoubleValue { get; private set; }
        public string StringValue { get; private set; }
        public byte[] BlobValue { get; private set; }
        public OscData[] Elements { get; private set; } = Array.Empty<OscData>();
        public uint TimeTagSeconds { get; private set; }
        public uint TimeTagNanoseconds { get; private set; }
        public byte ColorR { get; private set; }
        public byte ColorG { get; private set; }
        public byte ColorB { get; private set; }
        public byte ColorA { get; private set; }
        public byte MidiPort { get; private set; }
        public byte MidiStatus { get; private set; }
        public byte MidiData1 { get; private set; }
        public byte MidiData2 { get; private set; }

        public static OscData Boolean(bool value) => new OscData
        {
            Kind = OscDataKind.Boolean,
            BoolValue = value,
        };

        public static OscData Nil() => new OscData
        {
            Kind = OscDataKind.Nil,
        };

        public static OscData Impulse() => new OscData
        {
            Kind = OscDataKind.Impulse,
        };

        public static OscData Int32(int value) => new OscData
        {
            Kind = OscDataKind.Int32,
            IntValue = value,
        };

        public static OscData Float32(float value) => new OscData
        {
            Kind = OscDataKind.Float32,
            FloatValue = value,
        };

        public static OscData TimeTag(uint seconds, uint nanoseconds) => new OscData
        {
            Kind = OscDataKind.TimeTag,
            TimeTagSeconds = seconds,
            TimeTagNanoseconds = nanoseconds,
        };

        public static OscData String(string value) => new OscData
        {
            Kind = OscDataKind.String,
            StringValue = value ?? string.Empty,
        };

        public static OscData Symbol(string value) => new OscData
        {
            Kind = OscDataKind.Symbol,
            StringValue = value ?? string.Empty,
        };

        public static OscData Blob(byte[] value) => new OscData
        {
            Kind = OscDataKind.Blob,
            BlobValue = value == null ? Array.Empty<byte>() : (byte[])value.Clone(),
        };

        public static OscData Color(byte r, byte g, byte b, byte a) => new OscData
        {
            Kind = OscDataKind.Color,
            ColorR = r,
            ColorG = g,
            ColorB = b,
            ColorA = a,
        };

        public static OscData Int64(long value) => new OscData
        {
            Kind = OscDataKind.Int64,
            LongValue = value,
        };

        public static OscData Float64(double value) => new OscData
        {
            Kind = OscDataKind.Float64,
            DoubleValue = value,
        };

        public static OscData Char(uint value) => new OscData
        {
            Kind = OscDataKind.Char,
            UIntValue = value,
        };

        public static OscData Midi(byte port, byte status, byte data1, byte data2) => new OscData
        {
            Kind = OscDataKind.Midi,
            MidiPort = port,
            MidiStatus = status,
            MidiData1 = data1,
            MidiData2 = data2,
        };

        public static OscData ArrayValue(params OscData[] elements) => new OscData
        {
            Kind = OscDataKind.Array,
            Elements = elements == null ? Array.Empty<OscData>() : (OscData[])elements.Clone(),
        };

        public static OscData[] ConvertArguments(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0)
            {
                return Array.Empty<OscData>();
            }

            int argumentCount = arguments.Length;
            OscData[] converted = new OscData[argumentCount];
            for (int i = 0; i < argumentCount; i++)
            {
                converted[i] = FromRaw(arguments[i]);
            }

            return converted;
        }

        public static OscData FromRaw(object raw)
        {
            switch (raw)
            {
                case null:
                    return Nil();
                case bool boolValue:
                    return Boolean(boolValue);
                case int intValue:
                    return Int32(intValue);
                case float floatValue:
                    return Float32(floatValue);
                case SimpleOSC.TimeTag timeTag:
                    return TimeTag(timeTag.secs, timeTag.nsecs);
                case string stringValue:
                    return String(stringValue);
                case SimpleOSC.OSCSymbol symbolValue:
                    return Symbol(symbolValue.value);
                case byte[] blobValue:
                    return Blob(blobValue);
                case SimpleOSC.OSCColor colorValue:
                    return Color(colorValue.r, colorValue.g, colorValue.b, colorValue.a);
                case long longValue:
                    return Int64(longValue);
                case double doubleValue:
                    return Float64(doubleValue);
                case uint charValue:
                    return Char(charValue);
                case SimpleOSC.OSCMidi midiValue:
                    return Midi(midiValue.port, midiValue.status, midiValue.data1, midiValue.data2);
                case object[] elements:
                    return ArrayValue(ConvertArguments(elements));
                case SimpleOSC.Impulse impulseValue:
                    return Impulse();
                default:
                    throw new NotSupportedException($"Unsupported OSC raw data type: {raw.GetType().FullName}");
            }
        }

        internal char GetTypeTagChar()
        {
            switch (Kind)
            {
                case OscDataKind.Boolean:
                    return BoolValue ? 'T' : 'F';
                case OscDataKind.Nil:
                    return 'N';
                case OscDataKind.Impulse:
                    return 'I';
                case OscDataKind.Int32:
                    return 'i';
                case OscDataKind.Float32:
                    return 'f';
                case OscDataKind.TimeTag:
                    return 't';
                case OscDataKind.String:
                    return 's';
                case OscDataKind.Symbol:
                    return 'S';
                case OscDataKind.Blob:
                    return 'b';
                case OscDataKind.Color:
                    return 'r';
                case OscDataKind.Int64:
                    return 'h';
                case OscDataKind.Float64:
                    return 'd';
                case OscDataKind.Char:
                    return 'c';
                case OscDataKind.Midi:
                    return 'm';
                case OscDataKind.Array:
                    return '[';
                default:
                    throw new NotSupportedException($"Unsupported OSC data kind: {Kind}");
            }
        }

        internal object ToQueryValue()
        {
            switch (Kind)
            {
                case OscDataKind.Boolean:
                    return BoolValue;
                case OscDataKind.Nil:
                    return null;
                case OscDataKind.Impulse:
                    return "impulse";
                case OscDataKind.Int32:
                    return IntValue;
                case OscDataKind.Float32:
                    return FloatValue;
                case OscDataKind.TimeTag:
                    return new[] { TimeTagSeconds, TimeTagNanoseconds };
                case OscDataKind.String:
                case OscDataKind.Symbol:
                    return StringValue ?? string.Empty;
                case OscDataKind.Blob:
                    return Array.ConvertAll(BlobValue ?? Array.Empty<byte>(), b => (int)b);
                case OscDataKind.Color:
                    return new[] { (int)ColorR, (int)ColorG, (int)ColorB, (int)ColorA };
                case OscDataKind.Int64:
                    return LongValue;
                case OscDataKind.Float64:
                    return DoubleValue;
                case OscDataKind.Char:
                    return UIntValue;
                case OscDataKind.Midi:
                    return new[] { (int)MidiPort, (int)MidiStatus, (int)MidiData1, (int)MidiData2 };
                case OscDataKind.Array:
                    int elementCount = Elements?.Length ?? 0;
                    object[] nested = new object[elementCount];
                    for (int i = 0; i < elementCount; i++)
                    {
                        OscData element = Elements[i];
                        nested[i] = element != null ? element.ToQueryValue() : null;
                    }
                    return nested;
                default:
                    throw new NotSupportedException($"Unsupported OSC data kind: {Kind}");
            }
        }

        internal object ToOscArgument()
        {
            switch (Kind)
            {
                case OscDataKind.Boolean:
                    return BoolValue;
                case OscDataKind.Nil:
                    return null;
                case OscDataKind.Impulse:
                    return SimpleOSC.Impulse.IMPULSE;
                case OscDataKind.Int32:
                    return IntValue;
                case OscDataKind.Float32:
                    return FloatValue;
                case OscDataKind.TimeTag:
                    return new SimpleOSC.TimeTag
                    {
                        secs = TimeTagSeconds,
                        nsecs = TimeTagNanoseconds,
                    };
                case OscDataKind.String:
                    return StringValue ?? string.Empty;
                case OscDataKind.Symbol:
                    return new SimpleOSC.OSCSymbol
                    {
                        value = StringValue ?? string.Empty,
                    };
                case OscDataKind.Blob:
                    return BlobValue != null ? (byte[])BlobValue.Clone() : Array.Empty<byte>();
                case OscDataKind.Color:
                    return new SimpleOSC.OSCColor
                    {
                        r = ColorR,
                        g = ColorG,
                        b = ColorB,
                        a = ColorA,
                    };
                case OscDataKind.Int64:
                    return LongValue;
                case OscDataKind.Float64:
                    return DoubleValue;
                case OscDataKind.Char:
                    return UIntValue;
                case OscDataKind.Midi:
                    return new SimpleOSC.OSCMidi
                    {
                        port = MidiPort,
                        status = MidiStatus,
                        data1 = MidiData1,
                        data2 = MidiData2,
                    };
                case OscDataKind.Array:
                    int elementCount = Elements?.Length ?? 0;
                    object[] nested = new object[elementCount];
                    for (int i = 0; i < elementCount; i++)
                    {
                        OscData element = Elements[i];
                        nested[i] = element != null ? element.ToOscArgument() : null;
                    }
                    return nested;
                default:
                    throw new NotSupportedException($"Unsupported OSC data kind: {Kind}");
            }
        }
    }
}
