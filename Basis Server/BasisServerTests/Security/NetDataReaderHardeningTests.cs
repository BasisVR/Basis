using Basis.Network.Core;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Truncated-packet hardening for the shared reader.
///
/// Every byte a peer sends reaches the server through <see cref="NetDataReader"/>, and the
/// buffer underneath it is pooled — it outlives the packet and is longer than the packet.
/// An unchecked read past the packet's end therefore does not fault: it returns whatever the
/// PREVIOUS packet left in the pool, so a deliberately short datagram is a read of another
/// peer's traffic, and a handler that keeps reading walks further into it.
///
/// These tests pin the guards that make an over-read throw instead. The rule they encode:
/// a rejected read must throw BEFORE it moves Position, so a handler that catches is left
/// on a coherent reader rather than one pointing into the middle of a value it never read.
///
/// <see cref="NetDataReaderWriterTests"/> covers the happy path and the length-prefixed
/// claims (strings, arrays, byte runs). This file covers the fixed-width reads, the peeks,
/// the seek methods, and the window-relative "read the rest" methods.
/// </summary>
public class NetDataReaderHardeningTests
{
    private static NetDataReader ShortBy(int neededBytes)
    {
        // One byte fewer than the read needs; 0 bytes when the read needs 1.
        return new NetDataReader(new byte[neededBytes - 1]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fixed-width getters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneByteGetters_OnEmptyReader_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => ShortBy(1).GetByte());
        Assert.Throws<InvalidOperationException>(() => ShortBy(1).GetSByte());
        Assert.Throws<InvalidOperationException>(() => ShortBy(1).GetBool());
    }

