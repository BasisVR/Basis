#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.Receivers;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Scripts.Platform
{
    [NoAutoStaticsCleanup]
    public static class BasisWebAudio
    {
        public const int Loading = 1, Ready = 2, Failed = 3;
        public const int MicStarting = 1, MicRunning = 2, MicDenied = 3, MicError = 4;
        [DllImport("__Internal")] public static extern int BasisWebAudio_Initialize();
        [DllImport("__Internal")] public static extern int BasisWebAudio_State();
        [DllImport("__Internal")] public static extern int BasisWebAudio_SampleRate();
        [DllImport("__Internal")] public static extern int BasisWebAudio_ContextRunning();
        [DllImport("__Internal")] public static extern int BasisWebAudio_CreateVoice();
        [DllImport("__Internal")] public static extern void BasisWebAudio_DestroyVoice(int id);
        [DllImport("__Internal")] public static extern int BasisWebAudio_PushVoice(int id, float[] buffer, int count);
        [DllImport("__Internal")] public static extern int BasisWebAudio_VoiceQueued(int id);
        [DllImport("__Internal")] public static extern void BasisWebAudio_SetVoice(int id, float x, float y, float z, float gain, float blend);
        [DllImport("__Internal")] public static extern int BasisWebAudio_VoiceStats(int id, float[] output);
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicStart(int chunk, int echoCancellation, int noiseSuppression, int autoGainControl, int deviceIndex);
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicDeviceVersion();
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicDevices(byte[] buffer, int size);
        [DllImport("__Internal")] public static extern void BasisWebAudio_MicStop();
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicState();
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicSampleRate();
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicAvailable();
        [DllImport("__Internal")] public static extern int BasisWebAudio_MicRead(float[] buffer, int count);
    }

    [NoAutoStaticsCleanup]
    public static class BasisWebVoiceOutput
    {
        private const int BlockSize = 512;
        private const float StatsInterval = 10f;
        private static readonly List<BasisRemoteAudioDriver> drivers = new List<BasisRemoteAudioDriver>();
        private static readonly float[] block = new float[BlockSize];
        private static readonly float[] stats = new float[3];
        private static float smoothedDelta = 1f / 60f, statsTimer;
        private static bool voiceVolumeKnown, subscribed, loggedReady;
        private static AudioListener listener;
        public static int SampleRate { get; private set; }

        public static void Register(BasisRemoteAudioDriver driver)
        {
            if (!drivers.Contains(driver)) drivers.Add(driver);
        }

        public static void Unregister(BasisRemoteAudioDriver driver)
        {
            drivers.Remove(driver);
            if (driver.WebVoiceId == 0) return;
            BasisWebAudio.BasisWebAudio_DestroyVoice(driver.WebVoiceId);
            driver.WebVoiceId = 0;
        }

        public static void Tick(float deltaTime)
        {
            int state = BasisWebAudio.BasisWebAudio_State();
            if (state != BasisWebAudio.Ready)
            {
                if (state == 0) BasisWebAudio.BasisWebAudio_Initialize();
                return;
            }
            if (!subscribed)
            {
                subscribed = true;
                SMModuleAudio.VoiceVolume += OnVoiceVolume;
            }
            int rate = BasisWebAudio.BasisWebAudio_SampleRate();
            if (rate <= 0) return;
            SampleRate = rate;
            if (!loggedReady)
            {
                loggedReady = true;
                BasisDebug.Log($"[WebAudio] voice output ready at {rate} Hz", BasisDebug.LogTag.Voice);
            }
            UnityEngine.Profiling.Profiler.BeginSample("BasisWebVoiceOutput");
            BasisAudioReceiver.outputSampleRate = rate;
            smoothedDelta = Mathf.Lerp(smoothedDelta, Mathf.Clamp(deltaTime, 0.001f, 0.25f), 0.1f);
            int target = Mathf.Clamp(Mathf.RoundToInt(rate * (smoothedDelta * 2f + 0.03f)), rate / 25, rate / 4);
            Transform listenerTransform = ListenerTransform();
            Vector3 listenerPosition = listenerTransform != null ? listenerTransform.position : Vector3.zero;
            Vector3 listenerForward = listenerTransform != null ? listenerTransform.forward : Vector3.forward;
            float master = AudioListener.pause ? 0f : AudioListener.volume;
            bool report = (statsTimer += deltaTime) >= StatsInterval;
            if (report) statsTimer = 0f;
            for (int index = drivers.Count - 1; index >= 0; index--)
            {
                BasisRemoteAudioDriver driver = drivers[index];
                if (driver == null)
                {
                    drivers.RemoveAt(index);
                    continue;
                }
                AudioSource source = driver.WebSource;
                BasisAudioReceiver receiver = driver.BasisAudioReceiver;
                if (!driver.Initialized || receiver == null || source == null || !source.enabled)
                {
                    driver.WebActive = false;
                    continue;
                }
                if (driver.WebVoiceId == 0)
                {
                    driver.WebVoiceId = BasisWebAudio.BasisWebAudio_CreateVoice();
                    if (driver.WebVoiceId == 0) continue;
                }
                int id = driver.WebVoiceId;
                if (!driver.WebActive)
                {
                    driver.WebActive = true;
                    if (BasisWebAudio.BasisWebAudio_VoiceStats(id, stats) != 0) driver.WebUnderruns = (int)stats[1];
                }
                else if (driver.WebExtraTarget > 0)
                {
                    driver.WebExtraTarget = Mathf.Max(0, driver.WebExtraTarget - Mathf.CeilToInt(rate * deltaTime * 0.01f));
                }
                int queued = BasisWebAudio.BasisWebAudio_VoiceQueued(id);
                int want = target + driver.WebExtraTarget;
                for (int pulls = 0; queued < want && pulls < 64; pulls++)
                {
                    driver.WebFilterRead(block, 1);
                    BasisWebAudio.BasisWebAudio_PushVoice(id, block, BlockSize);
                    queued += BlockSize;
                }
                float blend = Mathf.Clamp01(source.spatialBlend);
                float gain = master * source.volume;
                if (source.outputAudioMixerGroup != null && voiceVolumeKnown) gain *= SMModuleAudio.ActiveVoiceVolume;
                Vector3 position = driver.transform.position;
                if (blend > 0f)
                {
                    Vector3 offset = position - listenerPosition;
                    gain *= Mathf.Lerp(1f, Rolloff(receiver, source, offset.magnitude), blend);
                    position = listenerPosition + offset - 2f * Vector3.Dot(offset, listenerForward) * listenerForward;
                }
                BasisWebAudio.BasisWebAudio_SetVoice(id, position.x, position.y, position.z, gain, blend);
                if (BasisWebAudio.BasisWebAudio_VoiceStats(id, stats) == 0) continue;
                int underruns = (int)stats[1];
                if (underruns > driver.WebUnderruns)
                {
                    driver.WebUnderruns = underruns;
                    driver.WebExtraTarget = Mathf.Min(driver.WebExtraTarget + rate / 50, rate / 4);
                }
                if (report) BasisDebug.Log($"[WebAudio] voice {id} rms={stats[0]:F4} underruns={underruns} pushedSeconds={stats[2]:F1} target={want} gain={gain:F2}", BasisDebug.LogTag.Voice);
            }
            UnityEngine.Profiling.Profiler.EndSample();
        }

        private static void OnVoiceVolume(float volume)
        {
            voiceVolumeKnown = true;
        }

        private static float Rolloff(BasisAudioReceiver receiver, AudioSource source, float distance)
        {
            if (ReferenceEquals(receiver.audioSource, source)) return receiver.RolloffAt(distance);
            float max = Mathf.Max(0.01f, source.maxDistance), min = Mathf.Max(0.01f, source.minDistance);
            if (source.rolloffMode == AudioRolloffMode.Linear) return Mathf.Clamp01(1f - (distance - min) / Mathf.Max(1e-4f, max - min));
            return min / Mathf.Max(Mathf.Min(distance, max), min);
        }

        private static Transform ListenerTransform()
        {
            if (listener == null)
            {
                listener = BasisLocalCameraDriver.HasInstance && BasisLocalCameraDriver.Instance != null ? BasisLocalCameraDriver.Instance.Listener : null;
                if (listener == null) listener = Object.FindAnyObjectByType<AudioListener>();
            }
            return listener != null ? listener.transform : null;
        }
    }
}
#endif
