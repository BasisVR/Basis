using System;
using System.Collections.Generic;
using System.Text;

namespace HVR.Basis.Comms
{
    internal class HVRVariableNetworkingPacket_NewVariables
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerSubmitsNewVariables;
        public List<HVRVariableNetworkingPacket_NewVariable> newGeneralVariables;
        public List<HVRVariableNetworkingPacket_NewQuickVariable> floatZero;
        public List<HVRVariableNetworkingPacket_NewQuickVariable> floatOne;

        internal class HVRVariableNetworkingPacket_NewVariable
        {
            public string address;
            public ushort networkId;
            public byte variableTypeCode;
            public object initialValue;
        }

        internal class HVRVariableNetworkingPacket_NewQuickVariable
        {
            public string address;
            public ushort networkId;
        }

        public byte[] Serialize()
        {
            var totalLength = 1 + 2; // packetType + countGeneral
            var addressBytesList = new List<byte[]>(newGeneralVariables.Count);
            foreach (var newVariableToCreate in newGeneralVariables)
            {
                var addressBytes = Encoding.UTF8.GetBytes(newVariableToCreate.address);
                addressBytesList.Add(addressBytes);

                totalLength += 2 + addressBytes.Length + 2 + 1 + 4;
            }

            totalLength += 2; // countFloatZero
            var floatZeroAddressBytesList = new List<byte[]>(floatZero.Count);
            foreach (var quickVar in floatZero)
            {
                var addressBytes = Encoding.UTF8.GetBytes(quickVar.address);
                floatZeroAddressBytesList.Add(addressBytes);
                totalLength += 2 + addressBytes.Length + 2;
            }

            totalLength += 2; // countFloatOne
            var floatOneAddressBytesList = new List<byte[]>(floatOne.Count);
            foreach (var quickVar in floatOne)
            {
                var addressBytes = Encoding.UTF8.GetBytes(quickVar.address);
                floatOneAddressBytesList.Add(addressBytes);
                totalLength += 2 + addressBytes.Length + 2;
            }

            var result = new byte[totalLength];
            result[0] = packetType;
            var offset = 1;

            // General Variables
            var countGeneral = (ushort)newGeneralVariables.Count;
            result[offset++] = (byte)(countGeneral & 0xFF);
            result[offset++] = (byte)((countGeneral >> 8) & 0xFF);

            for (var i = 0; i < newGeneralVariables.Count; i++)
            {
                var holder = newGeneralVariables[i];
                var addressBytes = addressBytesList[i];

                var m0_addressLength = (ushort)addressBytes.Length;
                var m1_addressBytes = addressBytes;
                var m1b_networkId = holder.networkId;
                var m2_variableTypeCode = holder.variableTypeCode;
                var m3_initialValue = (float)holder.initialValue;

                // Address length (ushort - 2 bytes)
                result[offset++] = (byte)(m0_addressLength & 0xFF);
                result[offset++] = (byte)((m0_addressLength >> 8) & 0xFF);

                // Address bytes
                Buffer.BlockCopy(m1_addressBytes, 0, result, offset, m1_addressBytes.Length);
                offset += m1_addressBytes.Length;

                // Network ID (ushort - 2 bytes)
                result[offset++] = (byte)(m1b_networkId & 0xFF);
                result[offset++] = (byte)((m1b_networkId >> 8) & 0xFF);

                // Variable type code (byte - 1 byte)
                result[offset++] = m2_variableTypeCode;

                // Initial value (float - 4 bytes)
                var valueBytes = BitConverter.GetBytes(m3_initialValue);
                Buffer.BlockCopy(valueBytes, 0, result, offset, 4);
                offset += 4;
            }

            // Float Zero Variables
            var countZero = (ushort)floatZero.Count;
            result[offset++] = (byte)(countZero & 0xFF);
            result[offset++] = (byte)((countZero >> 8) & 0xFF);

            for (var i = 0; i < floatZero.Count; i++)
            {
                var holder = floatZero[i];
                var addressBytes = floatZeroAddressBytesList[i];

                var addressLength = (ushort)addressBytes.Length;
                result[offset++] = (byte)(addressLength & 0xFF);
                result[offset++] = (byte)((addressLength >> 8) & 0xFF);

                Buffer.BlockCopy(addressBytes, 0, result, offset, addressBytes.Length);
                offset += addressBytes.Length;

                result[offset++] = (byte)(holder.networkId & 0xFF);
                result[offset++] = (byte)((holder.networkId >> 8) & 0xFF);
            }

            // Float One Variables
            var countOne = (ushort)floatOne.Count;
            result[offset++] = (byte)(countOne & 0xFF);
            result[offset++] = (byte)((countOne >> 8) & 0xFF);

            for (var i = 0; i < floatOne.Count; i++)
            {
                var holder = floatOne[i];
                var addressBytes = floatOneAddressBytesList[i];

                var addressLength = (ushort)addressBytes.Length;
                result[offset++] = (byte)(addressLength & 0xFF);
                result[offset++] = (byte)((addressLength >> 8) & 0xFF);

                Buffer.BlockCopy(addressBytes, 0, result, offset, addressBytes.Length);
                offset += addressBytes.Length;

                result[offset++] = (byte)(holder.networkId & 0xFF);
                result[offset++] = (byte)((holder.networkId >> 8) & 0xFF);
            }

            return result;
        }