    [Fact]
    public void TwoByteGetters_OnOneByteReader_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => ShortBy(2).GetUShort());
        Assert.Throws<InvalidOperationException>(() => ShortBy(2).GetShort());
        Assert.Throws<InvalidOperationException>(() => ShortBy(2).GetChar());
    }

    [Fact]
    public void FourByteGetters_OnThreeByteReader_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => ShortBy(4).GetInt());
        Assert.Throws<InvalidOperationException>(() => ShortBy(4).GetUInt());
        Assert.Throws<InvalidOperationException>(() => ShortBy(4).GetFloat());
    }

    [Fact]
    public void EightByteGetters_OnSevenByteReader_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => ShortBy(8).GetLong());
        Assert.Throws<InvalidOperationException>(() => ShortBy(8).GetULong());
        Assert.Throws<InvalidOperationException>(() => ShortBy(8).GetDouble());
    }

    [Fact]
    public void GetGuid_OnFifteenByteReader_Throws()
    {
        Assert.Throws<ArgumentException>(() => ShortBy(16).GetGuid());
    }

    [Fact]
    public void RejectedRead_LeavesPositionWhereItWas()
    {
        // Two bytes of real payload followed by a value the sender truncated.
        var w = new NetDataWriter();
        w.Put((ushort)0x1234);
        w.Put((byte)0xFF);
        var r = new NetDataReader(w.CopyData());

        Assert.Equal((ushort)0x1234, r.GetUShort());
        Assert.Equal(2, r.Position);

        Assert.Throws<InvalidOperationException>(() => r.GetInt());
        Assert.Equal(2, r.Position);
        Assert.Equal(1, r.AvailableBytes);

        // The reader is still usable for the byte that IS there.
        Assert.Equal((byte)0xFF, r.GetByte());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void ReadsThatExactlyFit_Succeed()
    {
        Assert.Equal((byte)0, new NetDataReader(new byte[1]).GetByte());
        Assert.Equal((ushort)0, new NetDataReader(new byte[2]).GetUShort());
        Assert.Equal(0, new NetDataReader(new byte[4]).GetInt());
        Assert.Equal(0L, new NetDataReader(new byte[8]).GetLong());
        Assert.Equal(Guid.Empty, new NetDataReader(new byte[16]).GetGuid());
    }

    [Fact]
    public void OverReadDoesNotLeakTheBytesAfterTheWindow()
    {
        // The pooled-buffer case in miniature: a long buffer, but only the first two
        // bytes belong to this packet. Reading an int must not reach the 0xDE 0xAD.
        byte[] pooled = { 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
        var r = new NetDataReader(pooled, 0, 2);

        Assert.Equal(2, r.AvailableBytes);
        Assert.Throws<InvalidOperationException>(() => r.GetInt());
        Assert.Equal((ushort)1, r.GetUShort());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void GetArray_OnAReaderTooShortForItsOwnLengthPrefix_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new NetDataReader(new byte[1]).GetIntArray());
        Assert.Throws<InvalidOperationException>(() => new NetDataReader(Array.Empty<byte>()).GetFloatArray());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Peeks
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Peeks_OnTruncatedData_Throw()
    {
        Assert.Throws<InvalidOperationException>(() => ShortBy(1).PeekByte());
        Assert.Throws<InvalidOperationException>(() => ShortBy(1).PeekSByte());
        Assert.Throws<InvalidOperationException>(() => ShortBy(1).PeekBool());
        Assert.Throws<InvalidOperationException>(() => ShortBy(2).PeekUShort());
        Assert.Throws<InvalidOperationException>(() => ShortBy(2).PeekShort());
        Assert.Throws<InvalidOperationException>(() => ShortBy(2).PeekChar());
        Assert.Throws<InvalidOperationException>(() => ShortBy(4).PeekInt());
        Assert.Throws<InvalidOperationException>(() => ShortBy(4).PeekUInt());
        Assert.Throws<InvalidOperationException>(() => ShortBy(4).PeekFloat());
        Assert.Throws<InvalidOperationException>(() => ShortBy(8).PeekLong());
        Assert.Throws<InvalidOperationException>(() => ShortBy(8).PeekULong());
        Assert.Throws<InvalidOperationException>(() => ShortBy(8).PeekDouble());
    }

    [Fact]
    public void Peek_AtTheEndOfAWindow_DoesNotReadPastIt()
    {
        byte[] pooled = { 0x11, 0x22, 0x33, 0x44 };
        var r = new NetDataReader(pooled, 0, 1);

        Assert.Equal((byte)0x11, r.PeekByte());
        Assert.Throws<InvalidOperationException>(() => r.PeekUShort());
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void PeekString_AtAWindowEdge_ReturnsEmptyRatherThanReadingOn()
    {
        // Length prefix says 4 content bytes; only 2 of them are inside the window.
        byte[] pooled = { 0x05, 0x00, 0x41, 0x42, 0x43, 0x44 };
        var r = new NetDataReader(pooled, 0, 4);

        Assert.Equal(string.Empty, r.PeekString());
        Assert.Equal(string.Empty, r.PeekString(64));
        Assert.Equal(0, r.Position);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Seeking
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SkipBytes_PastTheEnd_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).SkipBytes(5));
    }

    [Fact]
    public void SkipBytes_Negative_Throws()
    {
        // A negative skip would rewind the reader, letting a handler re-read
        // and re-dispatch the same bytes without bound.
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).SkipBytes(-1));
    }

    [Fact]
    public void SkipBytes_ToExactlyTheEnd_IsAllowed()
    {
        var r = new NetDataReader(new byte[4]);
        r.SkipBytes(4);
        Assert.True(r.EndOfData);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void SkipBytes_PastAWindowEnd_Throws()
    {
        var r = new NetDataReader(new byte[8], 0, 4);
        Assert.Throws<ArgumentException>(() => r.SkipBytes(5));
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void SetPosition_OutsideTheBuffer_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).SetPosition(5));
        Assert.Throws<ArgumentException>(() => new NetDataReader(new byte[4]).SetPosition(-1));
    }

    [Fact]
    public void SetPosition_BeforeAWindowStart_Throws()
    {
        // Rewinding below the window offset would expose the transport header
        // bytes that sit in front of the user payload.
        var r = new NetDataReader(new byte[8], 4, 8);
        Assert.Throws<ArgumentException>(() => r.SetPosition(3));
        Assert.Throws<ArgumentException>(() => r.SetPosition(0));
        Assert.Equal(4, r.Position);
    }

    [Fact]
    public void SetPosition_ToEitherEdgeOfTheWindow_IsAllowed()
    {
        var r = new NetDataReader(new byte[8], 2, 6);

        r.SetPosition(6);
        Assert.True(r.EndOfData);

        r.SetPosition(2);
        Assert.Equal(2, r.Position);
        Assert.Equal(4, r.AvailableBytes);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // "Read the rest" — must mean the rest of the WINDOW, not of the pooled buffer
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetRemainingBytes_OnAWindow_StopsAtTheWindowEnd()
    {
        byte[] pooled = { 0x01, 0x02, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF };
        var r = new NetDataReader(pooled, 0, 4);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, r.GetRemainingBytes());
        Assert.True(r.EndOfData);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void GetRemainingBytesSegment_OnAWindow_StopsAtTheWindowEnd()
    {
        byte[] pooled = { 0x01, 0x02, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF };
        var r = new NetDataReader(pooled, 0, 4);
        r.GetByte();

        Assert.Equal(new byte[] { 0x02, 0x03, 0x04 }, r.GetRemainingBytesSegment().ToArray());
        Assert.True(r.EndOfData);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void GetRemainingBytes_OnAnOffsetWindow_ReturnsOnlyThePayload()
    {
        byte[] pooled = { 0xFF, 0xFF, 0x01, 0x02, 0xDE, 0xAD };
        var r = new NetDataReader(pooled, 2, 4);

        Assert.Equal(new byte[] { 0x01, 0x02 }, r.GetRemainingBytes());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void AfterReadingTheRest_AvailableBytesIsNeverNegative()
    {
        // Position landing past _dataSize is what makes AvailableBytes go negative;
        // a negative AvailableBytes reads as "plenty left" to every `< needed` check
        // in the handlers, which is how a truncated packet used to keep being parsed.
        byte[] pooled = new byte[64];
        var r = new NetDataReader(pooled, 0, 8);
        r.GetRemainingBytes();
        Assert.Equal(0, r.AvailableBytes);

        var r2 = new NetDataReader(pooled, 0, 8);
        r2.GetRemainingBytesSegment();
        Assert.Equal(0, r2.AvailableBytes);
        Assert.Throws<InvalidOperationException>(() => r2.GetByte());
    }

    [Fact]
    public void RemainingSpanAndMemory_OnAWindow_MatchTheWindow()
    {
        byte[] pooled = { 0x01, 0x02, 0x03, 0x04, 0xDE, 0xAD };
        var r = new NetDataReader(pooled, 0, 4);
        r.GetByte();

        Assert.Equal(3, r.GetRemainingBytesSpan().Length);
        Assert.Equal(3, r.GetRemainingBytesMemory().Length);
        Assert.Equal(1, r.Position);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TryGet stays non-throwing — handlers that use it keep their fast reject path
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryGet_OnAWindowEdge_ReturnsFalseInsteadOfThrowing()
    {
        byte[] pooled = { 0x01, 0xDE, 0xAD, 0xBE, 0xEF };
        var r = new NetDataReader(pooled, 0, 1);

        Assert.False(r.TryGetInt(out int _i));
        Assert.False(r.TryGetUShort(out ushort _us));
        Assert.False(r.TryGetLong(out long _l));
        Assert.Equal(0, r.Position);

        Assert.True(r.TryGetByte(out byte only));
        Assert.Equal((byte)0x01, only);
        Assert.False(r.TryGetByte(out byte _b));
    }

    [Fact]
    public void TryGetString_OnAWindowEdge_ReturnsFalse()
    {
        byte[] pooled = { 0x05, 0x00, 0x41, 0x42, 0x43, 0x44 };
        var r = new NetDataReader(pooled, 0, 4);

        Assert.False(r.TryGetString(out string _s));
        Assert.Equal(0, r.Position);
    }
}
