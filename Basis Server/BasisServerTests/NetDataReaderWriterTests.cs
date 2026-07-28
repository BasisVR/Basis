using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Round-trip and accounting tests for the vendored LiteNetLib NetDataWriter/NetDataReader pair
/// (Basis.Network.Core). Covers every Put/Get overload present in the source, TryGet/Peek
/// semantics, position bookkeeping, writer growth/reset, and the ushort length-prefix cap.
/// </summary>
public class NetDataReaderWriterTests
{
    private static NetDataReader ReaderOver(NetDataWriter w) => new NetDataReader(w.CopyData());

    [Fact]
    public void Primitives_RoundTrip_IncludingExtremes()
    {
        var w = new NetDataWriter();
        w.Put(true);
        w.Put(false);
        w.Put((byte)0);
        w.Put((byte)255);
        w.Put((byte)0x5A);
        w.Put(sbyte.MinValue);
        w.Put(sbyte.MaxValue);
        w.Put((sbyte)-1);
        w.Put(short.MinValue);
        w.Put(short.MaxValue);
        w.Put((short)-12345);
        w.Put(ushort.MinValue);
        w.Put(ushort.MaxValue);
        w.Put((ushort)0xBEEF);
        w.Put(int.MinValue);
        w.Put(int.MaxValue);
        w.Put(-123456789);
        w.Put(uint.MinValue);
        w.Put(uint.MaxValue);
        w.Put(0xDEADBEEFu);
        w.Put(long.MinValue);
        w.Put(long.MaxValue);
        w.Put(-1234567890123456789L);
        w.Put(ulong.MinValue);
        w.Put(ulong.MaxValue);
        w.Put(0x0123456789ABCDEFul);
        w.Put('\0');
        w.Put('A');
        w.Put('好');
        w.Put('\uD800');
        var guid = new Guid("11223344-5566-7788-99aa-bbccddeeff00");
        w.Put(guid);

        var r = ReaderOver(w);
        Assert.True(r.GetBool());
        Assert.False(r.GetBool());
        Assert.Equal((byte)0, r.GetByte());
        Assert.Equal((byte)255, r.GetByte());
        Assert.Equal((byte)0x5A, r.GetByte());
        Assert.Equal(sbyte.MinValue, r.GetSByte());
        Assert.Equal(sbyte.MaxValue, r.GetSByte());
        Assert.Equal((sbyte)-1, r.GetSByte());
        Assert.Equal(short.MinValue, r.GetShort());
        Assert.Equal(short.MaxValue, r.GetShort());
        Assert.Equal((short)-12345, r.GetShort());
        Assert.Equal(ushort.MinValue, r.GetUShort());
        Assert.Equal(ushort.MaxValue, r.GetUShort());
        Assert.Equal((ushort)0xBEEF, r.GetUShort());
        Assert.Equal(int.MinValue, r.GetInt());
        Assert.Equal(int.MaxValue, r.GetInt());
        Assert.Equal(-123456789, r.GetInt());
        Assert.Equal(uint.MinValue, r.GetUInt());
        Assert.Equal(uint.MaxValue, r.GetUInt());
        Assert.Equal(0xDEADBEEFu, r.GetUInt());
        Assert.Equal(long.MinValue, r.GetLong());
        Assert.Equal(long.MaxValue, r.GetLong());
        Assert.Equal(-1234567890123456789L, r.GetLong());
        Assert.Equal(ulong.MinValue, r.GetULong());
        Assert.Equal(ulong.MaxValue, r.GetULong());
        Assert.Equal(0x0123456789ABCDEFul, r.GetULong());
        Assert.Equal('\0', r.GetChar());
        Assert.Equal('A', r.GetChar());
        Assert.Equal('好', r.GetChar());
        Assert.Equal('\uD800', r.GetChar());
        Assert.Equal(guid, r.GetGuid());
        Assert.True(r.EndOfData);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(-123.456f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Float_RoundTrips_BitExact(float value)
    {
        var w = new NetDataWriter();
        w.Put(value);
        var r = ReaderOver(w);
        Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(r.GetFloat()));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(2.718281828459045)]
    [InlineData(-98765.4321)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Double_RoundTrips_BitExact(double value)
    {
        var w = new NetDataWriter();
        w.Put(value);
        var r = ReaderOver(w);
        Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(r.GetDouble()));
    }

    [Fact]
    public void NegativeZero_RoundTrips_BitExact()
    {
        var w = new NetDataWriter();
        w.Put(-0f);
        w.Put(-0d);
        var r = ReaderOver(w);
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(r.GetFloat()));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(r.GetDouble()));
    }

    [Fact]
    public void GetOutOverloads_RoundTrip()
    {
        var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var w = new NetDataWriter();
        w.Put((byte)7);
        w.Put((sbyte)-8);
        w.Put(true);
        w.Put('Z');
        w.Put((ushort)900);
        w.Put((short)-901);
        w.Put(902ul);
        w.Put(-903L);
        w.Put(904u);
        w.Put(-905);
        w.Put(906.5);
        w.Put(907.25f);
        w.Put("hello");
        w.Put("world");
        w.Put(guid);

        var r = ReaderOver(w);
        r.Get(out byte b);
        r.Get(out sbyte sb);
        r.Get(out bool flag);
        r.Get(out char c);
        r.Get(out ushort us);
        r.Get(out short s);
        r.Get(out ulong ul);
        r.Get(out long l);
        r.Get(out uint ui);
        r.Get(out int i);
        r.Get(out double d);
        r.Get(out float f);
        r.Get(out string str);
        r.Get(out string strLimited, 10);
        r.Get(out Guid g);

        Assert.Equal((byte)7, b);
        Assert.Equal((sbyte)-8, sb);
        Assert.True(flag);
        Assert.Equal('Z', c);
        Assert.Equal((ushort)900, us);
        Assert.Equal((short)-901, s);
        Assert.Equal(902ul, ul);
        Assert.Equal(-903L, l);
        Assert.Equal(904u, ui);
        Assert.Equal(-905, i);
        Assert.Equal(906.5, d);
        Assert.Equal(907.25f, f);
        Assert.Equal("hello", str);
        Assert.Equal("world", strLimited);
        Assert.Equal(guid, g);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ascii only")]
    [InlineData("héllo wörld")]
    [InlineData("世界こんにちは")]
    [InlineData("emoji 🎈 pair")]
    public void String_RoundTrips(string value)
    {
        var w = new NetDataWriter();
        w.Put(value);
        var r = ReaderOver(w);
        Assert.Equal(value, r.GetString());
    }

    [Fact]
    public void String_NullAndEmpty_ReadBackAsEmpty()
    {
        var w = new NetDataWriter();
        w.Put((string)null);
        w.Put(string.Empty);
        Assert.Equal(4, w.Length);
        var r = ReaderOver(w);
        Assert.Equal(string.Empty, r.GetString());
        Assert.Equal(string.Empty, r.GetString());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void String_WriterMaxLength_TruncatesByCharCount()
    {
        var w = new NetDataWriter();
        w.Put("abcdefghij", 4);
        var r = ReaderOver(w);
        Assert.Equal("abcd", r.GetString());
    }

    [Fact]
    public void String_ReaderMaxLength_ReturnsEmptyButStaysAligned()
    {
        var w = new NetDataWriter();
        w.Put("abcdefghij");
        w.Put(42);
        var r = ReaderOver(w);
        Assert.Equal(string.Empty, r.GetString(3));
        Assert.Equal(42, r.GetInt());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void LargeString_RoundTrips_IncludingEmptyAndUnicode()
    {
        string big = string.Concat(Enumerable.Range(0, 300).Select(i => (char)('a' + i % 26)));
        string unicode = "世界 🎈 mixed";
        var w = new NetDataWriter();
        w.PutLargeString(big);
        w.PutLargeString(string.Empty);
        w.PutLargeString(null);
        w.PutLargeString(unicode);
        var r = ReaderOver(w);
        Assert.Equal(big, r.GetLargeString());
        Assert.Equal(string.Empty, r.GetLargeString());
        Assert.Equal(string.Empty, r.GetLargeString());
        Assert.Equal(unicode, r.GetLargeString());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void TypedArrays_RoundTrip()
    {
        bool[] bools = { true, false, true, true, false };
        short[] shorts = { short.MinValue, -1, 0, 1, short.MaxValue };
        ushort[] ushorts = { 0, 1, 0x8000, ushort.MaxValue };
        int[] ints = { int.MinValue, -1, 0, 1, int.MaxValue };
        uint[] uints = { 0u, 1u, 0x80000000u, uint.MaxValue };
        long[] longs = { long.MinValue, -1L, 0L, 1L, long.MaxValue };
        ulong[] ulongs = { 0ul, 1ul, 0x8000000000000000ul, ulong.MaxValue };
        float[] floats = { 0f, -1.5f, float.MaxValue, float.Epsilon, float.NaN };
        double[] doubles = { 0d, -2.5, double.MaxValue, double.Epsilon, double.NaN };

        var w = new NetDataWriter();
        w.PutArray(bools);
        w.PutArray(shorts);
        w.PutArray(ushorts);
        w.PutArray(ints);
        w.PutArray(uints);
        w.PutArray(longs);
        w.PutArray(ulongs);
        w.PutArray(floats);
        w.PutArray(doubles);

        var r = ReaderOver(w);
        Assert.Equal(bools, r.GetBoolArray());
        Assert.Equal(shorts, r.GetShortArray());
        Assert.Equal(ushorts, r.GetUShortArray());
        Assert.Equal(ints, r.GetIntArray());
        Assert.Equal(uints, r.GetUIntArray());
        Assert.Equal(longs, r.GetLongArray());
        Assert.Equal(ulongs, r.GetULongArray());
        Assert.Equal(floats, r.GetFloatArray());
        Assert.Equal(doubles, r.GetDoubleArray());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void TypedArrays_EmptyAndNull_ReadBackAsEmpty()
    {
        var w = new NetDataWriter();
        w.PutArray(Array.Empty<int>());
        w.PutArray((int[])null);
        w.PutArray(Array.Empty<double>());
        w.PutArray((string[])null);
        var r = ReaderOver(w);
        Assert.Empty(r.GetIntArray());
        Assert.Empty(r.GetIntArray());
        Assert.Empty(r.GetDoubleArray());
        Assert.Empty(r.GetStringArray());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void StringArrays_RoundTrip_AndPerElementMaxLength()
    {
        string[] values = { "", "one", "二 two", "three 🎈" };
        var w = new NetDataWriter();
        w.PutArray(values);
        w.PutArray(values, 3);
        var r = ReaderOver(w);
        Assert.Equal(values, r.GetStringArray());
        Assert.Equal(new[] { "", "one", "二 t", "thr" }, r.GetStringArray());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void StringArray_ReaderMaxLength_ReplacesOverlongEntriesWithEmpty()
    {
        string[] values = { "ok", "toolongvalue", "yes" };
        var w = new NetDataWriter();
        w.PutArray(values);
        var r = ReaderOver(w);
        Assert.Equal(new[] { "ok", "", "yes" }, r.GetStringArray(5));
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void BytesWithLength_RoundTrip_IncludingZeroLength()
    {
        byte[] payload = { 1, 2, 3, 250, 251, 252 };
        sbyte[] signed = { sbyte.MinValue, -1, 0, 1, sbyte.MaxValue };
        var w = new NetDataWriter();
        w.PutBytesWithLength(payload);
        w.PutBytesWithLength(Array.Empty<byte>());
        w.PutSBytesWithLength(signed);
        w.PutSBytesWithLength(Array.Empty<sbyte>());
        var r = ReaderOver(w);
        Assert.Equal(payload, r.GetBytesWithLength());
        Assert.Empty(r.GetBytesWithLength());
        Assert.Equal(signed, r.GetSBytesWithLength());
        Assert.Empty(r.GetSBytesWithLength());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void PutBytesWithLength_UShortCap_65535RoundTrips()
    {
        var payload = new byte[ushort.MaxValue];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31);
        var w = new NetDataWriter();
        w.PutBytesWithLength(payload);
        Assert.Equal(2 + ushort.MaxValue, w.Length);
        var r = ReaderOver(w);
        Assert.Equal(payload, r.GetBytesWithLength());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void PutBytesWithLength_Above64K_LengthPrefixWrapsToZero()
    {
        // Known design cap: the length prefix is a ushort, so a 65536-byte array wraps to a
        // zero-length record. Pinned so any future widening is a deliberate wire change.
        var payload = new byte[ushort.MaxValue + 1];
        var w = new NetDataWriter();
        w.PutBytesWithLength(payload);
        Assert.Equal(2, w.Length);
        var r = ReaderOver(w);
        Assert.Empty(r.GetBytesWithLength());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void TryGet_OnEmptyReader_ReturnsFalse_WithoutAdvancing()
    {
        var r = new NetDataReader(Array.Empty<byte>());
        Assert.False(r.TryGetByte(out byte b));
        Assert.False(r.TryGetSByte(out sbyte sb));
        Assert.False(r.TryGetBool(out bool flag));
        Assert.False(r.TryGetChar(out char c));
        Assert.False(r.TryGetShort(out short s));
        Assert.False(r.TryGetUShort(out ushort us));
        Assert.False(r.TryGetInt(out int i));
        Assert.False(r.TryGetUInt(out uint ui));
        Assert.False(r.TryGetLong(out long l));
        Assert.False(r.TryGetULong(out ulong ul));
        Assert.False(r.TryGetFloat(out float f));
        Assert.False(r.TryGetDouble(out double d));
        Assert.False(r.TryGetString(out string str));
        Assert.False(r.TryGetStringArray(out string[] strArr));
        Assert.False(r.TryGetBytesWithLength(out byte[] bytes));
        Assert.Equal(0, b);
        Assert.Equal(0, sb);
        Assert.False(flag);
        Assert.Equal('\0', c);
        Assert.Equal(0, s);
        Assert.Equal(0, us);
        Assert.Equal(0, i);
        Assert.Equal(0u, ui);
        Assert.Equal(0L, l);
        Assert.Equal(0ul, ul);
        Assert.Equal(0f, f);
        Assert.Equal(0d, d);
        Assert.Null(str);
        Assert.Null(strArr);
        Assert.Null(bytes);
        Assert.Equal(0, r.Position);
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void TryGet_OnTruncatedFixedWidthValue_ReturnsFalse_WithoutAdvancing()
    {
        var r = new NetDataReader(new byte[] { 0x11, 0x22, 0x33 });
        Assert.False(r.TryGetInt(out _));
        Assert.False(r.TryGetLong(out _));
        Assert.False(r.TryGetFloat(out _));
        Assert.False(r.TryGetDouble(out _));
        Assert.Equal(0, r.Position);
        Assert.True(r.TryGetShort(out short s));
        Assert.Equal(unchecked((short)0x2211), s);
        Assert.Equal(2, r.Position);
        Assert.True(r.TryGetByte(out byte b));
        Assert.Equal((byte)0x33, b);
        Assert.False(r.TryGetByte(out _));
        Assert.Equal(3, r.Position);
    }

    [Fact]
    public void TryGetString_OnTruncatedPayload_ReturnsFalse_WithoutAdvancing()
    {
        var w = new NetDataWriter();
        w.Put("hello");
        byte[] full = w.CopyData();
        var truncated = new byte[full.Length - 2];
        Array.Copy(full, truncated, truncated.Length);
        var r = new NetDataReader(truncated);
        Assert.False(r.TryGetString(out string s));
        Assert.Null(s);
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void TryGetBytesWithLength_OnTruncatedPayload_ReturnsFalse_WithoutAdvancing()
    {
        var w = new NetDataWriter();
        w.PutBytesWithLength(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        byte[] full = w.CopyData();
        var truncated = new byte[7];
        Array.Copy(full, truncated, truncated.Length);
        var r = new NetDataReader(truncated);
        Assert.False(r.TryGetBytesWithLength(out byte[] payload));
        Assert.Null(payload);
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void TryGetStringArray_MissingEntries_ReturnsFalse()
    {
        var w = new NetDataWriter();
        w.Put((ushort)3);
        w.Put("only-one");
        var r = new NetDataReader(w.CopyData());
        Assert.False(r.TryGetStringArray(out string[] arr));
        Assert.Null(arr);
    }

    [Fact]
    public void TryGet_OnSufficientData_ReturnsValues()
    {
        var w = new NetDataWriter();
        w.Put((byte)9);
        w.Put((sbyte)-9);
        w.Put(true);
        w.Put('q');
        w.Put((short)-1000);
        w.Put((ushort)1000);
        w.Put(-100000);
        w.Put(100000u);
        w.Put(-10_000_000_000L);
        w.Put(10_000_000_000ul);
        w.Put(1.25f);
        w.Put(-1.25);
        w.Put("str");
        w.PutArray(new[] { "a", "b" });
        w.PutBytesWithLength(new byte[] { 4, 5 });

        var r = ReaderOver(w);
        Assert.True(r.TryGetByte(out byte b) && b == 9);
        Assert.True(r.TryGetSByte(out sbyte sb) && sb == -9);
        Assert.True(r.TryGetBool(out bool flag) && flag);
        Assert.True(r.TryGetChar(out char c) && c == 'q');
        Assert.True(r.TryGetShort(out short s) && s == -1000);
        Assert.True(r.TryGetUShort(out ushort us) && us == 1000);
        Assert.True(r.TryGetInt(out int i) && i == -100000);
        Assert.True(r.TryGetUInt(out uint ui) && ui == 100000u);
        Assert.True(r.TryGetLong(out long l) && l == -10_000_000_000L);
        Assert.True(r.TryGetULong(out ulong ul) && ul == 10_000_000_000ul);
        Assert.True(r.TryGetFloat(out float f) && f == 1.25f);
        Assert.True(r.TryGetDouble(out double d) && d == -1.25);
        Assert.True(r.TryGetString(out string str));
        Assert.Equal("str", str);
        Assert.True(r.TryGetStringArray(out string[] strArr));
        Assert.Equal(new[] { "a", "b" }, strArr);
        Assert.True(r.TryGetBytesWithLength(out byte[] bytes));
        Assert.Equal(new byte[] { 4, 5 }, bytes);
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void Peek_DoesNotAdvance_AndMatchesGet()
    {
        var w = new NetDataWriter();
        w.Put((byte)200);
        var r1 = ReaderOver(w);
        Assert.Equal((byte)200, r1.PeekByte());
        Assert.Equal(unchecked((sbyte)200), r1.PeekSByte());
        Assert.False(r1.PeekBool());
        Assert.Equal(0, r1.Position);
        Assert.Equal((byte)200, r1.GetByte());

        var w2 = new NetDataWriter();
        w2.Put((ushort)'x');
        var r2 = ReaderOver(w2);
        Assert.Equal('x', r2.PeekChar());
        Assert.Equal((ushort)'x', r2.PeekUShort());
        Assert.Equal((short)'x', r2.PeekShort());
        Assert.Equal(0, r2.Position);

        var w3 = new NetDataWriter();
        w3.Put(-1234567890123456789L);
        var r3 = ReaderOver(w3);
        Assert.Equal(-1234567890123456789L, r3.PeekLong());
        Assert.Equal(unchecked((ulong)-1234567890123456789L), r3.PeekULong());
        Assert.Equal(0, r3.Position);
        Assert.Equal(-1234567890123456789L, r3.GetLong());

        var w4 = new NetDataWriter();
        w4.Put(-123456789);
        var r4 = ReaderOver(w4);
        Assert.Equal(-123456789, r4.PeekInt());
        Assert.Equal(unchecked((uint)-123456789), r4.PeekUInt());
        Assert.Equal(BitConverter.Int32BitsToSingle(-123456789), r4.PeekFloat());
        Assert.Equal(0, r4.Position);

        var w5 = new NetDataWriter();
        w5.Put(3.5);
        var r5 = ReaderOver(w5);
        Assert.Equal(3.5, r5.PeekDouble());
        Assert.Equal(0, r5.Position);

        var w6 = new NetDataWriter();
        w6.Put("peeked");
        var r6 = ReaderOver(w6);
        Assert.Equal("peeked", r6.PeekString());
        Assert.Equal("peeked", r6.PeekString(0));
        Assert.Equal("peeked", r6.PeekString(10));
        Assert.Equal(string.Empty, r6.PeekString(3));
        Assert.Equal(0, r6.Position);
        Assert.Equal("peeked", r6.GetString());
    }

    [Fact]
    public void PeekString_OnMalformedBuffers_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, new NetDataReader(new byte[] { 0x07 }).PeekString());
        // Prefix claims 4 content bytes that are not present.
        Assert.Equal(string.Empty, new NetDataReader(new byte[] { 0x05, 0x00 }).PeekString());
        Assert.Equal(string.Empty, new NetDataReader(new byte[] { 0x00, 0x00 }).PeekString());
    }

    [Fact]
    public void PositionAccounting_MixedReads_SkipBytes_SetPosition()
    {
        var w = new NetDataWriter();
        w.Put(true);
        w.Put((short)7);
        w.Put(11);
        w.Put(13L);
        w.Put(1.5f);
        byte[] data = w.CopyData();
        Assert.Equal(1 + 2 + 4 + 8 + 4, data.Length);

        var r = new NetDataReader(data);
        Assert.Same(data, r.RawData);
        Assert.False(r.IsNull);
        Assert.Equal(19, r.RawDataSize);
        Assert.Equal(0, r.UserDataOffset);
        Assert.Equal(19, r.UserDataSize);

        Assert.True(r.GetBool());
        Assert.Equal(1, r.Position);
        Assert.Equal(18, r.AvailableBytes);
        Assert.Equal((short)7, r.GetShort());
        Assert.Equal(3, r.Position);
        Assert.Equal(11, r.GetInt());
        Assert.Equal(7, r.Position);
        Assert.Equal(12, r.AvailableBytes);
        r.SkipBytes(8);
        Assert.Equal(15, r.Position);
        Assert.Equal(1.5f, r.GetFloat());
        Assert.True(r.EndOfData);
        Assert.Equal(0, r.AvailableBytes);

        r.SetPosition(3);
        Assert.Equal(11, r.GetInt());
        Assert.Equal(13L, r.GetLong());
    }

    [Fact]
    public void SetSource_WithOffsetWindow_ReportsUserDataAndReadsSlice()
    {
        var payload = new NetDataWriter();
        payload.Put(0x61626364);
        payload.Put((ushort)0x9876);
        byte[] body = payload.CopyData();

        const int prefix = 4;
        const int suffix = 3;
        var full = new byte[prefix + body.Length + suffix];
        for (int i = 0; i < full.Length; i++) full[i] = 0xEE;
        Array.Copy(body, 0, full, prefix, body.Length);

        var r = new NetDataReader(full, prefix, prefix + body.Length);
        Assert.Equal(prefix, r.Position);
        Assert.Equal(prefix, r.UserDataOffset);
        Assert.Equal(body.Length, r.UserDataSize);
        Assert.Equal(prefix + body.Length, r.RawDataSize);
        Assert.Equal(body.Length, r.AvailableBytes);
        Assert.Equal(0x61626364, r.GetInt());
        Assert.Equal((ushort)0x9876, r.GetUShort());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void SetSource_Reuse_ResetsState_AndClearNullsReader()
    {
        var r = new NetDataReader(new byte[] { 1, 0, 0, 0 });
        Assert.Equal(1, r.GetInt());
        Assert.True(r.EndOfData);

        r.SetSource(new byte[] { 2, 0, 0, 0, 9 });
        Assert.Equal(0, r.Position);
        Assert.Equal(0, r.UserDataOffset);
        Assert.Equal(5, r.AvailableBytes);
        Assert.Equal(2, r.GetInt());
        Assert.Equal((byte)9, r.GetByte());

        r.Clear();
        Assert.True(r.IsNull);
        Assert.Equal(0, r.Position);
        Assert.Equal(0, r.RawDataSize);
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void WriterGrowth_LargeDeterministicSequence_ReadsBackIntact()
    {
        var w = new NetDataWriter();
        Assert.Equal(64, w.Capacity);

        const int count = 500;
        for (int i = 0; i < count; i++)
        {
            switch (i % 7)
            {
                case 0: w.Put(unchecked((int)(i * 2654435761u))); break;
                case 1: w.Put(i * 1.618033988749895 - 500.0); break;
                case 2: w.Put(MakeString(i)); break;
                case 3: w.Put(((ulong)i << 32) | 0xDEADBEEFul); break;
                case 4: w.PutBytesWithLength(MakeBytes(i)); break;
                case 5: w.Put((short)(i * 31 - 16000)); break;
                default: w.Put(i % 3 == 0); break;
            }
        }

        Assert.True(w.Length > 64, "sequence should have outgrown the initial capacity");
        Assert.True(w.Capacity >= w.Length);

        var r = ReaderOver(w);
        for (int i = 0; i < count; i++)
        {
            switch (i % 7)
            {
                case 0: Assert.Equal(unchecked((int)(i * 2654435761u)), r.GetInt()); break;
                case 1: Assert.Equal(i * 1.618033988749895 - 500.0, r.GetDouble()); break;
                case 2: Assert.Equal(MakeString(i), r.GetString()); break;
                case 3: Assert.Equal(((ulong)i << 32) | 0xDEADBEEFul, r.GetULong()); break;
                case 4: Assert.Equal(MakeBytes(i), r.GetBytesWithLength()); break;
                case 5: Assert.Equal((short)(i * 31 - 16000), r.GetShort()); break;
                default: Assert.Equal(i % 3 == 0, r.GetBool()); break;
            }
        }
        Assert.True(r.EndOfData);
    }

    private static string MakeString(int i)
    {
        int length = i % 40;
        var chars = new char[length];
        for (int j = 0; j < length; j++)
            chars[j] = (j % 2 == 0) ? (char)('A' + (i + j) % 26) : (char)(0x4E00 + (i + j) % 512);
        return new string(chars);
    }

    private static byte[] MakeBytes(int i)
    {
        var bytes = new byte[i % 33];
        for (int j = 0; j < bytes.Length; j++) bytes[j] = (byte)((i + j * 7) & 0xFF);
        return bytes;
    }

    [Fact]
    public void Writer_Reset_KeepsCapacity_AndAllowsReuse()
    {
        var w = new NetDataWriter();
        w.Put(1234);
        w.Put("payload");
        int capacityAfterWrites = w.Capacity;
        Assert.True(w.Length > 0);

        w.Reset();
        Assert.Equal(0, w.Length);
        Assert.Equal(capacityAfterWrites, w.Capacity);

        w.Put((byte)77);
        var r = ReaderOver(w);
        Assert.Equal((byte)77, r.GetByte());
        Assert.True(r.EndOfData);

        w.Reset(4096);
        Assert.Equal(0, w.Length);
        Assert.True(w.Capacity >= 4096);
    }

    [Fact]
    public void Writer_SetPosition_RewritesEarlierBytes()
    {
        var w = new NetDataWriter();
        int headerPos = w.Length;
        w.Put(0);
        w.Put("body");
        int end = w.SetPosition(headerPos);
        Assert.Equal(headerPos, w.Length);
        w.Put(end);
        int back = w.SetPosition(end);
        Assert.Equal(headerPos + 4, back);
        Assert.Equal(end, w.Length);

        var r = ReaderOver(w);
        Assert.Equal(end, r.GetInt());
        Assert.Equal("body", r.GetString());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void Writer_WithoutAutoResize_ThrowsWhenCapacityExceeded()
    {
        var w = new NetDataWriter(false, 8);
        w.Put(0x1122334455667788L);
        Assert.Equal(8, w.Length);
        Assert.Throws<IndexOutOfRangeException>(() => w.Put((byte)1));

        var w2 = new NetDataWriter(false, 6);
        Assert.Throws<IndexOutOfRangeException>(() => w2.Put(1L));
        w2.Put(123);
        Assert.Equal(4, w2.Length);
    }

    [Fact]
    public void Writer_EnsureFit_And_ResizeIfNeed_GrowCapacity()
    {
        var w = new NetDataWriter();
        w.EnsureFit(1000);
        Assert.True(w.Capacity >= 1000);
        w.ResizeIfNeed(5000);
        Assert.True(w.Capacity >= 5000);
        Assert.Equal(0, w.Length);
    }

    [Fact]
    public void Writer_FromBytes_FromString_CopySemantics()
    {
        byte[] source = { 10, 20, 30, 40, 50 };

        var borrowed = NetDataWriter.FromBytes(source, copy: false);
        Assert.Same(source, borrowed.Data);
        Assert.Equal(source.Length, borrowed.Length);

        var copied = NetDataWriter.FromBytes(source, copy: true);
        Assert.NotSame(source, copied.Data);
        Assert.Equal(source, copied.CopyData());

        var sliced = NetDataWriter.FromBytes(source, 1, 3);
        Assert.Equal(new byte[] { 20, 30, 40 }, sliced.CopyData());

        var fromSpan = NetDataWriter.FromBytes(source.AsSpan());
        Assert.Equal(source, fromSpan.CopyData());

        var fromString = NetDataWriter.FromString("via string");
        Assert.Equal("via string", new NetDataReader(fromString.CopyData()).GetString());
    }

    [Fact]
    public void Writer_AsReadOnlySpan_MatchesCopyData()
    {
        var w = new NetDataWriter();
        w.Put(0x0A0B0C0D);
        w.Put((byte)0xFE);
        Assert.True(w.AsReadOnlySpan().SequenceEqual(w.CopyData()));
        Assert.Equal(w.Length, w.AsReadOnlySpan().Length);
    }

    [Fact]
    public void RawBytes_PutAndGetBytes_WithOffsets()
    {
        byte[] payload = { 9, 8, 7, 6, 5, 4 };
        var w = new NetDataWriter();
        w.Put(payload);
        w.Put(payload, 2, 3);
        w.Put((ReadOnlySpan<byte>)payload.AsSpan(1, 2));

        var r = ReaderOver(w);
        var first = new byte[payload.Length];
        r.GetBytes(first, payload.Length);
        Assert.Equal(payload, first);

        var second = new byte[5];
        r.GetBytes(second, 1, 3);
        Assert.Equal(new byte[] { 0, 7, 6, 5, 0 }, second);

        var third = new byte[2];
        r.GetBytes(third, 0, 2);
        Assert.Equal(new byte[] { 8, 7 }, third);
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void Segments_SpansAndRemainingBytes_BehaveConsistently()
    {
        var w = new NetDataWriter();
        w.Put((byte)1);
        w.Put((byte)2);
        w.Put((byte)3);
        w.Put((byte)4);
        var r = ReaderOver(w);

        ArraySegment<byte> empty = r.GetBytesSegment(0);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, r.Position);

        ArraySegment<byte> seg = r.GetBytesSegment(2);
        Assert.Equal(new byte[] { 1, 2 }, seg.ToArray());
        Assert.Equal(2, r.Position);

        ReadOnlySpan<byte> span = r.GetRemainingBytesSpan();
        Assert.Equal(2, span.Length);
        Assert.Equal(2, r.Position);
        ReadOnlyMemory<byte> memory = r.GetRemainingBytesMemory();
        Assert.Equal(2, memory.Length);
        Assert.Equal(2, r.Position);
        Assert.Equal((byte)3, span[0]);
        Assert.Equal((byte)4, span[1]);

        byte[] remaining = r.GetRemainingBytes();
        Assert.Equal(new byte[] { 3, 4 }, remaining);
        Assert.True(r.EndOfData);
        Assert.Equal(0, r.AvailableBytes);

        var r2 = ReaderOver(w);
        r2.GetByte();
        ArraySegment<byte> rest = r2.GetRemainingBytesSegment();
        Assert.Equal(new byte[] { 2, 3, 4 }, rest.ToArray());
        Assert.True(r2.EndOfData);
    }

    [Fact]
    public void OverclaimedLengths_ThrowArgumentException()
    {
        var arrayClaim = new NetDataWriter();
        arrayClaim.Put((ushort)100);
        arrayClaim.Put(0);
        Assert.Throws<ArgumentException>(() => new NetDataReader(arrayClaim.CopyData()).GetIntArray());

        var stringClaim = new NetDataWriter();
        stringClaim.Put((ushort)50);
        stringClaim.Put((byte)65);
        Assert.Throws<ArgumentException>(() => new NetDataReader(stringClaim.CopyData()).GetString());
        Assert.Throws<ArgumentException>(() => new NetDataReader(stringClaim.CopyData()).GetString(1000));

        var largeClaim = new NetDataWriter();
        largeClaim.Put(100);
        largeClaim.Put((byte)65);
        Assert.Throws<ArgumentException>(() => new NetDataReader(largeClaim.CopyData()).GetLargeString());

        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[8]).GetGuid());
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).GetBytesSegment(5));
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).GetBytesSegment(-1));
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).GetBytes(new byte[8], 8));
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).GetBytes(new byte[8], 0, 8));
    }
}
