using Basis.Scripts.Networking.Receivers;
using SteamAudio;
using System;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Bridges Unity's audio callback to the remote voice pipeline.
    /// For each audio frame, mixes network voice via <see cref="BasisAudioReceiver"/>,
    /// runs viseme analysis, and exposes a tap for any listeners via <see cref="AudioData"/>.
    /// </summary>
    public class BasisRemoteAudioDriver : MonoBehaviour
    {
        /// <summary>
        /// Viseme (lip-sync) analysis driver processing audio samples each frame.
        /// </summary>
        [SerializeReference]
        public BasisAudioAndVisemeDriver BasisAudioAndVisemeDriver = new BasisAudioAndVisemeDriver();

        /// <summary>
        /// Remote audio receiver that decodes and mixes network voice.
        /// </summary>
        [SerializeReference]
        public BasisAudioReceiver BasisAudioReceiver = new BasisAudioReceiver();

        /// <summary>
        /// Optional callback invoked after audio is processed:
        /// <c>float[] samples</c> (interleaved per channel), <c>int channels</c>.
        /// </summary>
        public Action<float[], int> AudioData;

        /// <summary>
        /// True once <see cref="Initialize(BasisAudioAndVisemeDriver)"/> has been called.
        /// </summary>
        public bool Initialized = false;

        /// <summary>
        /// Unity audio callback. Mixes network voice, runs viseme processing,
        /// and notifies <see cref="AudioData"/> listeners.
        /// </summary>
        /// <param name="data">Interleaved PCM buffer provided by Unity.</param>
        /// <param name="channels">Number of channels in <paramref name="data"/>.</param>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (Initialized)
            {
                int length = data.Length;
                BasisAudioReceiver.OnAudioFilterRead(data, channels, length);
                BasisAudioAndVisemeDriver.ProcessAudioSamples(data, channels, length);
                AudioData?.Invoke(data, channels);
            }
        }
        public void OnDestroy()
        {
            if (BasisAudioAndVisemeDriver != null)
            {
                BasisAudioAndVisemeDriver.OnDestroy();
                UnregisterDriver(BasisAudioAndVisemeDriver);
            }
        }
        /// <summary>
        /// Resets this driver for object pooling without destroying the GameObject.
        /// Performs the same cleanup as OnDestroy but keeps the component alive.
        /// </summary>
        public void ResetForPool()
        {
            Initialized = false;
            if (BasisAudioAndVisemeDriver != null)
            {
                BasisAudioAndVisemeDriver.OnDestroy();
                UnregisterDriver(BasisAudioAndVisemeDriver);
            }
            BasisAudioAndVisemeDriver = null;
            BasisAudioReceiver = null;
            AudioData = null;
        }
        /// <summary>
        /// Initializes the driver with a viseme processor and marks it ready.
        /// </summary>
        /// <param name="basisVisemeDriver">The viseme (lip-sync) driver to use.</param>
        public void Initialize(BasisAudioAndVisemeDriver basisVisemeDriver)
        {
            BasisAudioAndVisemeDriver = basisVisemeDriver;
            RegisterDriver(BasisAudioAndVisemeDriver);
            Initialized = true;
        }
        public static void Simulate(float DeltaTime)
        {
            // ActiveDrivers only holds in-range drivers, so the per-tick cost
            // scales with the in-range set instead of the total driver count
            // (matters at 1000+ players where most are out of viseme range).
            int count = ActiveDriversCount;
            var active = ActiveDrivers;
            for (int Index = 0; Index < count; Index++)
            {
                active[Index].Simulate(DeltaTime);
            }

            // Process all pending OpenLipSync contexts in a single batched
            // background task. This replaces per-context Task.Run() which
            // caused thread pool saturation with many players.
            BasisOpenLipSyncContext.ProcessAllPending();
        }
        public static void Apply()
        {
            int count = ActiveDriversCount;
            var active = ActiveDrivers;
            for (int Index = 0; Index < count; Index++)
            {
                active[Index].Apply();
            }
        }

        /// <summary>
        /// All registered viseme drivers. Backed by an array+count instead of List
        /// because Unity's mono BCL lacks CollectionsMarshal.AsSpan(List&lt;T&gt;), so
        /// indexer access pays a getter call per iteration.
        /// </summary>
        public static BasisAudioAndVisemeDriver[] Drivers = new BasisAudioAndVisemeDriver[16];
        public static int DriversCount;

        /// <summary>
        /// Subset of <see cref="Drivers"/> whose <c>InVisemeRange</c> is currently true.
        /// Maintained by <see cref="SetVisemeRange"/> on transition so Simulate/Apply
        /// don't have to scan the full driver list.
        /// </summary>
        public static BasisAudioAndVisemeDriver[] ActiveDrivers = new BasisAudioAndVisemeDriver[16];
        public static int ActiveDriversCount;

        public static void RegisterDriver(BasisAudioAndVisemeDriver driver)
        {
            if (driver == null || driver.RegisteredIndex >= 0) return;

            if (DriversCount == Drivers.Length)
            {
                Array.Resize(ref Drivers, Drivers.Length * 2);
            }
            driver.RegisteredIndex = DriversCount;
            Drivers[DriversCount++] = driver;

            if (driver.InVisemeRange)
            {
                AddToActive(driver);
            }
        }

        public static void UnregisterDriver(BasisAudioAndVisemeDriver driver)
        {
            if (driver == null) return;

            if (driver.ActiveIndex >= 0)
            {
                RemoveFromActive(driver);
            }

            int idx = driver.RegisteredIndex;
            if (idx < 0) return;

            int last = --DriversCount;
            if (idx != last)
            {
                var moved = Drivers[last];
                Drivers[idx] = moved;
                moved.RegisteredIndex = idx;
            }
            Drivers[last] = null;
            driver.RegisteredIndex = -1;
        }

        /// <summary>
        /// Flips the driver's in-range flag and adds/removes it from
        /// <see cref="ActiveDrivers"/> on transition. Call this instead of
        /// writing <c>InVisemeRange</c> directly so the active set stays consistent.
        /// </summary>
        public static void SetVisemeRange(BasisAudioAndVisemeDriver driver, bool inRange)
        {
            if (driver == null || driver.InVisemeRange == inRange) return;
            driver.InVisemeRange = inRange;

            if (!inRange)
            {
                // Zero here, on the transition, because this is the last moment the driver is
                // reachable: Simulate/Apply iterate ActiveDrivers, and the removal below takes this
                // driver out of that set, so nothing will ever tick it back down to rest. Left
                // frozen, the last viseme both shows as a stuck mouth shape and keeps costing a
                // blendshape pass on every frame the renderer draws — Unity only skips shapes whose
                // weight is actually zero.
                driver.ZeroVisemesNow();
            }

            // Only touch the active list if the driver is actually registered;
            // an unregistered driver toggling its flag is a no-op for us.
            if (driver.RegisteredIndex < 0) return;

            if (inRange)
            {
                if (driver.ActiveIndex < 0) AddToActive(driver);
            }
            else
            {
                if (driver.ActiveIndex >= 0) RemoveFromActive(driver);
            }
        }

        private static void AddToActive(BasisAudioAndVisemeDriver driver)
        {
            if (ActiveDriversCount == ActiveDrivers.Length)
            {
                Array.Resize(ref ActiveDrivers, ActiveDrivers.Length * 2);
            }
            driver.ActiveIndex = ActiveDriversCount;
            ActiveDrivers[ActiveDriversCount++] = driver;
        }

        private static void RemoveFromActive(BasisAudioAndVisemeDriver driver)
        {
            int idx = driver.ActiveIndex;
            int last = --ActiveDriversCount;
            if (idx != last)
            {
                var moved = ActiveDrivers[last];
                ActiveDrivers[idx] = moved;
                moved.ActiveIndex = idx;
            }
            ActiveDrivers[last] = null;
            driver.ActiveIndex = -1;
        }
    }
}
