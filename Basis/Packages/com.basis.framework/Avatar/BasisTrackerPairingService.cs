using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Device_Management.Devices.Pairing;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Avatar
{
    /// <summary>
    /// Bridges <see cref="BasisTrackerPairing"/> (string-keyed pairing graph) to
    /// the runtime device list. Listens for pairing changes and device list
    /// changes, and reconciles the set of <see cref="BasisVirtualMidpointInput"/>
    /// instances so that exactly one virtual exists per pair whose both partners
    /// are currently connected.
    /// </summary>
    public static class BasisTrackerPairingService
    {
        // Key: ordinal-sorted "idA|idB". Value: the live virtual driving that pair.
        private static readonly Dictionary<string, BasisVirtualMidpointInput> _virtuals = new();
        private static GameObject _root;
        private static bool _initialized;
        private static bool _devicesEventBound;
        // Reentrancy guard — TryAdd / Remove on AllInputDevices fire OnListChanged,
        // and we listen to that. Without the guard, creating one virtual would
        // recursively trigger another reconcile pass mid-iteration.
        private static bool _reconciling;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            BasisTrackerPairing.OnPairingsChanged += Reconcile;
            BasisDeviceManagement.OnInitializationCompleted += BindDeviceListEvent;

            // BasisDeviceManagement may already be initialized when we attach
            // (depending on script execution order vs runtime callbacks), so try
            // a direct bind too — BindDeviceListEvent is idempotent.
            BindDeviceListEvent();
        }

        private static void BindDeviceListEvent()
        {
            if (_devicesEventBound) return;
            BasisDeviceManagement instance = BasisDeviceManagement.Instance;
            if (instance == null) return;
            instance.AllInputDevices.OnListChanged += Reconcile;
            _devicesEventBound = true;
            Reconcile();
        }

        private static void Reconcile()
        {
            if (_reconciling) return;
            BasisDeviceManagement instance = BasisDeviceManagement.Instance;
            if (instance == null) return;

            _reconciling = true;
            try
            {
                BasisObservableList<BasisInput> devices = instance.AllInputDevices;

                // Snapshot the set of pair keys whose both partners are currently
                // connected. Pairings against a missing partner go dormant — when
                // the partner reconnects, the next reconcile creates the virtual.
                HashSet<string> validKeys = new HashSet<string>();
                Dictionary<string, (BasisInput a, BasisInput b)> pairLookup = new Dictionary<string, (BasisInput, BasisInput)>();
                foreach (KeyValuePair<string, string> pair in BasisTrackerPairing.EnumerateCanonicalPairs())
                {
                    BasisInput a = FindInput(devices, pair.Key);
                    BasisInput b = FindInput(devices, pair.Value);
                    if (a == null || b == null) continue;
                    string key = KeyFor(pair.Key, pair.Value);
                    validKeys.Add(key);
                    pairLookup[key] = (a, b);
                }

                // Tear down virtuals whose pair is no longer valid (unpaired,
                // partner disconnected, or pair replaced by Link()).
                List<string> stale = null;
                foreach (KeyValuePair<string, BasisVirtualMidpointInput> kvp in _virtuals)
                {
                    if (!validKeys.Contains(kvp.Key))
                    {
                        (stale ??= new List<string>()).Add(kvp.Key);
                    }
                }
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        TeardownVirtual(stale[i], devices);
                    }
                }

                // Create virtuals for new pairs.
                foreach (string key in validKeys)
                {
                    if (_virtuals.ContainsKey(key)) continue;
                    (BasisInput a, BasisInput b) = pairLookup[key];
                    CreateVirtual(key, a, b, instance);
                }
            }
            finally
            {
                _reconciling = false;
            }
        }

        private static BasisInput FindInput(BasisObservableList<BasisInput> devices, string id)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                BasisInput input = devices[i];
                if (input == null) continue;
                if (input is BasisVirtualMidpointInput) continue;
                if (input.UniqueDeviceIdentifier == id) return input;
            }
            return null;
        }

        private static void CreateVirtual(string key, BasisInput a, BasisInput b, BasisDeviceManagement instance)
        {
            if (_root == null)
            {
                _root = new GameObject("BasisTrackerPairingVirtuals");
                Object.DontDestroyOnLoad(_root);
            }

            GameObject go = new GameObject("VirtualMidpoint_" + key);
            // Initial parent under our root; Initialize will reparent under
            // PartnerA's parent so the local-pose math matches.
            go.transform.SetParent(_root.transform, false);
            BasisVirtualMidpointInput virt = go.AddComponent<BasisVirtualMidpointInput>();
            virt.Initialize(a, b);
            instance.TryAdd(virt);
            _virtuals[key] = virt;
        }

        private static void TeardownVirtual(string key, BasisObservableList<BasisInput> devices)
        {
            if (!_virtuals.TryGetValue(key, out BasisVirtualMidpointInput virt))
            {
                return;
            }
            _virtuals.Remove(key);
            if (virt == null)
            {
                return;
            }
            devices.Remove(virt);
            virt.Teardown();
            Object.Destroy(virt.gameObject);
        }

        private static string KeyFor(string a, string b)
        {
            return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        }
    }
}
