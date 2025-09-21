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
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using UnityEngine;

namespace Meta.XR.MultiplayerBlocks.Shared
{
    [Serializable]
    internal struct MatchInfo
    {
        internal string RoomId;
        internal string RoomPassword;
        internal string Extra;

        public MatchInfo(string roomId, string roomPassword, string extra = "")
        {
            RoomId = roomId;
            RoomPassword = roomPassword;
            Extra = extra;
        }
    }

    internal static class SerializationUtils
    {
        internal static string SerializeToString<T>(T obj)
        {
            if (obj == null)
                return null;
            var serializer = new DataContractSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, obj);
                var compressed = Compress(ms.ToArray());
                return Convert.ToBase64String(compressed);
            }
        }

        internal static T DeserializeFromString<T>(string base64)
        {
            var bytes = Convert.FromBase64String(base64);
            var decompressed = Decompress(bytes);
            using (var memStream = new MemoryStream())
            {
                var serializer = new DataContractSerializer(typeof(T));
                memStream.Write(decompressed, 0, decompressed.Length);
                memStream.Seek(0, SeekOrigin.Begin);
                var obj = serializer.ReadObject(memStream);
                return (T)obj;
            }
        }
        private static byte[] Compress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }
        private static byte[] Decompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress))
                {
                    deflate.CopyTo(ms);
                }
                return ms.ToArray();
            }
        }
    }

    internal static class CustomMatchmakingUtils
    {
        internal static MatchInfo DecodeMatchInfoWithStruct(string matchInfoString)
        {
            if (string.IsNullOrEmpty(matchInfoString))
            {
                throw new InvalidOperationException($"{nameof(matchInfoString)} can not be null or empty");
            }

            try
            {
                var matchInfo = SerializationUtils.DeserializeFromString<MatchInfo>(matchInfoString);
                return matchInfo;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to decode the matchInfo from string {matchInfoString}, {e}");
                // the decoding is unsuccessful, silently suppress the error because it could be receiving
                // the wrong data (expected for colocation session use case)
                return new MatchInfo();
            }
        }

        internal static string EncodeMatchInfoWithStruct(string roomId, string roomPassword = null, string extra = null)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                throw new InvalidOperationException($"{nameof(roomId)} can not be null or empty");
            }
            var matchInfo = new MatchInfo()
            {
                RoomId = roomId,
                RoomPassword = roomPassword,
                Extra = extra
            };
            return SerializationUtils.SerializeToString(matchInfo);
        }

        public static (string, string) ExtractMatchInfoFromSessionId(string matchSessionId)
        {
            if (string.IsNullOrEmpty(matchSessionId))
            {
                throw new InvalidOperationException($"{nameof(matchSessionId)} can not be null or empty");
            }

            if (!matchSessionId.Contains(":"))
            {
                return (matchSessionId, null); // Return roomId and null if no password is present
            }

            var parts = matchSessionId.Split(':');

            var result = parts.Length switch
            {
                0 => (null, null),
                1 => (parts[0], null),
                _ => (parts[0], parts[1])
            };

            if (result.Item1 == string.Empty)
            {
                result.Item1 = null;
            }

            if (result.Item2 == string.Empty)
            {
                result.Item2 = null;
            }

            return result;
        }

        public static string EncodeMatchInfoToSessionId(string roomId, string roomPassword = null)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                throw new InvalidOperationException($"{nameof(roomId)} can not be null or empty");
            }

            return string.IsNullOrEmpty(roomPassword) ? roomId : $"{roomId}:{roomPassword}";
        }
    }
}
