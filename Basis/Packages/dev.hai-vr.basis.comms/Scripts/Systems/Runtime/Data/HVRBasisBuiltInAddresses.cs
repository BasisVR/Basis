using System;
using System.Collections.Generic;
using System.Linq;
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
    public class HVRBasisBuiltInAddresses
    {
        private static readonly int[] _addressIds = new int[BasisOpenLipSyncContext.VisemeCount];
        private static int _addressMax;
        private static bool _addressIdsInitialized;

        private static readonly Dictionary<HVRAvatarComms, List<HVRBasisBuiltInAddresses>> Required = new();
        private static readonly Dictionary<HVRAvatarComms, HVRBasisBuiltInAddressesVisemeFlags> Flags = new();

        private HVRBasisBuiltInAddressesVisemeFlags requiredFlags = 0;

        private HVRAvatarComms _comms;
        private BasisAvatar _avatar;

        private bool _isWearer;
        private BasisNetworkReceiver _remoteReceiver;

        private BasisOpenLipSyncContext _contextNullable;
        private float[] _lastAppliedRef;
        private float[] _lastRead;
        private float _lastMax;
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

            if (!Required.ContainsKey(_comms)) Required[_comms] = new List<HVRBasisBuiltInAddresses>();
            Required[_comms].Add(this);
            Flags[_comms] = requiredFlags;
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
                Flags.Remove(_comms);
            }
            else
            {
                ReaggregateFlags();
            }
        }

        private void ReaggregateFlags()
        {
            Flags[_comms] = Required[_comms].Aggregate((HVRBasisBuiltInAddressesVisemeFlags)0, (current, acquisition) => current | acquisition.requiredFlags);
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

            _contextNullable ??= _isWearer
                ? BasisLocalPlayer.Instance.LocalVisemeDriver.openLipSyncContext
                : _remoteReceiver.AudioReceiverModule.BasisRemoteVisemeAudioDriver.BasisAudioAndVisemeDriver.openLipSyncContext;
            if (_contextNullable == null) return;

            // The context mutates this array in place and never reallocates it, so the reference
            // is fetched once per resolved context rather than every frame.
            _lastAppliedRef ??= _contextNullable.LastApplied;

            var flagsForThisComms = Flags[comms];

            _lastRead ??= new float[BasisOpenLipSyncContext.VisemeCount];

            var max = 0f;
            for (var index = 0; index < _lastRead.Length; index++)
            {
                var lastApplied = _lastAppliedRef[index];
                if (index != 0) // Ignore "sil"
                {
                    max = Mathf.Max(max, lastApplied);
                }
                if ((flagsForThisComms & (HVRBasisBuiltInAddressesVisemeFlags)(1 << index)) != 0)
                {
                    var lastRead = _lastRead[index];

                    if (!Mathf.Approximately(lastApplied, lastRead))
                    {
                        variableStore.SubmitOrDefineDefaultValue(_addressIds[index], lastApplied / 100f);
                        _lastRead[index] = lastApplied;
                    }
                }
            }

            if ((flagsForThisComms & HVRBasisBuiltInAddressesVisemeFlags.Gain) != 0 && !Mathf.Approximately(max, _lastMax))
            {
                variableStore.SubmitOrDefineDefaultValue(_addressMax, max / 100f);
                _lastMax = max;
            }
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
