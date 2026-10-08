using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Generic;
using UnityEngine;
using static SerializableBasis;

namespace Basis.Scripts.Networking.Transmitters
{
    [AutoStaticsCleanup]
    [DefaultExecutionOrder(15001)]
    [System.Serializable]
    public partial class BasisNetworkTransmitter : BasisNetworkPlayer
    {
        public bool HasEvents = false;

        [SerializeField]
        public BasisAudioTransmission AudioTransmission = new BasisAudioTransmission();

        [SerializeField]
        public BasisStoredAvatarData storedAvatarData = new BasisStoredAvatarData();

        [SerializeField]
        public BasisTransmissionResults TransmissionResults = new BasisTransmissionResults();

        [System.NonSerialized] public NetDataWriter AvatarSendWriter = new NetDataWriter(true, BasisAvatarBitPacking.ConvertToSize(BasisAvatarBitPacking.BitQuality.High) + 2);
        [System.NonSerialized] public Dictionary<byte, AdditionalAvatarData> SendingOutAvatarData = new Dictionary<byte, AdditionalAvatarData>();

        public static Action AfterAvatarChanges;

        /// <summary>
        /// Fires earlier in LateUpdate than <see cref="AfterAvatarChanges"/> — right after the
        /// camera lands, which is the first point this frame where the head position and gaze the
        /// distance job reads are final. Carries the transmit tick's distance/range job schedule so
        /// that chain runs through the rest of the frame's main-thread work; the AfterAvatarChanges
        /// tick below completes it and consumes the results.
        /// </summary>
        public static Action ScheduleTransmitJobs;
        public BasisNetworkTransmitter(ushort PlayerID)
        {
            playerId = PlayerID;
            hasID = true;
        }
        public void AddAdditional(AdditionalAvatarData AvatarData) => SendingOutAvatarData[AvatarData.messageIndex] = AvatarData;
        public void ClearAdditional() => SendingOutAvatarData.Clear();
        public override void Initialize()
        {
            TransmissionResults.Initialize();
            AudioTransmission.Initialize(this);
            OnAvatarCalibrationLocal();

            if (!HasEvents)
            {
                Player.OnAvatarSwitched += OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched += SendOutAvatarChange;
                ScheduleTransmitJobs += TransmissionResults.ScheduleTick;
                AfterAvatarChanges += TransmissionResults.CompleteTick;
                HasEvents = true;
            }
        }
        public override void DeInitialize()
        {
            TransmissionResults.DeInitialize();
            AudioTransmission?.DeInitialize();

            if (HasEvents)
            {
                Player.OnAvatarSwitched -= OnAvatarCalibrationLocal;
                Player.OnAvatarSwitched -= SendOutAvatarChange;
                ScheduleTransmitJobs -= TransmissionResults.ScheduleTick;
                AfterAvatarChanges -= TransmissionResults.CompleteTick;
                HasEvents = false;
            }
            BasisRemoteFaceManagement.Dispose();
        }

        public static NetDataWriter AvatarChangeWriter = new NetDataWriter();
        [NoAutoStaticsCleanup] private static readonly object AvatarChangeWriterLock = new object();

        public void SendOutAvatarChange()
        {
            LastLinkedAvatarIndex = (byte)((LastLinkedAvatarIndex + 1) % (byte.MaxValue + 1));

            Basis.IK.BasisBodyFitResult fit = Basis.Scripts.Drivers.BasisLocalRigDriver.AppliedBodyFit;
            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage
            {
                byteArray = BasisBundleConversionNetwork.ConvertBasisLoadableBundleToBytes(Player.AvatarMetaData),
                loadMode = Player.AvatarLoadMode,
                LocalAvatarIndex = LastLinkedAvatarIndex,
                // The fit for the avatar being swapped IN is not known until it loads and the rig
                // re-solves; this carries the current one and BasisBodyFitNetworking sends a correction
                // the moment RefreshBodyFit produces a different answer.
                ArmScale = fit.HasArmFit ? fit.ArmScale : 1f,
                LegScale = fit.HasBodyFit ? fit.LegScale : 1f,
                TorsoScale = fit.HasBodyFit ? fit.TorsoScale : 1f,
            };
            lock (AvatarChangeWriterLock)
            {
                AvatarChangeWriter.Reset();
                AvatarChangeWriter.Put(BasisNetworkCommons.AvatarChangeKindFull);
                ClientAvatarChangeMessage.Serialize(AvatarChangeWriter);
                BasisNetworkConnection.LocalPlayerPeer.Send(AvatarChangeWriter, BasisNetworkCommons.AvatarChangeMessageChannel, DeliveryMethod.ReliableOrdered);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarChange, AvatarChangeWriter.Length);
            }
        }

        /// <summary>
        /// Sends a body-fit-only update (no avatar reload on the receiving end). Called from
        /// BasisBodyFitNetworking, which owns the change detection. Returns false when there is no peer
        /// to send to, so the caller knows not to record the value as sent.
        /// </summary>
        public static bool SendOutBodyFit(in Basis.IK.BasisBodyFitResult fit)
        {
            if (BasisNetworkConnection.LocalPlayerPeer == null)
            {
                return false;
            }

            ClientBodyFitMessage message = new ClientBodyFitMessage
            {
                ArmScale = fit.HasArmFit ? fit.ArmScale : 1f,
                LegScale = fit.HasBodyFit ? fit.LegScale : 1f,
                TorsoScale = fit.HasBodyFit ? fit.TorsoScale : 1f,
            };

            lock (AvatarChangeWriterLock)
            {
                AvatarChangeWriter.Reset();
                AvatarChangeWriter.Put(BasisNetworkCommons.AvatarChangeKindBodyFit);
                message.Serialize(AvatarChangeWriter);
                BasisNetworkConnection.LocalPlayerPeer.Send(AvatarChangeWriter, BasisNetworkCommons.AvatarChangeMessageChannel, DeliveryMethod.ReliableOrdered);
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AvatarChange, AvatarChangeWriter.Length);
            }
            return true;
        }
    }
}
