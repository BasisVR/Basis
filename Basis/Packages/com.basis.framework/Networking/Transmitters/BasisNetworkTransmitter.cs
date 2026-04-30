using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Avatar;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [DefaultExecutionOrder(15001)]
    [System.Serializable]
    public class BasisNetworkTransmitter : BasisNetworkPlayer
    {
        public bool HasEvents = false;

        [SerializeField]
        public BasisAudioTransmission AudioTransmission = new BasisAudioTransmission();

        [SerializeField]
        public BasisStoredAvatarData storedAvatarData = new BasisStoredAvatarData();

        [SerializeField]
        public BasisTransmissionResults TransmissionResults = new BasisTransmissionResults();

        public NetDataWriter AvatarSendWriter = new NetDataWriter(true, BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.High) + 2);
        public Dictionary<byte, AdditionalAvatarData> SendingOutAvatarData = new Dictionary<byte, AdditionalAvatarData>();

        public static Action AfterAvatarChanges;
        public BasisNetworkTransmitter(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }
        public void AddAdditional(AdditionalAvatarData AvatarData) => SendingOutAvatarData[AvatarData.messageIndex] = AvatarData;
        public void ClearAdditional() => SendingOutAvatarData.Clear();
        public override void Initialize()
        {
            TransmissionResults.Initalize();
            AudioTransmission.Initialize(this);
            OnAvatarCalibrationLocal();

            if (!HasEvents)
            {
                Player.OnAvatarSwitched += OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched += SendOutAvatarChange;
                AfterAvatarChanges += TransmissionResults.Simulate;
                HasEvents = true;
            }
        }
        public override void DeInitialize()
        {
            TransmissionResults.DeInitalize();
            AudioTransmission?.DeInitialize();

            if (HasEvents)
            {
                Player.OnAvatarSwitched -= OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched -= SendOutAvatarChange;
                AfterAvatarChanges -= TransmissionResults.Simulate;
                HasEvents = false;
            }
            BasisRemoteFaceManagement.Dispose();
        }

        public static NetDataWriter AvatarChangeWriter = new NetDataWriter();
        private static readonly object AvatarChangeWriterLock = new object();

        public void SendOutAvatarChange()
        {
            LastLinkedAvatarIndex = (byte)((LastLinkedAvatarIndex + 1) % (byte.MaxValue + 1));

            if (!BasisAvatarFactory.EnsureAvatarNetworkGUID(Player.BasisAvatar, out string avatarNetworkGuid))
            {
                BasisDebug.LogError("Unable to send avatar change because the local avatar has no network GUID.", BasisDebug.LogTag.Networking);
                return;
            }

            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                byteArray = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(Player.AvatarMetaData),
                loadMode = Player.AvatarLoadMode,
                LocalAvatarIndex = LastLinkedAvatarIndex,
                AvatarNetworkGuid = avatarNetworkGuid,
            };
            lock (AvatarChangeWriterLock)
            {
                AvatarChangeWriter.Reset();
                ClientAvatarChangeMessage.Serialize(AvatarChangeWriter);
                BasisNetworkConnection.LocalPlayerPeer.Send(AvatarChangeWriter, BasisNetworkCommons.AvatarChangeMessageChannel, DeliveryMethod.ReliableOrdered);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarChange, AvatarChangeWriter.Length);
            }
        }
    }
}
