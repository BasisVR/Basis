using System;
using System.Collections.Generic;
using System.Text;

namespace HVR.Basis.Comms
{
    // If the last field of a packet is a list:
    // - Do not encode the size of the that last list.
    // - The size of the list determined by adding items to the list while reading until there is nothing left to read in the packet.

    internal class HVRPacket_NewVariables
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerSubmitsNewVariables;
        public List<Inner_NewVariable> newGeneralVariables;
        public List<Inner_NewQuickVariable> floatZero;
        public List<Inner_NewQuickVariable> floatOne;

        internal class Inner_NewVariable
        {
            public string address;
            public ushort networkId;
            public bool needsInterpolation;
            public byte variableTypeCode;
            public object initialValue;
        }

        internal class Inner_NewQuickVariable
        {
            public string address;
            public ushort networkId;
            public bool needsInterpolation;
        }

        public byte[] Serialize(bool needsUshortAddresses)
        {
            var writer = new HVRNetWriter(needsUshortAddresses);
            writer.WriteByte(packetType);

            writer.WriteUshort((ushort)newGeneralVariables.Count);

            foreach (var holder in newGeneralVariables)
            {
                writer.WriteString(holder.address);
                writer.WriteAddress(holder.networkId);
                writer.WriteByte(holder.needsInterpolation ? (byte)1 : (byte)0);
                writer.WriteByte(holder.variableTypeCode);
                writer.WriteFloat((float)holder.initialValue);
            }

            writer.WriteUshort((ushort)floatZero.Count);

            foreach (var holder in floatZero)
            {
                writer.WriteString(holder.address);
                writer.WriteAddress(holder.networkId);
                writer.WriteByte(holder.needsInterpolation ? (byte)1 : (byte)0);
            }

            foreach (var holder in floatOne)
            {
                writer.WriteString(holder.address);
                writer.WriteAddress(holder.networkId);
                writer.WriteByte(holder.needsInterpolation ? (byte)1 : (byte)0);
            }

            return writer.ToArray();
        }

        public static bool TryDeserialize(bool needsUshortAddresses, ArraySegment<byte> data, out HVRPacket_NewVariables result)
        {
            try
            {
                var reader = new HVRNetReader(data, needsUshortAddresses);

                var packetType = reader.ReadByte();
                result = new HVRPacket_NewVariables();

                var countGeneral = reader.ReadUshort();
                result.newGeneralVariables = new List<Inner_NewVariable>((int)countGeneral);
                for (var i = 0; i < countGeneral; i++)
                {
                    var address = reader.ReadString();
                    var networkId = reader.ReadAddress();
                    var needsInterpolation = reader.ReadByte() == 1;
                    var variableTypeCode = reader.ReadByte();
                    var initialValue = reader.ReadFloat();

                    result.newGeneralVariables.Add(new Inner_NewVariable
                    {
                        address = address,
                        networkId = networkId,
                        needsInterpolation = needsInterpolation,
                        variableTypeCode = variableTypeCode,
                        initialValue = initialValue
                    });
                }

                var countZero = reader.ReadUshort();
                result.floatZero = new List<Inner_NewQuickVariable>((int)countZero);
                for (var i = 0; i < countZero; i++)
                {
                    var address = reader.ReadString();
                    var networkId = reader.ReadAddress();
                    var needsInterpolation = reader.ReadByte() == 1;
                    result.floatZero.Add(new Inner_NewQuickVariable
                    {
                        address = address,
                        networkId = networkId,
                        needsInterpolation = needsInterpolation
                    });
                }

                result.floatOne = new List<Inner_NewQuickVariable>();
                while (!reader.IsExhausted)
                {
                    var address = reader.ReadString();
                    var networkId = reader.ReadAddress();
                    var needsInterpolation = reader.ReadByte() == 1;
                    result.floatOne.Add(new Inner_NewQuickVariable
                    {
                        address = address,
                        networkId = networkId,
                        needsInterpolation = needsInterpolation
                    });
                }

                reader.Finish();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }
    }

    internal class HVRPacket_UpdatedVariables_ZeroesOrOnes
    {
        public byte packetType;
        public byte timingSteps;
        public List<ushort> networkIds;

        public byte[] Serialize(bool needsUshortAddresses)
        {
            var writer = new HVRNetWriter(needsUshortAddresses);
            writer.WriteByte(packetType);
            writer.WriteByte(timingSteps);

            foreach (var networkId in networkIds)
            {
                writer.WriteAddress(networkId);
            }

            return writer.ToArray();
        }

