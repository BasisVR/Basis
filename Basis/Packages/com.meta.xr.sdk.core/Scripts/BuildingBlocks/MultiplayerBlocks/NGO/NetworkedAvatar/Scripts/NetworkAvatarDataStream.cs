/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using Unity.Netcode;

namespace Meta.XR.MultiplayerBlocks.NGO
{
    /// <summary>
    ///     A custom network variable class that stores a variable sized byte array representing an avatar entity
    /// </summary>
    internal class NetworkAvatarDataStream : NetworkVariableBase
    {
        private byte[] _data;
        private bool _isDataDirty;
        public event Action OnDataChanged;

        public NetworkAvatarDataStream(
            NetworkVariableReadPermission readPerm = DefaultReadPerm,
            NetworkVariableWritePermission writePerm = DefaultWritePerm)
            : base(readPerm, writePerm)
        {
        }

        public byte[] Value
        {
            get => _data;
            set
            {
                if (GetBehaviour() && !CanClientWrite(GetBehaviour().NetworkManager.LocalClientId))
                {
                    throw new InvalidOperationException("Client is not allowed to write to this NetworkVariable");
                }

                if (AreEqual(value, _data))
                {
                    return;
                }

                _data = value;
                _isDataDirty = true;

                MarkNetworkBehaviourDirty();
            }
        }

        private static bool AreEqual(IReadOnlyList<byte> byteArray1, IReadOnlyList<byte> byteArray2)
        {
            if (ReferenceEquals(byteArray1, byteArray2))
            {
                return true;
            }

            if (byteArray1 == null || byteArray2 == null)
            {
                return false;
            }

            if (byteArray1.Count != byteArray2.Count)
            {
                return false;
            }

            for (var i = 0; i < byteArray1.Count; i++)
            {
                if (byteArray1[i] != byteArray2[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override void ResetDirty()
        {
            base.ResetDirty();
            _isDataDirty = false;
        }

        public override bool IsDirty()
        {
            return base.IsDirty() || _isDataDirty;
        }

        public override void WriteDelta(FastBufferWriter writer)
        {
            WriteField(writer);
        }

        public override void WriteField(FastBufferWriter writer)
        {
            if (_data == null)
            {
                writer.WriteValueSafe((ushort)0);
                return;
            }

            writer.WriteValueSafe((ushort)_data.Length);
            writer.WriteBytesSafe(_data, _data.Length);
        }

        public override void ReadField(FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort count);

            if (count == 0)
            {
                _data = null;
                return;
            }

            if (_data == null || _data.Length != count)
            {
                _data = new byte[count];
            }

            reader.ReadBytesSafe(ref _data, count);
            OnDataChanged?.Invoke();
        }

        public override void ReadDelta(FastBufferReader reader, bool keepDirtyDelta)
        {
            ReadField(reader);
        }
    }
}
