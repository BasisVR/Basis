using Basis.Network.Core.Compression;
using Basis.Scripts.Behaviour;
using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Networking.NetworkedAvatar
{
    /// <summary>
    /// Network-synced blendshape component for avatars.
    /// Piggybacks on player movement (AdditionalAvatarData, Channel 2, unreliable).
    ///
    /// Automatically filters out viseme/blink/laughter blendshapes — those are
    /// reconstructed remotely by OpenLipSync and BasisRemoteFaceManagement.
    /// Only face-tracking blendshapes are synced.
    ///
    /// Timing is driven by BasisBlendShapeDriver (Simulate/Apply pattern),
    /// NOT by MonoBehaviour Update/LateUpdate.
    ///
    /// Hooks:
    ///   SetWeight(filteredIndex, weight) — set a synced blendshape from code
    ///   GetWeight(filteredIndex)         — read current weight
    ///   OnWeightsReceived               — fires on remote side after decode
    ///   SyncedIndices / SyncedCount     — introspect what's being synced
    /// </summary>
    public class BasisBlendShapeSync : BasisAvatarMonoBehaviour
    {
        [Tooltip("SkinnedMeshRenderer to sync. If null, auto-detects from avatar FaceVisemeMesh.")]
        public SkinnedMeshRenderer TargetMesh;

        [Tooltip("Keyframe interval in network ticks. 32 ≈ 1.6s at 50ms tick rate.")]
        public int KeyframeInterval = BasisBlendShapePacking.DefaultKeyframeInterval;

        // ---- Public state ----

        /// <summary>True once initialization completes and the component is actively syncing.</summary>
        public bool IsReady { get; private set; }

        /// <summary>True if this is the local player's instance (sender).</summary>
        public bool IsLocal { get; private set; }

        /// <summary>True when remote data has been decoded but not yet applied to the mesh.</summary>
        public bool HasPendingData { get; private set; }

        /// <summary>Number of blendshapes being synced (excludes viseme/blink/laughter).</summary>
        public byte SyncedCount { get; private set; }

        /// <summary>Mesh blendshape indices being synced. Index into this with filtered indices.</summary>
        public int[] SyncedIndices { get; private set; }

        /// <summary>Fires on the remote side after weights are decoded from the network.</summary>
        public Action OnWeightsReceived;

        // ---- Sender state ----
        private byte[] _senderCurrent;
        private byte[] _senderPrevious;
        private byte[] _encodeBuffer;
        private byte[] _payload;
        private int _frameCounter;

        // ---- Receiver state ----
        private byte[] _receiverState;
        private float[] _appliedWeights;

        // ---- Mesh info ----
        private int _meshBlendShapeCount;

        // ================================================================
        // Lifecycle
        // ================================================================

        public override void OnNetworkReady(bool IsLocallyOwned)
        {
            IsLocal = IsLocallyOwned;

            // Auto-detect mesh if not assigned
            if (TargetMesh == null)
            {
                BasisAvatar avatar = NetworkedPlayer?.Player?.BasisAvatar;
                if (avatar != null)
                {
                    TargetMesh = avatar.FaceVisemeMesh != null ? avatar.FaceVisemeMesh : avatar.FaceBlinkMesh;
                }
            }

            if (TargetMesh == null || TargetMesh.sharedMesh == null)
            {
                IsReady = false;
                return;
            }

            _meshBlendShapeCount = TargetMesh.sharedMesh.blendShapeCount;
            if (_meshBlendShapeCount == 0)
            {
                IsReady = false;
                return;
            }

            BuildFilteredIndices();

            if (SyncedCount == 0)
            {
                IsReady = false;
                return;
            }

            if (IsLocal)
            {
                _senderCurrent = new byte[SyncedCount];
                _senderPrevious = new byte[SyncedCount];
                _encodeBuffer = new byte[BasisBlendShapePacking.MaxEncodedSize(SyncedCount)];
                _payload = null;
                _frameCounter = 0;
            }
            else
            {
                _receiverState = new byte[SyncedCount];
                _appliedWeights = new float[SyncedCount];
                HasPendingData = false;
            }

            IsReady = true;
            BasisBlendShapeDriver.Register(this);
        }

        void OnDestroy()
        {
            if (IsReady)
                BasisBlendShapeDriver.Unregister(this);
            IsReady = false;
        }

        // ================================================================
        // Filtering — exclude blendshapes handled by other remote systems
        // ================================================================

        private void BuildFilteredIndices()
        {
            BasisAvatar avatar = NetworkedPlayer?.Player?.BasisAvatar;
            var excluded = new HashSet<int>();

            if (avatar != null)
            {
                // Exclude viseme shapes (reconstructed by remote OpenLipSync)
                if (TargetMesh == avatar.FaceVisemeMesh && avatar.FaceVisemeMovement != null)
                {
                    for (int i = 0; i < avatar.FaceVisemeMovement.Length; i++)
                    {
                        if (avatar.FaceVisemeMovement[i] >= 0)
                            excluded.Add(avatar.FaceVisemeMovement[i]);
                    }
                    if (avatar.laughterBlendTarget >= 0)
                        excluded.Add(avatar.laughterBlendTarget);
                }

                // Exclude blink shapes (reconstructed by BasisRemoteFaceManagement)
                if (TargetMesh == avatar.FaceBlinkMesh && avatar.BlinkViseme != null)
                {
                    for (int i = 0; i < avatar.BlinkViseme.Length; i++)
                    {
                        if (avatar.BlinkViseme[i] >= 0)
                            excluded.Add(avatar.BlinkViseme[i]);
                    }
                }
            }

            // Build filtered list: everything on this mesh that isn't excluded
            var indices = new List<int>();
            for (int i = 0; i < _meshBlendShapeCount && indices.Count < 255; i++)
            {
                if (!excluded.Contains(i))
                    indices.Add(i);
            }

            SyncedIndices = indices.ToArray();
            SyncedCount = (byte)indices.Count;
        }

        // ================================================================
        // Local hooks — for code to drive blendshape values
        // ================================================================

        /// <summary>Set a synced blendshape weight by its filtered index [0..SyncedCount).</summary>
        public void SetWeight(byte filteredIndex, float weight)
        {
            if (!IsReady || filteredIndex >= SyncedCount) return;
            TargetMesh.SetBlendShapeWeight(SyncedIndices[filteredIndex], weight);
        }

        /// <summary>Set a blendshape weight by its mesh index. No-op if this index isn't synced.</summary>
        public void SetWeightByMeshIndex(int meshIndex, float weight)
        {
            if (!IsReady || meshIndex < 0 || meshIndex >= _meshBlendShapeCount) return;
            TargetMesh.SetBlendShapeWeight(meshIndex, weight);
        }

        /// <summary>Get current weight by filtered index.</summary>
        public float GetWeight(byte filteredIndex)
        {
            if (!IsReady || filteredIndex >= SyncedCount) return 0f;
            return TargetMesh.GetBlendShapeWeight(SyncedIndices[filteredIndex]);
        }

        /// <summary>Get the mesh blendshape index for a filtered index, or -1 if invalid.</summary>
        public int GetMeshIndex(byte filteredIndex)
        {
            if (filteredIndex >= SyncedCount) return -1;
            return SyncedIndices[filteredIndex];
        }

        /// <summary>Get the filtered index for a mesh blendshape index, or 255 if not synced.</summary>
        public byte GetFilteredIndex(int meshIndex)
        {
            for (byte i = 0; i < SyncedCount; i++)
            {
                if (SyncedIndices[i] == meshIndex) return i;
            }
            return byte.MaxValue;
        }

        // ================================================================
        // Sender — called by BasisBlendShapeDriver.Simulate()
        // ================================================================

        /// <summary>
        /// Capture current blendshape weights from the mesh, delta-encode,
        /// and stage for piggyback delivery on the next player movement packet.
        /// Called by BasisBlendShapeDriver, not by Update.
        /// </summary>
        public void CaptureAndEncode()
        {
            // Read only the synced (filtered) shapes from the mesh
            for (int i = 0; i < SyncedCount; i++)
            {
                _senderCurrent[i] = BasisBlendShapePacking.Quantize(
                    TargetMesh.GetBlendShapeWeight(SyncedIndices[i]));
            }

            _frameCounter++;
            bool isKeyframe = (_frameCounter % KeyframeInterval) == 0;

            int written = BasisBlendShapePacking.Encode(
                _encodeBuffer, 0,
                _senderCurrent, _senderPrevious,
                SyncedCount, isKeyframe);

            if (written == 0) return;

            System.Buffer.BlockCopy(_senderCurrent, 0, _senderPrevious, 0, SyncedCount);

            if (_payload == null || _payload.Length != written)
                _payload = new byte[written];
            System.Buffer.BlockCopy(_encodeBuffer, 0, _payload, 0, written);

            ServerReductionSystemMessageSend(_payload);
        }

        // ================================================================
        // Receiver — network callback + apply (called by driver)
        // ================================================================

        /// <summary>
        /// Network callback: decode incoming blendshape data into persistent state.
        /// Actual mesh application happens in ApplyReceivedWeights via the driver.
        /// </summary>
        public override void OnNetworkMessageServerReductionSystem(byte[] buffer)
        {
            if (IsLocal || buffer == null || !IsReady) return;

            if (BasisBlendShapePacking.Decode(
                buffer, 0, buffer.Length,
                _receiverState, SyncedCount,
                out _, out _))
            {
                HasPendingData = true;
            }
        }

        /// <summary>
        /// Apply decoded weights to the mesh. Only touches values that actually changed.
        /// Called by BasisBlendShapeDriver.Apply().
        /// </summary>
        public void ApplyReceivedWeights()
        {
            HasPendingData = false;

            for (int i = 0; i < SyncedCount; i++)
            {
                float weight = BasisBlendShapePacking.Dequantize(_receiverState[i]);
                if (_appliedWeights[i] != weight)
                {
                    _appliedWeights[i] = weight;
                    TargetMesh.SetBlendShapeWeight(SyncedIndices[i], weight);
                }
            }

            OnWeightsReceived?.Invoke();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Auto-configures TargetMesh from the avatar's FaceVisemeMesh or FaceBlinkMesh.
        /// Called by the Avatar SDK inspector when this component is added.
        /// </summary>
        public override void OnEditorSetup(GameObject avatarRoot, BasisAvatar avatar)
        {
            if (avatar == null) return;

            if (avatar.FaceVisemeMesh != null)
                TargetMesh = avatar.FaceVisemeMesh;
            else if (avatar.FaceBlinkMesh != null)
                TargetMesh = avatar.FaceBlinkMesh;
        }
#endif
    }
}