        public static bool TryDeserialize(bool needsUshortAddresses, ArraySegment<byte> data, byte packetType, out HVRPacket_UpdatedVariables_ZeroesOrOnes result)
        {
            try
            {
                var reader = new HVRNetReader(data, needsUshortAddresses);

                var receivedPacketType = reader.ReadByte();
                var timingSteps = reader.ReadByte();

                result = new HVRPacket_UpdatedVariables_ZeroesOrOnes
                {
                    packetType = packetType,
                    timingSteps = timingSteps,
                    networkIds = new List<ushort>()
                };

                while (!reader.IsExhausted)
                {
                    result.networkIds.Add(reader.ReadAddress());
                }

                reader.Finish();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }
    }

    internal class HVRPacket_UpdatedVariables_ZeroesAndOnes
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_ZeroesAndOnes;
        public byte timingSteps;
        public ushort numberOfZeroes;
        public List<ushort> networkIds;

        public byte[] Serialize(bool needsUshortAddresses)
        {
            var writer = new HVRNetWriter(needsUshortAddresses);
            writer.WriteByte(packetType);
            writer.WriteByte(timingSteps);

            writer.WriteUshort(numberOfZeroes);

            foreach (var networkId in networkIds)
            {
                writer.WriteAddress(networkId);
            }

            return writer.ToArray();
        }

        public static bool TryDeserialize(bool needsUshortAddresses, ArraySegment<byte> data, out HVRPacket_UpdatedVariables_ZeroesAndOnes result)
        {
            try
            {
                var reader = new HVRNetReader(data, needsUshortAddresses);

                var packetType = reader.ReadByte();
                var timingSteps = reader.ReadByte();
                var numberOfZeroes = reader.ReadUshort();

                result = new HVRPacket_UpdatedVariables_ZeroesAndOnes
                {
                    timingSteps = timingSteps,
                    numberOfZeroes = numberOfZeroes,
                    networkIds = new List<ushort>()
                };

                while (!reader.IsExhausted)
                {
                    result.networkIds.Add(reader.ReadAddress());
                }

                reader.Finish();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }
    }

    internal class HVRPacket_UpdatedVariables_Mixed
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Mixed;
        public byte timingSteps;
        public ushort numberOfZeroes;
        public List<ushort> networkIds;
        public List<Inner_UpdatedValue> other;

        internal class Inner_UpdatedValue
        {
            public ushort networkId;
            public object value;
        }

        public byte[] Serialize(bool needsUshortAddresses)
        {
            var writer = new HVRNetWriter(needsUshortAddresses);
            writer.WriteByte(packetType);
            writer.WriteByte(timingSteps);

            writer.WriteUshort(numberOfZeroes);

            writer.WriteUshort((ushort)networkIds.Count);

            foreach (var networkId in networkIds)
            {
                writer.WriteAddress(networkId);
            }

            foreach (var updatedValue in other)
            {
                writer.WriteAddress(updatedValue.networkId);
                writer.WriteFloat((float)updatedValue.value);
            }

            return writer.ToArray();
        }

        public static bool TryDeserialize(bool needsUshortAddresses, ArraySegment<byte> data, out HVRPacket_UpdatedVariables_Mixed result)
        {
            try
            {
                var reader = new HVRNetReader(data, needsUshortAddresses);

                var packetType = reader.ReadByte();
                var timingSteps = reader.ReadByte();
                var numberOfZeroes = reader.ReadUshort();
                var networkIdsCount = reader.ReadUshort();

                result = new HVRPacket_UpdatedVariables_Mixed
                {
                    timingSteps = timingSteps,
                    numberOfZeroes = numberOfZeroes,
                    networkIds = new List<ushort>((int)networkIdsCount)
                };

                for (var i = 0; i < networkIdsCount; i++)
                {
                    result.networkIds.Add(reader.ReadAddress());
                }

                result.other = new List<Inner_UpdatedValue>();

                while (!reader.IsExhausted)
                {
                    var networkId = reader.ReadAddress();
                    var floatValue = reader.ReadFloat();

                    result.other.Add(new Inner_UpdatedValue
                    {
                        networkId = networkId,
                        value = floatValue
                    });
                }

                reader.Finish();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }
    }

    internal class HVRPacket_DowngradeFloatToLowFrequency
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerDowngradesFloatToLowFrequency;
        public List<ushort> networkIds;

        public byte[] Serialize(bool needsUshortAddresses)
        {
            var writer = new HVRNetWriter(needsUshortAddresses);
            writer.WriteByte(packetType);

            foreach (var networkId in networkIds)
            {
                writer.WriteAddress(networkId);
            }

            return writer.ToArray();
        }

        public static bool TryDeserialize(bool needsUshortAddresses, ArraySegment<byte> data, out HVRPacket_DowngradeFloatToLowFrequency result)
        {
            try
            {
                var reader = new HVRNetReader(data, needsUshortAddresses);

                var packetType = reader.ReadByte();

                result = new HVRPacket_DowngradeFloatToLowFrequency
                {
                    networkIds = new List<ushort>()
                };

                while (!reader.IsExhausted)
                {
                    result.networkIds.Add(reader.ReadAddress());
                }

                reader.Finish();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }
    }

