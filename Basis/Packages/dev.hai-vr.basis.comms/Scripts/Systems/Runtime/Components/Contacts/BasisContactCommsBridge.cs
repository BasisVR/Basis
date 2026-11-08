using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Contacts;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Contacts;
using HVR.Basis.Comms.OSC;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [NoAutoStaticsCleanup]
    public static class BasisContactCommsBridge
    {
        public const string AvatarParametersPrefix = "/avatar/parameters/";
        public const string HapticsPrefix = AvatarParametersPrefix + "BasisHaptics/";
        public const int MaxParameterLength = 128;
        private static readonly Dictionary<string, int> addressIds = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> oscAddresses = new Dictionary<string, string>();
        private static readonly string[] zoneAddresses = new string[BasisBodyHaptics.ZoneCount];
        private static readonly float[] sentZones = new float[BasisBodyHaptics.ZoneCount];
        private static readonly HapticsOscOutput hapticsOutput = new HapticsOscOutput();
        private static BasisAvatar cachedAvatar;
        private static HVRAvatarComms cachedComms;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            cachedAvatar = null;
            cachedComms = null;
            for (int index = 0; index < sentZones.Length; index++)
            {
                sentZones[index] = -1f;
            }
            BasisContacts.ReceiverChanged -= OnReceiverChanged;
            BasisContacts.ReceiverChanged += OnReceiverChanged;
            BasisBodyHaptics.UnregisterOutput(hapticsOutput);
            BasisBodyHaptics.RegisterOutput(hapticsOutput);
        }

        private static void OnReceiverChanged(BasisContactReceiver receiver)
        {
            string parameter = receiver.parameter;
            if (string.IsNullOrWhiteSpace(parameter) || parameter.Length > MaxParameterLength)
            {
                return;
            }
            BasisAvatar owner = receiver.OwnerAvatar;
            BasisLocalPlayer local = BasisLocalPlayer.Instance;
            if (owner == null || local == null || owner != local.BasisAvatar)
            {
                return;
            }
            if (owner != cachedAvatar || cachedComms == null)
            {
                cachedAvatar = owner;
                cachedComms = HVRCommsUtil.GetComms(owner);
            }
            if (cachedComms != null && cachedComms.VariableStore != null)
            {
                if (!addressIds.TryGetValue(parameter, out int id))
                {
                    id = HVRAddress.AddressToId(parameter);
                    addressIds.Add(parameter, id);
                }
                cachedComms.VariableStore.SubmitOrDefineDefaultValue(id, receiver.Value);
            }
            if (BasisSettingsDefaults.EnableOSC.RawValue)
            {
                if (!oscAddresses.TryGetValue(parameter, out string address))
                {
                    address = AvatarParametersPrefix + parameter.Trim().TrimStart('/');
                    oscAddresses.Add(parameter, address);
                }
                BasisOscService.PublishValue(address, OscData.Float32(receiver.Value));
            }
        }

        private sealed class HapticsOscOutput : IBasisBodyHapticsOutput
        {
            public void OnBodyHapticsChanged(float[] intensities)
            {
                if (!BasisSettingsDefaults.EnableOSC.RawValue)
                {
                    return;
                }
                for (int zone = 0; zone < BasisBodyHaptics.ZoneCount; zone++)
                {
                    float value = intensities[zone];
                    if (value == sentZones[zone])
                    {
                        continue;
                    }
                    sentZones[zone] = value;
                    zoneAddresses[zone] ??= HapticsPrefix + ((BasisBodyHapticZone)zone).ToString();
                    BasisOscService.PublishValue(zoneAddresses[zone], OscData.Float32(value));
                }
            }
        }
    }
}
