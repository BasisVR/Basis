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
        /// True on the driver <see cref="Basis.Scripts.Networking.Receivers.BasisAnnounceAudioDriver"/>
        /// creates for a announcing player. See <see cref="OwnsVisemeTap"/>.
        /// </summary>
        public bool IsAnnounceSource;

        /// <summary>
        /// Whether this driver may feed the shared viseme analyser this callback.
        /// <para>While a player announces, TWO AudioSources point at the same
        /// <see cref="BasisAudioAndVisemeDriver"/>: the announce source carrying their voice, and
        /// their own spatial source, which receives nothing (an announcer transmits on the announce
        /// channel only) and therefore writes a buffer of silence every callback. Feeding both
        /// splices that silence through the mel front-end between real speech — poisoning the
        /// streaming convolution caches the model carries between hops — and races the analyser's
        /// ingest buffer, which is written assuming a single producer. So the announce source takes
        /// the tap on its own for as long as the announce lasts.</para>
        /// <para>Ownership is a match between the source's kind and the player's current mode
        /// rather than "announce wins": that makes it exactly one owner in BOTH directions off a
        /// single volatile read, so the teardown window — where the announce driver still holds a
        /// live viseme driver for the rest of the frame, because Object.Destroy is deferred —
        /// cannot briefly double up.</para>
        /// </summary>
        public bool OwnsVisemeTap(BasisAudioAndVisemeDriver driver)
        {
            return driver != null && IsAnnounceSource == driver.AnnounceActive;
        }

        /// <summary>
        /// Unity audio callback. Mixes network voice, runs viseme processing,
        /// and notifies <see cref="AudioData"/> listeners.
        /// </summary>
        /// <param name="data">Interleaved PCM buffer provided by Unity.</param>
        /// <param name="channels">Number of channels in <paramref name="data"/>.</param>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (Initialized == false)
            {
                return;
            }

            BasisAudioReceiver receiver = BasisAudioReceiver;
            BasisAudioAndVisemeDriver visemeDriver = BasisAudioAndVisemeDriver;
            if (receiver == null || visemeDriver == null)
            {
                return;
            }

            int length = data.Length;
            receiver.OnAudioFilterRead(data, channels, length);
            // Lip-sync reads the untinted voice: ApplySpatialTone runs after it so a
            // talker's viseme classification doesn't change with which way their
            // head is pointing.
            if (OwnsVisemeTap(visemeDriver))
            {
                // Off the receiver, not off `data`: by here the buffer carries the listener's
                // distance, per-player volume and cone attenuation, and a voice level an avatar
                // animates has to describe the talker, not how much of them reached you.
                visemeDriver.VoiceRms = receiver.SourceRms;
                visemeDriver.ProcessAudioSamples(data, channels, length);
            }
            receiver.ApplySpatialTone(data, channels, length);
            AudioData?.Invoke(data, channels);
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
            IsAnnounceSource = false;
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
        public static void Apply(float DeltaTime)
        {
            int count = ActiveDriversCount;
            var active = ActiveDrivers;
            for (int Index = 0; Index < count; Index++)
            {
                active[Index].Apply(DeltaTime);
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
            if (driver == null) return;

            // A announcing player is heard at any distance — that is the whole point of announce — so
            // the distance cutoff must not take their mouth away. This is the one case where
            // "too far to read a mouth shape" and "too far to hear" disagree.
            if (driver.AnnounceActive) inRange = true;

            if (driver.InVisemeRange == inRange) return;
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
