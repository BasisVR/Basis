using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Network.Core;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Per-client singleton that replicates image pickups. It shares a deterministic network identity across
    /// all clients, so any client's manager can message the others. The server (or the P2P link) relays the
    /// bytes and never stores them: an image exists only while its spawner is connected, and a late joiner is
    /// served by the owner re-sending. Anyone may delete any image for everyone.
    /// </summary>
    public class BasisImagePickupManager : BasisNetworkBehaviour
    {
        public static BasisImagePickupManager Instance { get; private set; }

        private const string FixedNetworkIdentifier = "BasisImagePickupManager";

        private const byte OpSpawn = 1;
        private const byte OpChunk = 2;
        private const byte OpTransform = 3;
        private const byte OpDespawn = 4;

        private sealed class OwnedImage
        {
            public BasisImagePickupObject Object;
            public byte[] CleanPng;
            public int Width;
            public int Height;
            public string OwnerName;
            public float LastSendTime;
            public Vector3 LastPosition;
            public Quaternion LastRotation;
            public float LastScale;
        }

        private sealed class InboundTransfer
        {
            public ushort Sender;
            public Guid Id;
            public byte[] Buffer;
            public bool[] Received;
            public int ReceivedCount;
            public int TotalChunks;
            public int Width;
            public int Height;
            public ushort OwnerId;
            public string OwnerName;
            public float Deadline;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private readonly Dictionary<Guid, BasisImagePickupObject> _images = new();
        private readonly Dictionary<Guid, OwnedImage> _owned = new();
        private readonly Dictionary<Guid, InboundTransfer> _inbound = new();
        private readonly Dictionary<ushort, float> _lastSpawnTimeBySender = new();
        private readonly Dictionary<ushort, int> _imageCountBySender = new();
        private readonly List<Guid> _scratchIds = new();

#if UNITY_EDITOR
        [SerializeField] private string editorTestImagePath;
#endif

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public override void Start()
        {
            AssignNetworkGUIDIdentifier(FixedNetworkIdentifier);
            base.Start();
        }

        public override void OnNetworkReady()
        {
            BasisDebug.Log($"Image pickup manager ready (network id {NetworkID}).");
        }

        /// <summary>Validates a PNG file, spawns the owner pickup locally, and broadcasts it to all peers.</summary>
        public bool SpawnFromFile(string path)
        {
            BasisImageValidationResult result = BasisImageSecurity.ValidateFile(path);
            if (!result.Ok)
            {
                BasisDebug.LogWarning($"Image pickup rejected: {result.Error}");
                return false;
            }

            Guid id = Guid.NewGuid();
            ushort ownerId = BasisNetworkPlayer.LocalPlayer != null ? BasisNetworkPlayer.LocalPlayer.playerId : (ushort)0;
            string ownerName = BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.SafeDisplayName : "Unknown";

            GetSpawnPose(out Vector3 position, out Quaternion rotation);

            var pickup = BasisImagePickupObject.Build(this, id, ownerId, ownerName, true, result.Texture, result.CleanPng, position, rotation);
            _images[id] = pickup;
            _owned[id] = new OwnedImage
            {
                Object = pickup,
                CleanPng = result.CleanPng,
                Width = result.Width,
                Height = result.Height,
                OwnerName = ownerName,
                LastSendTime = 0f,
                LastPosition = position,
                LastRotation = rotation,
                LastScale = 1f,
            };
            IncrementSenderCount(ownerId);

            if (HasNetworkID)
            {
                SendSpawn(id, ownerId, ownerName, result.Width, result.Height, result.CleanPng, position, rotation, null);
                BasisDebug.Log($"Image pickup spawned and replicated ({result.Width}x{result.Height}, {result.CleanPng.Length} bytes).");
            }
            else
            {
                BasisDebug.Log($"Image pickup spawned locally; not connected, so it will not replicate yet ({result.Width}x{result.Height}).");
            }
            return true;
        }

        /// <summary>Removes an image for everyone. Any client may call this for any image.</summary>
        public void RequestDespawn(Guid id)
        {
            if (HasNetworkID)
            {
                SendCustomNetworkEventDirect(EncodeDespawn(id), DeliveryMethod.ReliableOrdered, null);
            }
            RemoveImage(id);
        }

        private void Update()
        {
            if (!HasNetworkID) return;

            float now = Time.unscaledTime;
            float interval = 1f / BasisImagePickupSettings.TransmitTransformHz;

            foreach (KeyValuePair<Guid, OwnedImage> entry in _owned)
            {
                OwnedImage owned = entry.Value;
                if (owned.Object == null) continue;
                if (now - owned.LastSendTime < interval) continue;

                owned.Object.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                float scale = owned.Object.transform.localScale.x;
                bool moved = (position - owned.LastPosition).sqrMagnitude > BasisImagePickupSettings.MovedPositionEpsilon * BasisImagePickupSettings.MovedPositionEpsilon
                    || Quaternion.Angle(rotation, owned.LastRotation) > BasisImagePickupSettings.MovedRotationEpsilonDegrees
                    || Mathf.Abs(scale - owned.LastScale) > BasisImagePickupSettings.MovedScaleEpsilon;
                if (!moved) continue;

                owned.LastSendTime = now;
                owned.LastPosition = position;
                owned.LastRotation = rotation;
                owned.LastScale = scale;
                SendCustomNetworkEventDirect(EncodeTransform(entry.Key, position, rotation, scale), DeliveryMethod.Sequenced, null);
            }

            CleanupExpiredTransfers(now);
        }

        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (player == null || _owned.Count == 0) return;
            ushort[] recipients = { player.playerId };
            ushort ownerId = BasisNetworkPlayer.LocalPlayer != null ? BasisNetworkPlayer.LocalPlayer.playerId : (ushort)0;

            foreach (KeyValuePair<Guid, OwnedImage> entry in _owned)
            {
                OwnedImage owned = entry.Value;
                if (owned.Object == null) continue;
                owned.Object.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                SendSpawn(entry.Key, ownerId, owned.OwnerName, owned.Width, owned.Height, owned.CleanPng, position, rotation, recipients);
            }
        }

        public override void OnPlayerLeft(BasisNetworkPlayer player)
        {
            if (player == null) return;
            ushort left = player.playerId;

            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, BasisImagePickupObject> entry in _images)
            {
                if (entry.Value != null && entry.Value.OwnerId == left) _scratchIds.Add(entry.Key);
            }
            for (int i = 0; i < _scratchIds.Count; i++) RemoveImage(_scratchIds[i]);

            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, InboundTransfer> entry in _inbound)
            {
                if (entry.Value.Sender == left) _scratchIds.Add(entry.Key);
            }
            for (int i = 0; i < _scratchIds.Count; i++) _inbound.Remove(_scratchIds[i]);

            _imageCountBySender.Remove(left);
            _lastSpawnTimeBySender.Remove(left);
        }

        public override void OnDirectNetworkMessage(ushort senderId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer == null || buffer.Length < 1) return;

            using var stream = new MemoryStream(buffer, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            byte opcode = reader.ReadByte();
            try
            {
                switch (opcode)
                {
                    case OpSpawn: HandleSpawn(senderId, reader); break;
                    case OpChunk: HandleChunk(senderId, reader); break;
                    case OpTransform: HandleTransform(senderId, reader); break;
                    case OpDespawn: HandleDespawn(reader); break;
                }
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning($"Image pickup: malformed message from {senderId} ({e.Message}).");
            }
        }

        private void HandleSpawn(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            ushort ownerId = reader.ReadUInt16();
            string ownerName = reader.ReadString();
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int totalBytes = reader.ReadInt32();
            int totalChunks = reader.ReadInt32();
            Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Quaternion rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            if (_images.ContainsKey(id) || _inbound.ContainsKey(id)) return;

            if (!CanAcceptSpawn(senderId, totalBytes, width, height, totalChunks, out string reason))
            {
                BasisDebug.LogWarning($"Image pickup from {senderId} dropped: {reason}.");
                return;
            }

            _lastSpawnTimeBySender[senderId] = Time.unscaledTime;
            _inbound[id] = new InboundTransfer
            {
                Sender = senderId,
                Id = id,
                Buffer = new byte[totalBytes],
                Received = new bool[totalChunks],
                ReceivedCount = 0,
                TotalChunks = totalChunks,
                Width = width,
                Height = height,
                OwnerId = ownerId,
                OwnerName = ownerName,
                Deadline = Time.unscaledTime + BasisImagePickupSettings.InboundTransferTimeoutSeconds,
                Position = position,
                Rotation = rotation,
            };
        }

        private void HandleChunk(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            int chunkIndex = reader.ReadInt32();
            int length = reader.ReadInt32();

            if (!_inbound.TryGetValue(id, out InboundTransfer transfer)) return;
            if (transfer.Sender != senderId) return;
            if (chunkIndex < 0 || chunkIndex >= transfer.TotalChunks) return;
            if (length < 0 || length > BasisImagePickupSettings.ChunkPayloadBytes) return;

            int offset = chunkIndex * BasisImagePickupSettings.ChunkPayloadBytes;
            if (offset < 0 || offset >= transfer.Buffer.Length) return;

            int expectedLength = Mathf.Min(BasisImagePickupSettings.ChunkPayloadBytes, transfer.Buffer.Length - offset);
            if (length != expectedLength) return;

            long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
            if (remainingBytes < length) return;

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length) return;

            if (!transfer.Received[chunkIndex])
            {
                Buffer.BlockCopy(data, 0, transfer.Buffer, offset, length);
                transfer.Received[chunkIndex] = true;
                transfer.ReceivedCount++;
            }

            if (transfer.ReceivedCount >= transfer.TotalChunks) FinalizeTransfer(transfer);
        }

        private void FinalizeTransfer(InboundTransfer transfer)
        {
            _inbound.Remove(transfer.Id);

            BasisImageValidationResult result = BasisImageSecurity.ValidateBytes(transfer.Buffer);
            if (!result.Ok)
            {
                BasisDebug.LogWarning($"Image pickup from {transfer.Sender} failed validation: {result.Error}.");
                return;
            }
            if (_images.ContainsKey(transfer.Id))
            {
                if (result.Texture != null) Destroy(result.Texture);
                return;
            }

            var pickup = BasisImagePickupObject.Build(this, transfer.Id, transfer.OwnerId, transfer.OwnerName, false, result.Texture, result.CleanPng, transfer.Position, transfer.Rotation);
            _images[transfer.Id] = pickup;
            IncrementSenderCount(transfer.OwnerId);
        }

        private void HandleTransform(ushort senderId, BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Quaternion rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float scale = reader.ReadSingle();

            if (_images.TryGetValue(id, out BasisImagePickupObject pickup) && pickup != null && !pickup.IsOwner && pickup.OwnerId == senderId)
            {
                pickup.SetRemoteTarget(position, rotation, scale);
            }
        }

        private void HandleDespawn(BinaryReader reader)
        {
            Guid id = new Guid(reader.ReadBytes(16));
            RemoveImage(id);
        }

        private bool CanAcceptSpawn(ushort sender, int totalBytes, int width, int height, int totalChunks, out string reason)
        {
            reason = null;
            if (!BasisImagePickupSettings.ReceiveEnabled) { reason = "receiving disabled"; return false; }
            if (totalBytes <= 0 || totalBytes > BasisImagePickupSettings.MaxImageBytes) { reason = "size"; return false; }
            if (width <= 0 || height <= 0 || width > BasisImagePickupSettings.MaxDimension || height > BasisImagePickupSettings.MaxDimension) { reason = "dimensions"; return false; }
            if ((long)width * height > BasisImagePickupSettings.MaxTotalPixels) { reason = "pixel budget"; return false; }

            int expectedChunks = (totalBytes + BasisImagePickupSettings.ChunkPayloadBytes - 1) / BasisImagePickupSettings.ChunkPayloadBytes;
            if (totalChunks != expectedChunks) { reason = "chunk count"; return false; }

            float now = Time.unscaledTime;
            if (_lastSpawnTimeBySender.TryGetValue(sender, out float last) && now - last < BasisImagePickupSettings.MinSecondsBetweenSpawnsPerSender) { reason = "rate limit"; return false; }
            if (_imageCountBySender.TryGetValue(sender, out int count) && count >= BasisImagePickupSettings.MaxConcurrentImagesPerSender) { reason = "too many images"; return false; }

            int activeTransfers = 0;
            foreach (InboundTransfer transfer in _inbound.Values)
            {
                if (transfer.Sender == sender) activeTransfers++;
            }
            if (activeTransfers >= BasisImagePickupSettings.MaxInboundTransfersPerSender) { reason = "too many transfers"; return false; }

            return true;
        }

        private void CleanupExpiredTransfers(float now)
        {
            if (_inbound.Count == 0) return;
            _scratchIds.Clear();
            foreach (KeyValuePair<Guid, InboundTransfer> entry in _inbound)
            {
                if (now >= entry.Value.Deadline) _scratchIds.Add(entry.Key);
            }
            for (int i = 0; i < _scratchIds.Count; i++) _inbound.Remove(_scratchIds[i]);
        }

        private void RemoveImage(Guid id)
        {
            if (_images.TryGetValue(id, out BasisImagePickupObject pickup))
            {
                if (pickup != null)
                {
                    DecrementSenderCount(pickup.OwnerId);
                    Destroy(pickup.gameObject);
                }
                _images.Remove(id);
            }
            _owned.Remove(id);
        }

        private void IncrementSenderCount(ushort sender)
        {
            _imageCountBySender.TryGetValue(sender, out int count);
            _imageCountBySender[sender] = count + 1;
        }

        private void DecrementSenderCount(ushort sender)
        {
            if (_imageCountBySender.TryGetValue(sender, out int count))
            {
                if (count <= 1) _imageCountBySender.Remove(sender);
                else _imageCountBySender[sender] = count - 1;
            }
        }

        private void SendSpawn(Guid id, ushort ownerId, string ownerName, int width, int height, byte[] png, Vector3 position, Quaternion rotation, ushort[] recipients)
        {
            int chunkSize = BasisImagePickupSettings.ChunkPayloadBytes;
            int totalChunks = (png.Length + chunkSize - 1) / chunkSize;

            SendCustomNetworkEventDirect(EncodeSpawn(id, ownerId, ownerName, width, height, png.Length, totalChunks, position, rotation), DeliveryMethod.ReliableOrdered, recipients);

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkSize;
                int length = Mathf.Min(chunkSize, png.Length - offset);
                SendCustomNetworkEventDirect(EncodeChunk(id, i, png, offset, length), DeliveryMethod.ReliableOrdered, recipients);
            }
        }

        private static byte[] EncodeSpawn(Guid id, ushort ownerId, string ownerName, int width, int height, int totalBytes, int totalChunks, Vector3 position, Quaternion rotation)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpSpawn);
            writer.Write(id.ToByteArray());
            writer.Write(ownerId);
            writer.Write(ownerName ?? string.Empty);
            writer.Write(width);
            writer.Write(height);
            writer.Write(totalBytes);
            writer.Write(totalChunks);
            WritePose(writer, position, rotation);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] EncodeChunk(Guid id, int chunkIndex, byte[] source, int offset, int length)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpChunk);
            writer.Write(id.ToByteArray());
            writer.Write(chunkIndex);
            writer.Write(length);
            writer.Write(source, offset, length);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] EncodeTransform(Guid id, Vector3 position, Quaternion rotation, float scale)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpTransform);
            writer.Write(id.ToByteArray());
            WritePose(writer, position, rotation);
            writer.Write(scale);
            writer.Flush();
            return stream.ToArray();
        }

        private static byte[] EncodeDespawn(Guid id)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(OpDespawn);
            writer.Write(id.ToByteArray());
            writer.Flush();
            return stream.ToArray();
        }

        private static void WritePose(BinaryWriter writer, Vector3 position, Quaternion rotation)
        {
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            writer.Write(rotation.x);
            writer.Write(rotation.y);
            writer.Write(rotation.z);
            writer.Write(rotation.w);
        }

        private void GetSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            if (BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.Instance != null)
            {
                BasisLocalCameraDriver.Instance.transform.GetPositionAndRotation(out Vector3 cameraPosition, out Quaternion cameraRotation);
                Vector3 forward = cameraRotation * Vector3.forward;
                position = cameraPosition + forward * BasisImagePickupSettings.SpawnDistance;
                rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            else
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Test Spawn From Path")]
        private void EditorTestSpawn()
        {
            if (!string.IsNullOrEmpty(editorTestImagePath)) SpawnFromFile(editorTestImagePath);
        }
#endif
    }
}
