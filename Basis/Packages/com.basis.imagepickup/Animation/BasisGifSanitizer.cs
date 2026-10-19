using System;
using System.Collections.Generic;
using System.IO;

namespace Basis.ImagePickup
{
    internal sealed class BasisGifSanitizer
    {
        private const int MaxCodeBits = 12;
        private const int CodeLimit = 1 << MaxCodeBits;
        private const int HashSize = 5003;
        private const int HashShift = 4;
        private const int LogicalScreenEnd = 13;

        private static readonly byte[] Signature = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' };
        private static readonly byte[] NetscapeIdentifier = { (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0' };
        private static readonly byte[] AnimextsIdentifier = { (byte)'A', (byte)'N', (byte)'I', (byte)'M', (byte)'E', (byte)'X', (byte)'T', (byte)'S', (byte)'1', (byte)'.', (byte)'0' };

        private struct Frame
        {
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public byte Packed;
            public int PaletteOffset;
            public int PaletteCount;
            public byte MinimumCodeSize;
            public int DataOffset;
            public bool HasControl;
            public byte ControlPacked;
            public int Delay;
            public byte TransparentIndex;
        }

        private readonly byte[] _source;
        private readonly Stream _destination;
        private readonly List<Frame> _frames = new List<Frame>();
        private readonly ushort[] _prefix = new ushort[CodeLimit];
        private readonly byte[] _suffix = new byte[CodeLimit];
        private readonly byte[] _stack = new byte[CodeLimit + 1];
        private readonly int[] _hashKeys = new int[HashSize];
        private readonly int[] _hashCodes = new int[HashSize];
        private readonly byte[] _block = new byte[255];
        private byte[] _indices;
        private int _globalPaletteCount;
        private int _loopCount = -1;
        private int _largestFramePixels;
        private int _blockLength;
        private int _bitBuffer;
        private int _bitCount;
        private int _codeBits;
        private int _maxCode;
        private int _nextCode;
        private int _initialCodeBits;
        private bool _clearPending;

        private BasisGifSanitizer(byte[] source, Stream destination)
        {
            _source = source;
            _destination = destination;
        }

        public static bool TryWrite(byte[] source, Stream destination, out string error)
        {
            if (source == null || destination == null)
            {
                error = "No GIF data to write.";
                return false;
            }
            var sanitizer = new BasisGifSanitizer(source, destination);
            return sanitizer.TryParse(out error) && sanitizer.TryRewrite(out error);
        }

        private bool TryParse(out string error)
        {
            byte[] source = _source;
            int length = source.Length;
            if (length < LogicalScreenEnd)
                return Fail("GIF header is truncated.", out error);
            if (
                source[0] != (byte)'G'
                || source[1] != (byte)'I'
                || source[2] != (byte)'F'
                || source[3] != (byte)'8'
                || (source[4] != (byte)'7' && source[4] != (byte)'9')
                || source[5] != (byte)'a'
            )
            {
                return Fail("Not a GIF (bad signature).", out error);
            }

            int canvasWidth = ReadUInt16(6);
            int canvasHeight = ReadUInt16(8);
            if (
                canvasWidth <= 0
                || canvasHeight <= 0
                || canvasWidth > BasisImagePickupSettings.MaxAnimationDimension
                || canvasHeight > BasisImagePickupSettings.MaxAnimationDimension
                || (long)canvasWidth * canvasHeight > BasisImagePickupSettings.MaxAnimationCanvasPixels
            )
            {
                return Fail("GIF canvas dimensions exceed the configured limits.", out error);
            }

            int offset = LogicalScreenEnd;
            byte logicalPacked = source[10];
            if ((logicalPacked & 0x80) != 0)
            {
                _globalPaletteCount = 1 << ((logicalPacked & 0x07) + 1);
                offset += _globalPaletteCount * 3;
                if (offset > length)
                    return Fail("GIF color table is truncated.", out error);
            }

            bool hasControl = false;
            byte controlPacked = 0;
            byte transparentIndex = 0;
            int delay = 0;
            long decodedPixels = 0;
            while (offset < length)
            {
                byte blockType = source[offset++];
                if (blockType == 0x3B)
                    break;

                if (blockType == 0x21)
                {
                    if (offset >= length)
                        return Fail("GIF extension is truncated.", out error);
                    byte label = source[offset++];
                    if (label == 0xF9)
                    {
                        if (offset + 6 > length || source[offset] != 4 || source[offset + 5] != 0)
                            return Fail("GIF graphic control extension is malformed.", out error);
                        controlPacked = source[offset + 1];
                        delay = ReadUInt16(offset + 2);
                        transparentIndex = source[offset + 4];
                        hasControl = true;
                        offset += 6;
                        continue;
                    }

                    if (label == 0xFF)
                    {
                        if (offset >= length)
                            return Fail("GIF application extension is malformed.", out error);
                        int identifierLength = source[offset++];
                        if (offset + identifierLength > length)
                            return Fail("GIF application extension is malformed.", out error);
                        bool loopExtension = IsLoopIdentifier(offset, identifierLength);
                        offset += identifierLength;
                        if (
                            loopExtension
                            && offset < length
                            && source[offset] >= 3
                            && offset + 1 + source[offset] <= length
                            && source[offset + 1] == 1
                        )
                        {
                            _loopCount = ReadUInt16(offset + 2);
                        }
                        if (!TrySkipSubBlocks(ref offset))
                            return Fail("GIF data sub-blocks are truncated.", out error);
                        continue;
                    }

                    if (!TrySkipSubBlocks(ref offset))
                        return Fail("GIF data sub-blocks are truncated.", out error);
                    if (label == 0x01)
                        hasControl = false;
                    continue;
                }

                if (blockType != 0x2C)
                    return Fail("GIF contains an unsupported block.", out error);
                if (_frames.Count >= BasisImagePickupSettings.MaxAnimationFrames)
                    return Fail($"GIF exceeds {BasisImagePickupSettings.MaxAnimationFrames:N0} frames.", out error);
                if (offset + 9 > length)
                    return Fail("GIF frame descriptor is truncated.", out error);

                var frame = new Frame
                {
                    Left = ReadUInt16(offset),
                    Top = ReadUInt16(offset + 2),
                    Width = ReadUInt16(offset + 4),
                    Height = ReadUInt16(offset + 6),
                    Packed = source[offset + 8],
                    HasControl = hasControl,
                    ControlPacked = controlPacked,
                    Delay = delay,
                    TransparentIndex = transparentIndex,
                };
                offset += 9;
                if (
                    frame.Width <= 0
                    || frame.Height <= 0
                    || frame.Left + frame.Width > canvasWidth
                    || frame.Top + frame.Height > canvasHeight
                )
                {
                    return Fail("GIF frame lies outside the canvas.", out error);
                }

                int framePixels = frame.Width * frame.Height;
                decodedPixels += framePixels;
                if (decodedPixels > BasisImagePickupSettings.MaxAnimationDecodedFramePixels)
                    return Fail("GIF exceeds the decoded frame-pixel budget.", out error);

                if ((frame.Packed & 0x80) != 0)
                {
                    frame.PaletteCount = 1 << ((frame.Packed & 0x07) + 1);
                    frame.PaletteOffset = offset;
                    offset += frame.PaletteCount * 3;
                    if (offset > length)
                        return Fail("GIF color table is truncated.", out error);
                }
                else
                {
                    frame.PaletteCount = _globalPaletteCount;
                    frame.PaletteOffset = LogicalScreenEnd;
                }
                if (frame.PaletteCount <= 0)
                    return Fail("GIF frame has no color table.", out error);

                if (offset >= length || source[offset] < 2 || source[offset] > 8)
                    return Fail("GIF LZW code size is invalid.", out error);
                frame.MinimumCodeSize = source[offset++];
                frame.DataOffset = offset;
                if (!TrySkipSubBlocks(ref offset))
                    return Fail("GIF data sub-blocks are truncated.", out error);

                _frames.Add(frame);
                _largestFramePixels = Math.Max(_largestFramePixels, framePixels);
                hasControl = false;
            }

            if (_frames.Count == 0)
                return Fail("GIF contains no image frames.", out error);
            error = null;
            return true;
        }

        private bool TryRewrite(out string error)
        {
            _indices = new byte[_largestFramePixels];
            Stream output = _destination;
            output.Write(Signature, 0, Signature.Length);
            output.Write(_source, 6, LogicalScreenEnd - 6);
            if (_globalPaletteCount > 0)
                output.Write(_source, LogicalScreenEnd, _globalPaletteCount * 3);
            if (_loopCount >= 0)
            {
                output.WriteByte(0x21);
                output.WriteByte(0xFF);
                output.WriteByte((byte)NetscapeIdentifier.Length);
                output.Write(NetscapeIdentifier, 0, NetscapeIdentifier.Length);
                output.WriteByte(3);
                output.WriteByte(1);
                WriteUInt16(_loopCount);
                output.WriteByte(0);
            }

            int frameCount = _frames.Count;
            for (int index = 0; index < frameCount; index++)
            {
                Frame frame = _frames[index];
                if (frame.HasControl)
                {
                    output.WriteByte(0x21);
                    output.WriteByte(0xF9);
                    output.WriteByte(4);
                    output.WriteByte((byte)(frame.ControlPacked & 0x1F));
                    WriteUInt16(frame.Delay);
                    output.WriteByte((frame.ControlPacked & 0x01) != 0 ? frame.TransparentIndex : (byte)0);
                    output.WriteByte(0);
                }

                output.WriteByte(0x2C);
                WriteUInt16(frame.Left);
                WriteUInt16(frame.Top);
                WriteUInt16(frame.Width);
                WriteUInt16(frame.Height);
                output.WriteByte((byte)(frame.Packed & 0xE7));
                if ((frame.Packed & 0x80) != 0)
                    output.Write(_source, frame.PaletteOffset, frame.PaletteCount * 3);

                int pixelCount = frame.Width * frame.Height;
                if (!TryDecodeIndices(frame, pixelCount))
                {
                    error = $"GIF frame {index + 1:N0} has an invalid LZW stream.";
                    return false;
                }
                output.WriteByte(frame.MinimumCodeSize);
                EncodeIndices(pixelCount, frame.MinimumCodeSize);
                output.WriteByte(0);
            }

            output.WriteByte(0x3B);
            error = null;
            return true;
        }

        private bool TryDecodeIndices(Frame frame, int pixelCount)
        {
            byte[] source = _source;
            int length = source.Length;
            int offset = frame.DataOffset;
            int minimumCodeSize = frame.MinimumCodeSize;
            int clearCode = 1 << minimumCodeSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minimumCodeSize + 1;
            int oldCode = -1;
            byte firstCharacter = 0;
            bool validateLiterals = frame.PaletteCount < clearCode;
            int remainingInBlock = 0;
            int bits = 0;
            int bitCount = 0;
            int written = 0;
            while (true)
            {
                while (bitCount < codeSize)
                {
                    if (remainingInBlock == 0)
                    {
                        if (offset >= length)
                            return false;
                        remainingInBlock = source[offset++];
                        if (remainingInBlock == 0)
                            return false;
                    }
                    if (offset >= length)
                        return false;
                    bits |= source[offset++] << bitCount;
                    bitCount += 8;
                    remainingInBlock--;
                }

                int code = bits & ((1 << codeSize) - 1);
                bits >>= codeSize;
                bitCount -= codeSize;
                if (code == clearCode)
                {
                    codeSize = minimumCodeSize + 1;
                    nextCode = endCode + 1;
                    oldCode = -1;
                    continue;
                }
                if (code == endCode)
                    return written == pixelCount;

                int inputCode = code;
                int stackCount = 0;
                if (code == nextCode)
                {
                    if (oldCode < 0)
                        return false;
                    _stack[stackCount++] = firstCharacter;
                    code = oldCode;
                }
                else if (code > nextCode)
                {
                    return false;
                }

                while (code > endCode)
                {
                    if (code >= nextCode || stackCount >= _stack.Length)
                        return false;
                    _stack[stackCount++] = _suffix[code];
                    code = _prefix[code];
                }
                if (code >= clearCode || stackCount >= _stack.Length)
                    return false;
                if (validateLiterals && code >= frame.PaletteCount)
                    return false;

                firstCharacter = (byte)code;
                _stack[stackCount++] = firstCharacter;
                if (stackCount > pixelCount - written)
                    return false;
                while (stackCount > 0)
                    _indices[written++] = _stack[--stackCount];

                if (oldCode >= 0 && nextCode < CodeLimit)
                {
                    _prefix[nextCode] = (ushort)oldCode;
                    _suffix[nextCode] = firstCharacter;
                    nextCode++;
                    if (nextCode == (1 << codeSize) && codeSize < MaxCodeBits)
                        codeSize++;
                }
                oldCode = inputCode;
            }
        }

        private void EncodeIndices(int pixelCount, int minimumCodeSize)
        {
            int clearCode = 1 << minimumCodeSize;
            int endCode = clearCode + 1;
            _initialCodeBits = minimumCodeSize + 1;
            _codeBits = _initialCodeBits;
            _maxCode = (1 << _codeBits) - 1;
            _nextCode = clearCode + 2;
            _clearPending = false;
            _bitBuffer = 0;
            _bitCount = 0;
            _blockLength = 0;
            Array.Fill(_hashKeys, -1);

            EmitCode(clearCode);
            int prefix = _indices[0];
            for (int pixelIndex = 1; pixelIndex < pixelCount; pixelIndex++)
            {
                int pixel = _indices[pixelIndex];
                int packed = (pixel << MaxCodeBits) + prefix;
                int slot = (pixel << HashShift) ^ prefix;
                bool found = false;
                if (_hashKeys[slot] == packed)
                {
                    found = true;
                }
                else if (_hashKeys[slot] >= 0)
                {
                    int stride = slot == 0 ? 1 : HashSize - slot;
                    do
                    {
                        slot -= stride;
                        if (slot < 0)
                            slot += HashSize;
                        if (_hashKeys[slot] == packed)
                        {
                            found = true;
                            break;
                        }
                    }
                    while (_hashKeys[slot] >= 0);
                }

                if (found)
                {
                    prefix = _hashCodes[slot];
                    continue;
                }

                EmitCode(prefix);
                if (_nextCode < CodeLimit)
                {
                    _hashCodes[slot] = _nextCode++;
                    _hashKeys[slot] = packed;
                }
                else
                {
                    Array.Fill(_hashKeys, -1);
                    _nextCode = clearCode + 2;
                    _clearPending = true;
                    EmitCode(clearCode);
                }
                prefix = pixel;
            }

            EmitCode(prefix);
            EmitCode(endCode);
            while (_bitCount > 0)
            {
                EmitByte((byte)(_bitBuffer & 0xFF));
                _bitBuffer >>= 8;
                _bitCount -= 8;
            }
            FlushBlock();
        }

        private void EmitCode(int code)
        {
            _bitBuffer |= code << _bitCount;
            _bitCount += _codeBits;
            while (_bitCount >= 8)
            {
                EmitByte((byte)(_bitBuffer & 0xFF));
                _bitBuffer >>= 8;
                _bitCount -= 8;
            }

            if (_nextCode > _maxCode || _clearPending)
            {
                if (_clearPending)
                {
                    _codeBits = _initialCodeBits;
                    _maxCode = (1 << _codeBits) - 1;
                    _clearPending = false;
                }
                else
                {
                    _codeBits++;
                    _maxCode = _codeBits == MaxCodeBits ? CodeLimit : (1 << _codeBits) - 1;
                }
            }
        }

        private void EmitByte(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == _block.Length)
                FlushBlock();
        }

        private void FlushBlock()
        {
            if (_blockLength == 0)
                return;
            _destination.WriteByte((byte)_blockLength);
            _destination.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }

        private bool TrySkipSubBlocks(ref int offset)
        {
            int length = _source.Length;
            while (offset < length)
            {
                int size = _source[offset++];
                if (size == 0)
                    return true;
                offset += size;
            }
            return false;
        }

        private bool IsLoopIdentifier(int offset, int identifierLength)
        {
            if (identifierLength < NetscapeIdentifier.Length)
                return false;
            return Matches(offset, NetscapeIdentifier) || Matches(offset, AnimextsIdentifier);
        }

        private bool Matches(int offset, byte[] identifier)
        {
            for (int i = 0; i < identifier.Length; i++)
            {
                if (_source[offset + i] != identifier[i])
                    return false;
            }
            return true;
        }

        private int ReadUInt16(int offset)
        {
            return _source[offset] | (_source[offset + 1] << 8);
        }

        private void WriteUInt16(int value)
        {
            _destination.WriteByte((byte)value);
            _destination.WriteByte((byte)(value >> 8));
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
