using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using HVR.Basis.Comms.HVRUtility;
using UnityEngine;

namespace HVR.Basis.Comms
{
    /// The implementation has been split so that we may separate the "SDK" part from the implementation part if needed.
    [AutoStaticsCleanup]
    public partial class HVRBasisBuiltInAddresses
    {
        [NoAutoStaticsCleanup] private static readonly int[] _addressIds = new int[BasisOpenLipSyncContext.VisemeCount];
        private static int _addressMax;
        private static bool _addressIdsInitialized;

        private static readonly Dictionary<HVRAvatarComms, List<HVRBasisBuiltInAddresses>> Required = new();

        private HVRBasisBuiltInAddressesVisemeFlags requiredFlags = 0;
        private HVRBasisBuiltInAddressesVisemeFlags aggregatedFlags = 0;

        private HVRAvatarComms _comms;
        private BasisAvatar _avatar;

        private bool _isWearer;
        private BasisNetworkReceiver _remoteReceiver;

        private HVRBuiltInAddressPublisher _publisher;
        private bool _firstTick = true;

        public HVRBasisBuiltInAddresses(HVRAvatarComms comms, bool isWearer)
        {
            if (!_addressIdsInitialized)
            {
                _addressIdsInitialized = true;

                _addressIds[0] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.sil.address);
                _addressIds[1] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.PP.address);
                _addressIds[2] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.FF.address);
                _addressIds[3] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.TH.address);
                _addressIds[4] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.DD.address);
                _addressIds[5] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.kk.address);
                _addressIds[6] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.CH.address);
                _addressIds[7] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.SS.address);
                _addressIds[8] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.nn.address);
                _addressIds[9] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.RR.address);
                _addressIds[10] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.aa.address);
                _addressIds[11] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.E.address);
                _addressIds[12] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.ih.address);
                _addressIds[13] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.oh.address);
                _addressIds[14] = HVRAddress.AddressToId(HVRAddress.System.User.Viseme.ou.address);
                _addressMax = HVRAddress.AddressToId(HVRAddress.System.User.VoiceGain.address);
            }

            _comms = comms;
            _avatar = HVRCommsUtil.GetAvatar(_comms);
            _isWearer = isWearer;
            _publisher = new HVRBuiltInAddressPublisher(_addressIds, _addressMax);

            if (!Required.ContainsKey(_comms)) Required[_comms] = new List<HVRBasisBuiltInAddresses>();
            Required[_comms].Add(this);
            ReaggregateFlags();
        }

        public void Destroy()
        {
            if (ReferenceEquals(_comms, null)) return;
            if (!Required.TryGetValue(_comms, out var list)) return;

            list.Remove(this);
            if (list.Count == 0)
            {
                Required.Remove(_comms);
            }
            else
            {
                ReaggregateFlags();
            }
        }

        private void ReaggregateFlags()
        {
            var list = Required[_comms];
            var aggregate = (HVRBasisBuiltInAddressesVisemeFlags)0;
            for (var index = 0; index < list.Count; index++)
            {
                aggregate |= list[index].requiredFlags;
            }
            for (var index = 0; index < list.Count; index++)
            {
                list[index].aggregatedFlags = aggregate;
            }
        }

        public static void Simulate()
        {
            foreach (var commsToRequired in Required)
            {
                commsToRequired.Value[0].ApplyForAllInComms(commsToRequired.Key);
            }
        }

        private void ApplyForAllInComms(HVRAvatarComms comms)
        {
            if (!_isWearer && _remoteReceiver == null && BasisNetworkPlayers.AvatarToPlayer(_avatar, out _, out var netPlayer) && netPlayer is BasisNetworkReceiver netReceiver)
            {
                _remoteReceiver = netReceiver;
            }
            ProcessViseme(comms);
        }

        private void ProcessViseme(HVRAvatarComms comms)
        {
            if (comms == null) return;
            var variableStore = comms.VariableStore;
            if (variableStore == null) return;
            if (_firstTick)
            {
                variableStore.SubmitOrDefineDefaultValue(_addressIds[0], 1f); // sil has a default value of 1
                _firstTick = false;
            }

            if (!_isWearer && _remoteReceiver == null) return;

            var visemeDriver = ResolveVisemeDriver();
            _publisher.Publish(variableStore, visemeDriver?.openLipSyncContext, visemeDriver?.VoiceLevel01 ?? 0f, aggregatedFlags);
        }

        private BasisAudioAndVisemeDriver ResolveVisemeDriver()
        {
            if (_isWearer)
            {
                var localPlayer = BasisLocalPlayer.Instance;
                return localPlayer == null ? null : localPlayer.LocalVisemeDriver;
            }

            var audioReceiver = _remoteReceiver.AudioReceiverModule;
            if (audioReceiver == null) return null;

            var remoteAudioDriver = audioReceiver.BasisRemoteVisemeAudioDriver;
            if (remoteAudioDriver == null) return null;

            return remoteAudioDriver.BasisAudioAndVisemeDriver;
        }

        public void DeclareAllRequired(HashSet<int> systemAddresses)
        {
            requiredFlags = 0;
            if (systemAddresses.Contains(_addressIds[0])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.sil;
            if (systemAddresses.Contains(_addressIds[1])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.PP;
            if (systemAddresses.Contains(_addressIds[2])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.FF;
            if (systemAddresses.Contains(_addressIds[3])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.TH;
            if (systemAddresses.Contains(_addressIds[4])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.DD;
            if (systemAddresses.Contains(_addressIds[5])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.kk;
            if (systemAddresses.Contains(_addressIds[6])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.CH;
            if (systemAddresses.Contains(_addressIds[7])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.SS;
            if (systemAddresses.Contains(_addressIds[8])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.nn;
            if (systemAddresses.Contains(_addressIds[9])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.RR;
            if (systemAddresses.Contains(_addressIds[10])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.aa;
            if (systemAddresses.Contains(_addressIds[11])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.E;
            if (systemAddresses.Contains(_addressIds[12])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.ih;
            if (systemAddresses.Contains(_addressIds[13])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.oh;
            if (systemAddresses.Contains(_addressIds[14])) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.ou;
            if (systemAddresses.Contains(_addressMax)) requiredFlags |= HVRBasisBuiltInAddressesVisemeFlags.Gain;
            ReaggregateFlags();
        }
    }

    /// Publishes the viseme weights an OpenLipSync context last wrote to the face mesh, plus the
    /// player's voice level, out to the variable store, deduplicated against what it published
    /// previously.
    ///
    /// The context reference is NOT stable and must not be cached beyond a single Publish call.
    /// Remote contexts are pooled: the viseme driver disposes one after a few seconds of silence or
    /// when the player leaves viseme range, and allocates a fresh instance on their next utterance.
    /// Holding on to either the context or its LastApplied array leaves this reading a dead,
    /// all-zero array — which is how the viseme addresses came to freeze on remote avatars after
    /// their first pause, while the wearer, whose context is never released, worked.
    ///
    /// Voice gain does not come from the context at all. It used to be the loudest non-"sil"
    /// viseme, which is a lip-shape confidence rather than a loudness — it saturates the moment a
    /// vowel is recognised however quietly it was spoken, and it is 0 for any avatar without a
    /// viseme mesh. It is now the measured level of the voice itself, which is why it is published
    /// on its own and survives every case that leaves the context null.
    public class HVRBuiltInAddressPublisher
    {
        private readonly int[] _addressIds;
        private readonly int _addressMax;

        private BasisOpenLipSyncContext _contextNullable;
        private float[] _lastAppliedRef;
        private float[] _lastRead;
        private float _lastGain;

        public HVRBuiltInAddressPublisher(int[] addressIds, int addressMax)
        {
            _addressIds = addressIds;
            _addressMax = addressMax;
        }

        public BasisOpenLipSyncContext TrackedContext => _contextNullable;

        public void Publish(HVRVariableStore variableStore, BasisOpenLipSyncContext context, float voiceLevel01, HVRBasisBuiltInAddressesVisemeFlags flags)
        {
            PublishVisemes(variableStore, context, flags);
            PublishVoiceGain(variableStore, voiceLevel01, flags);
        }

        private void PublishVisemes(HVRVariableStore variableStore, BasisOpenLipSyncContext context, HVRBasisBuiltInAddressesVisemeFlags flags)
        {
            if (context != _contextNullable)
            {
                if (context == null)
                {
                    RestVisemes(variableStore, flags);
                    return;
                }
                _contextNullable = context;
                _lastAppliedRef = context.LastApplied;
            }
            else if (context == null)
            {
                return;
            }

            _lastRead ??= new float[BasisOpenLipSyncContext.VisemeCount];

            var lastAppliedRef = _lastAppliedRef;
            var lastReadRef = _lastRead;

            for (var index = 0; index < lastAppliedRef.Length; index++)
            {
                if ((flags & (HVRBasisBuiltInAddressesVisemeFlags)(1 << index)) == 0) continue;

                var lastApplied = lastAppliedRef[index];
                if (Mathf.Approximately(lastApplied, lastReadRef[index])) continue;

                variableStore.SubmitOrDefineDefaultValue(_addressIds[index], lastApplied / 100f);
                lastReadRef[index] = lastApplied;
            }
        }

        private void PublishVoiceGain(HVRVariableStore variableStore, float voiceLevel01, HVRBasisBuiltInAddressesVisemeFlags flags)
        {
            if ((flags & HVRBasisBuiltInAddressesVisemeFlags.Gain) == 0) return;

            // Not Mathf.Clamp01: it answers NaN with NaN, and this value lands on a material.
            var gain = voiceLevel01 > 0f ? (voiceLevel01 < 1f ? voiceLevel01 : 1f) : 0f;
            if (Mathf.Approximately(gain, _lastGain)) return;

            variableStore.SubmitOrDefineDefaultValue(_addressMax, gain);
            _lastGain = gain;
        }

        /// Matches the ZeroVisemes the driver runs on its way out, so a mouth shape doesn't stay
        /// stuck mid-word for as long as the player is quiet or out of range.
        private void RestVisemes(HVRVariableStore variableStore, HVRBasisBuiltInAddressesVisemeFlags flags)
        {
            _contextNullable = null;
            _lastAppliedRef = null;

            var lastReadRef = _lastRead;
            if (lastReadRef == null) return;

            for (var index = 0; index < lastReadRef.Length; index++)
            {
                if (lastReadRef[index] == 0f) continue;

                lastReadRef[index] = 0f;
                if ((flags & (HVRBasisBuiltInAddressesVisemeFlags)(1 << index)) != 0)
                {
                    variableStore.SubmitOrDefineDefaultValue(_addressIds[index], 0f);
                }
            }
        }
    }

    [Flags]
    public enum HVRBasisBuiltInAddressesVisemeFlags
    {
        sil = 1 << 0,
        PP = 1 << 1,
        FF = 1 << 2,
        TH = 1 << 3,
        DD = 1 << 4,
        kk = 1 << 5,
        CH = 1 << 6,
        SS = 1 << 7,
        nn = 1 << 8,
        RR = 1 << 9,
        aa = 1 << 10,
        E = 1 << 11,
        ih = 1 << 12,
        oh = 1 << 13,
        ou = 1 << 14,
        Gain = 1 << 30
    }
}
