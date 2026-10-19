using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using HVR.Vixxy;
using UnityEngine;

namespace HVR.Basis.Comms
{
    [CreateAssetMenu(menuName = "HVR.Basis/Comms", fileName = "HVRAddress")]
    [NoAutoStaticsCleanup]
    public class HVRAddress : ScriptableObject
    {
        [NonSerialized] public const string SystemAddressPrefix = "@System/";

        public static class System
        {
            public class HVRSystemAddress
            {
                public string address;
                public string localizationName;
                public float min;
                public float max;
                public float defaultValue;
            }

            // ReSharper disable InconsistentNaming
            /// System addresses tied specifically to the representation of one given user.
            [NoAutoStaticsCleanup]
            public static class User
            {
                [NonSerialized] public const string UserAddressPrefix = SystemAddressPrefix + "User/";

                [NonSerialized] public static readonly HVRSystemAddress VoiceGain = new() { address = UserAddressPrefix + nameof(VoiceGain), localizationName = "address.system.user.voicegain", min = 0f, max = 1f };

                [NoAutoStaticsCleanup]
                public static class Viseme
                {
                    [NonSerialized] public const string VisemeAddressPrefix = UserAddressPrefix + "Viseme/";

                    // Below names are based on the Viseme MPEG-4 Standard, except Gain.
                    [NonSerialized] public static readonly HVRSystemAddress sil = new() { address = VisemeAddressPrefix + nameof(sil), localizationName = "address.system.user.viseme.sil", min = 0f, max = 1f, defaultValue = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress PP = new() { address = VisemeAddressPrefix + nameof(PP), localizationName = "address.system.user.viseme.pp", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress FF = new() { address = VisemeAddressPrefix + nameof(FF), localizationName = "address.system.user.viseme.ff", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress TH = new() { address = VisemeAddressPrefix + nameof(TH), localizationName = "address.system.user.viseme.th", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress DD = new() { address = VisemeAddressPrefix + nameof(DD), localizationName = "address.system.user.viseme.dd", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress kk = new() { address = VisemeAddressPrefix + nameof(kk), localizationName = "address.system.user.viseme.kk", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress CH = new() { address = VisemeAddressPrefix + nameof(CH), localizationName = "address.system.user.viseme.ch", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress SS = new() { address = VisemeAddressPrefix + nameof(SS), localizationName = "address.system.user.viseme.ss", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress nn = new() { address = VisemeAddressPrefix + nameof(nn), localizationName = "address.system.user.viseme.nn", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress RR = new() { address = VisemeAddressPrefix + nameof(RR), localizationName = "address.system.user.viseme.rr", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress aa = new() { address = VisemeAddressPrefix + nameof(aa), localizationName = "address.system.user.viseme.aa", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress E = new() { address = VisemeAddressPrefix + nameof(E), localizationName = "address.system.user.viseme.e", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress ih = new() { address = VisemeAddressPrefix + nameof(ih), localizationName = "address.system.user.viseme.ih", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress oh = new() { address = VisemeAddressPrefix + nameof(oh), localizationName = "address.system.user.viseme.oh", min = 0f, max = 1f };
                    [NonSerialized] public static readonly HVRSystemAddress ou = new() { address = VisemeAddressPrefix + nameof(ou), localizationName = "address.system.user.viseme.ou", min = 0f, max = 1f };

                    public static HVRSystemAddress[] All() => new[] { sil, PP, FF, TH, DD, kk, CH, SS, nn, RR, aa, E, ih, oh, ou };
                }

                public static HVRSystemAddress[] All() => Viseme.All().Concat(new [] { VoiceGain }).ToArray();
            }

            /// System addresses tied specifically to the scene that the user is in.
            public static class Scene
            {
            }

            public static HVRSystemAddress[] All() => User.All();
        }
        // ReSharper restore InconsistentNaming

        public string path;

        public string AsPath()
        {
            return !string.IsNullOrWhiteSpace(path) ? path : name;
        }

        [NonSerialized] private static readonly Dictionary<string, int> AddressToIdDict = new();
        [NonSerialized] private static readonly Dictionary<int, string> IdToAddressDict = new(); // TODO: Could probably make a List and stop using _nextId, or make a bidirectional dictionary
        [NonSerialized] private static readonly HashSet<int> SystemAddressIds = new();
        [NonSerialized] private static int _nextId = 1;

        /// Generates a GUID address. Use this is when the string address doesn't matter, and you need an internal identifier to reference a value.
        /// Please store this address, don't call this over and over.
        /// Valid IDs start at 1.
        public static int NewRandomAddress()
        {
            return AddressToId(Guid.NewGuid().ToString());
        }

        /// Returns an ID for that address, storing that address if it was not seen before.
        /// This ID is only valid for the duration of the app's execution; don't store it across app executions.
        /// Valid IDs start at 1.<br/>
        /// You should store the returned value of this somewhere, the whole point of having addresses represented as a number is to
        /// avoid using string references on frequently invoked methods.
        public static int AddressToId(string address)
        {
            if (AddressToIdDict.TryGetValue(address, out var id)) return id;

            var newId = _nextId;
            AddressToIdDict.Add(address, newId);
            IdToAddressDict.Add(newId, address);
            if (IsSystemAddressName(address))
            {
                SystemAddressIds.Add(newId);
            }
            _nextId++;

            return newId;
        }

        /// Returns the string address for an ID that was returned by any method of this class. Throws an exception if that ID was never seen.
        public static string ResolveKnownAddressFromId(int knownAddressId)
        {
            if (IdToAddressDict.TryGetValue(knownAddressId, out var id)) return id;
            throw new IndexOutOfRangeException();
        }

        /// Generates an address in the form of (pathlike@sha1+componentIndexOnThisType).
        public static string GenerateAddressFromPath<T>(T discriminatorComponent, Transform context) where T : Component
        {
            var componentIndex = Array.IndexOf(discriminatorComponent.GetComponents<T>(), discriminatorComponent);
            var path = HVR_VixxyUtil.GenerateRelativeLikePath(context, discriminatorComponent.transform);
            return $"{path}@{HVR_VixxyUtil.SimpleSha1(path)}+{componentIndex}";
        }

        /// Returns true if the address starts with a specific prefix, but the system address itself may not be registered.
        public static bool IsSystemAddressName(string address)
        {
            return address.StartsWith(SystemAddressPrefix);
        }

        /// Returns true if the address for that ID is a system address.
        public static bool IsSystemAddressId(int knownAddressId)
        {
            return SystemAddressIds.Contains(knownAddressId);
        }
    }
}