        public static HVRVariableNetworkingPacket_NewVariables Deserialize(ArraySegment<byte> data)
        {
            var offset = data.Offset;
            var array = data.Array;

            var packetType = array[offset++];
            var result = new HVRVariableNetworkingPacket_NewVariables();

            // General Variables
            var countGeneral = (ushort)(array[offset++] | (array[offset++] << 8));
            result.newGeneralVariables = new List<HVRVariableNetworkingPacket_NewVariable>(countGeneral);
            for (int i = 0; i < countGeneral; i++)
            {
                var addressLength = (ushort)(array[offset++] | (array[offset++] << 8));
                var address = Encoding.UTF8.GetString(array, offset, addressLength);
                offset += addressLength;

                var networkId = (ushort)(array[offset++] | (array[offset++] << 8));
                var variableTypeCode = array[offset++];
                var initialValue = BitConverter.ToSingle(array, offset);
                offset += 4;

                result.newGeneralVariables.Add(new HVRVariableNetworkingPacket_NewVariable
                {
                    address = address,
                    networkId = networkId,
                    variableTypeCode = variableTypeCode,
                    initialValue = initialValue
                });
            }

            // Float Zero Variables
            var countZero = (ushort)(array[offset++] | (array[offset++] << 8));
            result.floatZero = new List<HVRVariableNetworkingPacket_NewQuickVariable>(countZero);
            for (int i = 0; i < countZero; i++)
            {
                var addressLength = (ushort)(array[offset++] | (array[offset++] << 8));
                var address = Encoding.UTF8.GetString(array, offset, addressLength);
                offset += addressLength;

                var networkId = (ushort)(array[offset++] | (array[offset++] << 8));
                result.floatZero.Add(new HVRVariableNetworkingPacket_NewQuickVariable
                {
                    address = address,
                    networkId = networkId
                });
            }

            // Float One Variables
            var countOne = (ushort)(array[offset++] | (array[offset++] << 8));
            result.floatOne = new List<HVRVariableNetworkingPacket_NewQuickVariable>(countOne);
            for (int i = 0; i < countOne; i++)
            {
                var addressLength = (ushort)(array[offset++] | (array[offset++] << 8));
                var address = Encoding.UTF8.GetString(array, offset, addressLength);
                offset += addressLength;

                var networkId = (ushort)(array[offset++] | (array[offset++] << 8));
                result.floatOne.Add(new HVRVariableNetworkingPacket_NewQuickVariable
                {
                    address = address,
                    networkId = networkId
                });
            }

            return result;
        }
    }

    internal class HVRVariableNetworkingPacket_UpdatedVariables_ZeroesOrOnes
    {
        public byte packetType;
        public List<ushort> networkIds;

        public byte[] Serialize()
        {
            var totalLength = 1 + 2; // packetType + count
            totalLength += networkIds.Count * 2; // each networkId is ushort (2 bytes)

            var result = new byte[totalLength];
            result[0] = packetType;
            var offset = 1;

            var count = (ushort)networkIds.Count;
            result[offset++] = (byte)(count & 0xFF);
            result[offset++] = (byte)((count >> 8) & 0xFF);

            foreach (var networkId in networkIds)
            {
                result[offset++] = (byte)(networkId & 0xFF);
                result[offset++] = (byte)((networkId >> 8) & 0xFF);
            }

            return result;
        }

        public static HVRVariableNetworkingPacket_UpdatedVariables_ZeroesOrOnes Deserialize(ArraySegment<byte> data, byte packetType)
        {
            var offset = data.Offset;
            var array = data.Array;

            var receivedPacketType = array[offset++];
            var count = (ushort)(array[offset++] | (array[offset++] << 8));

            var result = new HVRVariableNetworkingPacket_UpdatedVariables_ZeroesOrOnes
            {
                packetType = packetType,
                networkIds = new List<ushort>(count)
            };

            for (int i = 0; i < count; i++)
            {
                result.networkIds.Add((ushort)(array[offset++] | (array[offset++] << 8)));
            }

            return result;
        }
    }

    internal class HVRVariableNetworkingPacket_UpdatedVariables_ZeroesAndOnes
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_ZeroesAndOnes;
        public ushort numberOfZeroes;
        public List<ushort> networkIds;

        public byte[] Serialize()
        {
            var totalLength = 1 + 1 + 2 + 2; // packetType + numberOfZeroes + count
            totalLength += networkIds.Count * 2; // each networkId is ushort (2 bytes)

            var result = new byte[totalLength];
            result[0] = packetType;
            var offset = 1;

            result[offset++] = (byte)(numberOfZeroes & 0xFF);
            result[offset++] = (byte)((numberOfZeroes >> 8) & 0xFF);

            var count = (ushort)networkIds.Count;
            result[offset++] = (byte)(count & 0xFF);
            result[offset++] = (byte)((count >> 8) & 0xFF);

            foreach (var networkId in networkIds)
            {
                result[offset++] = (byte)(networkId & 0xFF);
                result[offset++] = (byte)((networkId >> 8) & 0xFF);
            }

            return result;
        }

        public static HVRVariableNetworkingPacket_UpdatedVariables_ZeroesAndOnes Deserialize(ArraySegment<byte> data)
        {
            var offset = data.Offset;
            var array = data.Array;

            var packetType = array[offset++];
            var numberOfZeroes = (ushort)(array[offset++] | (array[offset++] << 8));
            var count = (ushort)(array[offset++] | (array[offset++] << 8));

            var result = new HVRVariableNetworkingPacket_UpdatedVariables_ZeroesAndOnes
            {
                numberOfZeroes = numberOfZeroes,
                networkIds = new List<ushort>(count)
            };

            for (int i = 0; i < count; i++)
            {
                result.networkIds.Add((ushort)(array[offset++] | (array[offset++] << 8)));
            }

            return result;
        }
    }

    internal class HVRVariableNetworkingPacket_UpdatedVariables_Mixed
    {
        public readonly byte packetType = AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Mixed;
        public ushort numberOfZeroes;
        public List<ushort> networkIds;
        public List<HVRVariableNetworkingPacket_UpdatedValue> other;

        internal class HVRVariableNetworkingPacket_UpdatedValue
        {
            public ushort networkId;
            public object value;
        }

        public byte[] Serialize()
        {
            var totalLength = 1 + 1 + 2 + 2; // packetType + numberOfZeroes + count
            totalLength += networkIds.Count * 2; // each networkId is ushort (2 bytes)
            totalLength += 2; // count of 'other'
            totalLength += other.Count * (2 + 4); // each has networkId (2 bytes) + float value (4 bytes)

            var result = new byte[totalLength];
            result[0] = packetType;
            var offset = 1;

            result[offset++] = (byte)(numberOfZeroes & 0xFF);
            result[offset++] = (byte)((numberOfZeroes >> 8) & 0xFF);

            var count = (ushort)networkIds.Count;
            result[offset++] = (byte)(count & 0xFF);
            result[offset++] = (byte)((count >> 8) & 0xFF);

            foreach (var networkId in networkIds)
            {
                result[offset++] = (byte)(networkId & 0xFF);
                result[offset++] = (byte)((networkId >> 8) & 0xFF);
            }

            var otherCount = (ushort)other.Count;
            result[offset++] = (byte)(otherCount & 0xFF);
            result[offset++] = (byte)((otherCount >> 8) & 0xFF);

            foreach (var updatedValue in other)
            {
                result[offset++] = (byte)(updatedValue.networkId & 0xFF);
                result[offset++] = (byte)((updatedValue.networkId >> 8) & 0xFF);

                var floatValue = (float)updatedValue.value;
                var valueBytes = BitConverter.GetBytes(floatValue);
                Buffer.BlockCopy(valueBytes, 0, result, offset, 4);
                offset += 4;
            }

            return result;
        }

        public static HVRVariableNetworkingPacket_UpdatedVariables_Mixed Deserialize(ArraySegment<byte> data)
        {
            var offset = data.Offset;
            var array = data.Array;

            var packetType = array[offset++];
            var numberOfZeroes = (ushort)(array[offset++] | (array[offset++] << 8));
            var networkIdsCount = (ushort)(array[offset++] | (array[offset++] << 8));

            var result = new HVRVariableNetworkingPacket_UpdatedVariables_Mixed
            {
                numberOfZeroes = numberOfZeroes,
                networkIds = new List<ushort>(networkIdsCount)
            };

            for (int i = 0; i < networkIdsCount; i++)
            {
                result.networkIds.Add((ushort)(array[offset++] | (array[offset++] << 8)));
            }

            var otherCount = (ushort)(array[offset++] | (array[offset++] << 8));
            result.other = new List<HVRVariableNetworkingPacket_UpdatedValue>(otherCount);

            for (int i = 0; i < otherCount; i++)
            {
                var networkId = (ushort)(array[offset++] | (array[offset++] << 8));
                var floatValue = BitConverter.ToSingle(array, offset);
                offset += 4;

                result.other.Add(new HVRVariableNetworkingPacket_UpdatedValue
                {
                    networkId = networkId,
                    value = floatValue
                });
            }

            return result;
        }
    }

    internal class HVRVariableNetworkingPacket_UpgradeFloatToHighFrequency
    {
        public List<HVRVariableNetworkingPacket_UpgradeToHighFrequency_Item> items;

        internal class HVRVariableNetworkingPacket_UpgradeToHighFrequency_Item
        {
            public ushort networkId;
            public float min;
            public float max;
        }
    }
}