    internal class HVRPacket_UpgradeFloatToHighFrequency
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerUpgradesFloatToHighFrequency;
        public List<Inner_Item> items;

        internal class Inner_Item
        {
            public ushort networkId;
            public float min;
            public float max;
        }

        public byte[] Serialize(bool needsUshortAddresses)
        {
            var writer = new HVRNetWriter(needsUshortAddresses);
            writer.WriteByte(packetType);

            foreach (var item in items)
            {
                writer.WriteAddress(item.networkId);
                writer.WriteFloat(item.min);
                writer.WriteFloat(item.max);
            }

            return writer.ToArray();
        }

        public static bool TryDeserialize(bool needsUshortAddresses, ArraySegment<byte> data, out HVRPacket_UpgradeFloatToHighFrequency result)
        {
            try
            {
                var reader = new HVRNetReader(data, needsUshortAddresses);

                var packetType = reader.ReadByte();

                result = new HVRPacket_UpgradeFloatToHighFrequency
                {
                    items = new List<Inner_Item>()
                };

                while (!reader.IsExhausted)
                {
                    result.items.Add(new Inner_Item
                    {
                        networkId = reader.ReadAddress(),
                        min = reader.ReadFloat(),
                        max = reader.ReadFloat()
                    });
                }

                reader.Finish();
                return true;
            }
            catch (Exception)
            {
                result = null;
                return false;
            }
        }
    }

    internal class HVRNetWriter
    {
        private readonly bool _needsUshortAddresses;
        private byte[] _buffer;
        private int _offset;

        public HVRNetWriter(bool needsUshortAddresses)
        {
            _needsUshortAddresses = needsUshortAddresses;
            _buffer = new byte[4096];
            _offset = 0;
        }

        public byte[] ToArray()
        {
            var result = new byte[_offset];
            Buffer.BlockCopy(_buffer, 0, result, 0, _offset);
            return result;
        }

        private void EnsureCapacity(int additional)
        {
            if (_offset + additional > _buffer.Length)
            {
                throw new Exception($"Attempted to write {additional} bytes but only {_buffer.Length - _offset} bytes remain in the buffer.");
            }
        }

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_offset++] = value;
        }

        public void WriteUshort(ushort value)
        {
            EnsureCapacity(2);
            _buffer[_offset++] = (byte)(value & 0xFF);
            _buffer[_offset++] = (byte)((value >> 8) & 0xFF);
        }

        public void WriteFloat(float value)
        {
            EnsureCapacity(4);
            var valueBytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(valueBytes, 0, _buffer, _offset, 4);
            _offset += 4;
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteUshort((ushort)bytes.Length);
            EnsureCapacity(bytes.Length);
            Buffer.BlockCopy(bytes, 0, _buffer, _offset, bytes.Length);
            _offset += bytes.Length;
        }

        public void WriteAddress(ushort networkId)
        {
            if (_needsUshortAddresses) WriteUshort(networkId);
            else
            {
                if (networkId > 255) throw new InvalidOperationException($"Network ID {networkId} is out of range, this indicates a programming error. Writer should have been supplied with needsUshortAddresses set to true.");
                WriteByte((byte)networkId);
            }
        }
    }

    internal class HVRNetReader
    {
        private readonly bool _needsUshortAddresses;
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _offset;

        public HVRNetReader(ArraySegment<byte> data, bool needsUshortAddresses)
        {
            _needsUshortAddresses = needsUshortAddresses;
            _buffer = data.Array;
            _offset = data.Offset;
            _end = data.Offset + data.Count;
        }

        public bool IsExhausted => _offset >= _end;

        public void Finish()
        {
            if (_offset < _end)
            {
                throw new Exception($"Finished reading packet but {_end - _offset} bytes remain in the buffer.");
            }
        }

        private void EnsureCapacity(int count)
        {
            if (_offset + count > _end)
            {
                throw new Exception($"Attempted to read {count} bytes but only {_end - _offset} bytes remain in the buffer.");
            }
        }

        public byte ReadByte()
        {
            EnsureCapacity(1);
            return _buffer[_offset++];
        }

        public ushort ReadUshort()
        {
            EnsureCapacity(2);
            var value = (ushort)(_buffer[_offset] | (_buffer[_offset + 1] << 8));
            _offset += 2;
            return value;
        }

        public float ReadFloat()
        {
            EnsureCapacity(4);
            var value = BitConverter.ToSingle(_buffer, _offset);
            _offset += 4;
            return value;
        }

        public string ReadString()
        {
            var length = ReadUshort();
            EnsureCapacity(length);
            var value = Encoding.UTF8.GetString(_buffer, _offset, length);
            _offset += length;
            return value;
        }

        public ushort ReadAddress()
        {
            if (_needsUshortAddresses) return ReadUshort();
            return (ushort)ReadByte();
        }
    }
}
