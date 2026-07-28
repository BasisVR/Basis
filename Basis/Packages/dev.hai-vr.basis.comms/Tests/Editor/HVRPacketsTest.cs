using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace HVR.Basis.Comms.Tests
{
    public class HVRPacketsTest
    {
        // - Use the "It should ... packet of type ..." naming for test names.
        // - Addresses are just "TestAddressA", "TestAddressB", etc.
        // - The steps are Given, When, Then.
        // - In the Given, the variables should be called input.
        // - In the When, the variables should be called result.
        // - If there are multiple variables, add a suffix to it.

        [Test]
        public void It_should_throw_exception_when_buffer_exceeded()
        {
            var writer = new HVRNetWriter(false);

            for (var i = 0; i < 4096; i++)
            {
                writer.WriteByte(0);
            }

            Assert.Throws<Exception>(() => writer.WriteByte(0));
        }

        [Test]
        public void It_should_serialize_and_deserialize_packet_of_type_HVRPacket_NewVariables()
        {
            // Given
            var input = new HVRPacket_NewVariables
            {
                newGeneralVariables = new List<HVRPacket_NewVariables.Inner_NewVariable>
                {
                    new() { address = "TestAddressA", networkId = 1, variableTypeCode = 5, initialValue = 1.23f, needsInterpolation = true },
                    new() { address = "TestAddressB", networkId = 3, variableTypeCode = 10, initialValue = -4.56f, needsInterpolation = false },
                },
                floatZero = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>
                {
                    new() { address = "TestAddressC", networkId = 2, needsInterpolation = false}
                },
                floatOne = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>
                {
                    new() { address = "TestAddressD", networkId = 4, needsInterpolation = false },
                    new() { address = "TestAddressE", networkId = 5, needsInterpolation = true },
                }
            };

            // When
            var resultSerialized = input.Serialize(false);
            var success = HVRPacket_NewVariables.TryDeserialize(false, new ArraySegment<byte>(resultSerialized), out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);

            Assert.AreEqual(input.newGeneralVariables.Count, resultDeserialized.newGeneralVariables.Count);
            for (var i = 0; i < input.newGeneralVariables.Count; i++)
            {
                Assert.AreEqual(input.newGeneralVariables[i].address, resultDeserialized.newGeneralVariables[i].address);
                Assert.AreEqual(input.newGeneralVariables[i].networkId, resultDeserialized.newGeneralVariables[i].networkId);
                Assert.AreEqual(input.newGeneralVariables[i].needsInterpolation, resultDeserialized.newGeneralVariables[i].needsInterpolation);
                Assert.AreEqual(input.newGeneralVariables[i].variableTypeCode, resultDeserialized.newGeneralVariables[i].variableTypeCode);
                Assert.AreEqual((float)input.newGeneralVariables[i].initialValue, (float)resultDeserialized.newGeneralVariables[i].initialValue, 0.0001f);
            }

            Assert.AreEqual(input.floatZero.Count, resultDeserialized.floatZero.Count);
            for (var i = 0; i < input.floatZero.Count; i++)
            {
                Assert.AreEqual(input.floatZero[i].address, resultDeserialized.floatZero[i].address);
                Assert.AreEqual(input.floatZero[i].networkId, resultDeserialized.floatZero[i].networkId);
                Assert.AreEqual(input.floatZero[i].needsInterpolation, resultDeserialized.floatZero[i].needsInterpolation);
            }

            Assert.AreEqual(input.floatOne.Count, resultDeserialized.floatOne.Count);
            for (var i = 0; i < input.floatOne.Count; i++)
            {
                Assert.AreEqual(input.floatOne[i].address, resultDeserialized.floatOne[i].address);
                Assert.AreEqual(input.floatOne[i].networkId, resultDeserialized.floatOne[i].networkId);
                Assert.AreEqual(input.floatOne[i].needsInterpolation, resultDeserialized.floatOne[i].needsInterpolation);
            }
        }

        [Test]
        public void It_should_return_false_when_deserializing_packet_with_extra_data()
        {
            // Given
            var input = new HVRPacket_NewVariables
            {
                newGeneralVariables = new List<HVRPacket_NewVariables.Inner_NewVariable>(),
                floatZero = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>(),
                floatOne = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>()
            };
            var serialized = input.Serialize(false);
            var extraData = new byte[serialized.Length + 1];
            Buffer.BlockCopy(serialized, 0, extraData, 0, serialized.Length);
            extraData[serialized.Length] = 0xFF; // Extra byte

            // When
            var success = HVRPacket_NewVariables.TryDeserialize(false, new ArraySegment<byte>(extraData), out _);

            // Then
            Assert.IsFalse(success);
        }

        [Test]
        public void It_should_return_false_when_deserializing_packet_with_missing_data()
        {
            // Given
            var input = new HVRPacket_NewVariables
            {
                newGeneralVariables = new List<HVRPacket_NewVariables.Inner_NewVariable>(),
                floatZero = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>(),
                floatOne = new List<HVRPacket_NewVariables.Inner_NewQuickVariable>()
            };
            var serialized = input.Serialize(false);
            var missingData = new byte[serialized.Length - 1];
            Buffer.BlockCopy(serialized, 0, missingData, 0, serialized.Length - 1);

            // When
            var success = HVRPacket_NewVariables.TryDeserialize(false, new ArraySegment<byte>(missingData), out _);

            // Then
            Assert.IsFalse(success);
        }

        [Test]
        public void It_should_serialize_and_deserialize_packet_of_type_HVRPacket_UpdatedVariables_ZeroesOrOnes()
        {
            // Given
            var input = new HVRPacket_UpdatedVariables_ZeroesOrOnes
            {
                packetType = AvatarMessageProcessing.NewNet_WearerSubmitsUpdatedVariables_Zeroes,
                timingSteps = 11,
                networkIds = new List<ushort> { 1, 2, 3, 4, 5 }
            };

            // When
            var resultSerialized = input.Serialize(false);
            var success = HVRPacket_UpdatedVariables_ZeroesOrOnes.TryDeserialize(false, new ArraySegment<byte>(resultSerialized), input.packetType, out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);
            Assert.AreEqual(input.timingSteps, resultDeserialized.timingSteps);
            Assert.AreEqual(input.networkIds.Count, resultDeserialized.networkIds.Count);
            for (var i = 0; i < input.networkIds.Count; i++)
            {
                Assert.AreEqual(input.networkIds[i], resultDeserialized.networkIds[i]);
            }
        }

        [Test]
        public void It_should_serialize_and_deserialize_packet_of_type_HVRPacket_UpdatedVariables_ZeroesAndOnes()
        {
            // Given
            var input = new HVRPacket_UpdatedVariables_ZeroesAndOnes
            {
                timingSteps = 11,
                numberOfZeroes = 10,
                networkIds = new List<ushort> { 101, 102, 103 }
            };

            // When
            var resultSerialized = input.Serialize(false);
            var success = HVRPacket_UpdatedVariables_ZeroesAndOnes.TryDeserialize(false, new ArraySegment<byte>(resultSerialized), out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);
            Assert.AreEqual(input.timingSteps, resultDeserialized.timingSteps);
            Assert.AreEqual(input.numberOfZeroes, resultDeserialized.numberOfZeroes);
            Assert.AreEqual(input.networkIds.Count, resultDeserialized.networkIds.Count);
            for (var i = 0; i < input.networkIds.Count; i++)
            {
                Assert.AreEqual(input.networkIds[i], resultDeserialized.networkIds[i]);
            }
        }

        [Test]
        public void It_should_serialize_and_deserialize_packet_of_type_HVRPacket_UpdatedVariables_Mixed()
        {
            // Given
            var input = new HVRPacket_UpdatedVariables_Mixed
            {
                timingSteps = 11,
                numberOfZeroes = 5,
                networkIds = new List<ushort> { 10, 20 },
                other = new List<HVRPacket_UpdatedVariables_Mixed.Inner_UpdatedValue>
                {
                    new() { networkId = 30, value = 0.5f },
                    new() { networkId = 40, value = -1.0f }
                }
            };

            // When
            var resultSerialized = input.Serialize(false);
            var success = HVRPacket_UpdatedVariables_Mixed.TryDeserialize(false, new ArraySegment<byte>(resultSerialized), out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);
            Assert.AreEqual(input.timingSteps, resultDeserialized.timingSteps);
            Assert.AreEqual(input.numberOfZeroes, resultDeserialized.numberOfZeroes);
            Assert.AreEqual(input.networkIds.Count, resultDeserialized.networkIds.Count);
            for (var i = 0; i < input.networkIds.Count; i++)
            {
                Assert.AreEqual(input.networkIds[i], resultDeserialized.networkIds[i]);
            }
            Assert.AreEqual(input.other.Count, resultDeserialized.other.Count);
            for (var i = 0; i < input.other.Count; i++)
            {
                Assert.AreEqual(input.other[i].networkId, resultDeserialized.other[i].networkId);
                Assert.AreEqual((float)input.other[i].value, (float)resultDeserialized.other[i].value, 0.0001f);
            }
        }

        [Test]
        public void It_should_serialize_and_deserialize_packet_of_type_HVRPacket_UpgradeFloatToHighFrequency()
        {
            // Given
            var input = new HVRPacket_UpgradeFloatToHighFrequency
            {
                items = new List<HVRPacket_UpgradeFloatToHighFrequency.Inner_Item>
                {
                    new() { networkId = 100, min = 0f, max = 1f },
                    new() { networkId = 200, min = -10f, max = 10f }
                }
            };

            // When
            var resultSerialized = input.Serialize(false);
            var success = HVRPacket_UpgradeFloatToHighFrequency.TryDeserialize(false, new ArraySegment<byte>(resultSerialized), out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);
            Assert.AreEqual(input.items.Count, resultDeserialized.items.Count);
            for (var i = 0; i < input.items.Count; i++)
            {
                Assert.AreEqual(input.items[i].networkId, resultDeserialized.items[i].networkId);
                Assert.AreEqual(input.items[i].min, resultDeserialized.items[i].min, 0.0001f);
                Assert.AreEqual(input.items[i].max, resultDeserialized.items[i].max, 0.0001f);
            }
        }

        [Test]
        public void It_should_serialize_and_deserialize_packet_of_type_HVRPacket_DowngradeFloatToLowFrequency()
        {
            // Given
            var input = new HVRPacket_DowngradeFloatToLowFrequency
            {
                networkIds = new List<ushort> { 123, 234, 251 }
            };

            // When
            var resultSerialized = input.Serialize(false);
            var success = HVRPacket_DowngradeFloatToLowFrequency.TryDeserialize(false, new ArraySegment<byte>(resultSerialized), out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);
            Assert.AreEqual(input.networkIds.Count, resultDeserialized.networkIds.Count);
            for (var i = 0; i < input.networkIds.Count; i++)
            {
                Assert.AreEqual(input.networkIds[i], resultDeserialized.networkIds[i]);
            }
        }

        /// <summary>
        /// The byte-addressed tests above all keep their ids under 256. Once a wearer announces more
        /// variables than that, every address widens to a ushort — this covers that path, and the ids are
        /// deliberately above 255 so a writer that silently narrowed them would drop the high byte.
        /// </summary>
        [Test]
        public void It_should_serialize_and_deserialize_addresses_above_a_byte_when_ushort_addressing_is_on()
        {
            // Given
            var input = new HVRPacket_DowngradeFloatToLowFrequency
            {
                networkIds = new List<ushort> { 123, 456, 789, ushort.MaxValue }
            };

            // When
            var resultSerialized = input.Serialize(true);
            var success = HVRPacket_DowngradeFloatToLowFrequency.TryDeserialize(true, new ArraySegment<byte>(resultSerialized), out var resultDeserialized);

            // Then
            Assert.IsTrue(success);
            Assert.AreEqual(input.packetType, resultDeserialized.packetType);
            Assert.AreEqual(input.networkIds.Count, resultDeserialized.networkIds.Count);
            for (var i = 0; i < input.networkIds.Count; i++)
            {
                Assert.AreEqual(input.networkIds[i], resultDeserialized.networkIds[i]);
            }
        }
    }
}
