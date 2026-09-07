using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;
#if BASIS_HAS_RTAO && !UNITY_ANDROID
using Basis.Rendering.RTAO;
#endif

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// CLI-driven, fully unattended render-pass capture for a standalone Player — no in-game UI click
    /// needed. Inert unless --render-capture-frames is present on the command line. Flags:
    ///   --render-capture-frames[=N]        enables automation; N defaults to BasisRenderProfileHistory.DefaultFrames
    ///   --render-capture-connect=host:port  connects before capturing (skips BasisDeepLinkProvider's
    ///                                       confirmation dialog, which can't be automated)
    ///   --render-capture-gi-quality=Tier    Low/Medium/High/Ultra, applied to
    ///                                       BasisGlobalIlluminationSettings.Current.quality before capture
    ///   --render-capture-rtao-quality=Tier  Low/Medium/High/Ultra, applied via
    ///                                       BasisRTAOFeature.HasQualityOverride/QualityOverride
    /// Always self-terminates (Application.Quit) once the capture is written, so it never idles
    /// connected to a real server.
    /// </summary>
    public static class BasisRenderCaptureAutomation
    {
        private const float PostConnectSettleSeconds = 18f;
        private const float PostCaptureQuitDelaySeconds = 2f;

        private enum State { Idle, WaitingForNetworkInit, WaitingToSettle, Capturing, WaitingToQuit }

        private static State state = State.Idle;
        private static int frames;
        private static string connectHost;
        private static ushort connectPort;
        private static bool hasConnectTarget;
        private static string giQualityOverride;
        private static string rtaoQualityOverride;
        private static float settleStartTime;
        private static float quitStartTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool hasFramesFlag = false;
            string framesValue = null, connectValue = null, qualityValue = null, rtaoQualityValue = null;

            foreach (string arg in args)
            {
                if (arg.StartsWith("--render-capture-frames", StringComparison.OrdinalIgnoreCase))
                {
                    hasFramesFlag = true;
                    framesValue = ValueOf(arg);
                }
                else if (arg.StartsWith("--render-capture-connect=", StringComparison.OrdinalIgnoreCase))
                {
                    connectValue = ValueOf(arg);
                }
                else if (arg.StartsWith("--render-capture-gi-quality=", StringComparison.OrdinalIgnoreCase))
                {
                    qualityValue = ValueOf(arg);
                }
                else if (arg.StartsWith("--render-capture-rtao-quality=", StringComparison.OrdinalIgnoreCase))
                {
                    rtaoQualityValue = ValueOf(arg);
                }
            }

            if (!hasFramesFlag)
            {
                return;
            }

            frames = int.TryParse(framesValue, out int parsedFrames) && parsedFrames > 0
                ? parsedFrames
                : BasisRenderProfileHistory.DefaultFrames;
            giQualityOverride = qualityValue;
            rtaoQualityOverride = rtaoQualityValue;
            hasConnectTarget = TryParseHostPort(connectValue, out connectHost, out connectPort);

            Debug.Log($"[BasisRenderCaptureAutomation] Enabled: frames={frames} connect={(hasConnectTarget ? connectHost + ":" + connectPort : "none")} giQuality={giQualityOverride ?? "unchanged"} rtaoQuality={rtaoQualityOverride ?? "unchanged"}");

            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += OnTick;

            if (!hasConnectTarget)
            {
                BeginSettle();
                return;
            }

            state = State.WaitingForNetworkInit;
            if (BasisNetworkManagement.IsInitialized)
            {
                AttemptConnect();
            }
            else
            {
                BasisNetworkManagement.OnEnableInstanceCreate += AttemptConnect;
            }
        }

        private static void AttemptConnect()
        {
            BasisNetworkManagement.OnEnableInstanceCreate -= AttemptConnect;
            BasisNetworkManagement.Ip = connectHost;
            BasisNetworkManagement.Port = connectPort;
            BasisNetworkManagement.IsHostMode = false;
            Debug.Log($"[BasisRenderCaptureAutomation] Connecting to {connectHost}:{connectPort}...");
            BasisNetworkManagement.Connect();
            BeginSettle();
        }

        private static void BeginSettle()
        {
            settleStartTime = Time.unscaledTime;
            state = State.WaitingToSettle;
        }

        private static void OnTick()
        {
            switch (state)
            {
                case State.WaitingToSettle:
                    // No proven "world fully loaded" signal was found in the time available; this
                    // combines the real signal that exists (local player initialized) with a generous
                    // fixed settle window for remote avatars/world content to stream in afterward.
                    if (BasisLocalPlayerData.PlayerReady && Time.unscaledTime - settleStartTime >= PostConnectSettleSeconds)
                    {
                        BeginCapture();
                    }
                    break;
                case State.WaitingToQuit:
                    if (Time.unscaledTime - quitStartTime >= PostCaptureQuitDelaySeconds)
                    {
                        Debug.Log("[BasisRenderCaptureAutomation] Quitting.");
                        Application.Quit();
                    }
                    break;
            }
        }

        private static void BeginCapture()
        {
            state = State.Capturing;
            ApplyGiQualityOverride();
            ApplyRtaoQualityOverride();

            string reason = "cli-automation-default";
            if (!string.IsNullOrEmpty(giQualityOverride)) reason = "cli-automation-gi-" + giQualityOverride;
            if (!string.IsNullOrEmpty(rtaoQualityOverride)) reason += "-rtao-" + rtaoQualityOverride;
            Debug.Log($"[BasisRenderCaptureAutomation] Capturing {frames} frames, reason={reason}...");

            bool started = BasisRenderProfileHistory.CaptureToDisk(reason, frames);
            if (!started)
            {
                Debug.LogWarning("[BasisRenderCaptureAutomation] CaptureToDisk refused to start (already capturing?). Quitting anyway.");
            }

            BasisFrameClock.OnTick -= OnTick;
            BasisFrameClock.RemoveRequest();
            BasisFrameClock.AddRequest();
            BasisFrameClock.OnTick += WaitForCaptureComplete;
        }

        private static void WaitForCaptureComplete()
        {
            if (BasisRenderProfileHistory.IsCapturing)
            {
                return;
            }

            BasisFrameClock.OnTick -= WaitForCaptureComplete;
            Debug.Log($"[BasisRenderCaptureAutomation] Capture complete. LastWrittenPath={BasisRenderProfileHistory.LastWrittenPath}");

            quitStartTime = Time.unscaledTime;
            state = State.WaitingToQuit;
            BasisFrameClock.OnTick += OnTick;
        }

        private static void ApplyGiQualityOverride()
        {
            if (string.IsNullOrEmpty(giQualityOverride))
            {
                return;
            }
#if BASIS_HAS_GI && !UNITY_ANDROID
            if (Enum.TryParse(giQualityOverride, true, out BasisGlobalIlluminationQuality quality))
            {
                BasisGlobalIlluminationSettings.Current.quality = quality;
                Debug.Log($"[BasisRenderCaptureAutomation] GI quality overridden to {quality}.");
            }
            else
            {
                Debug.LogWarning($"[BasisRenderCaptureAutomation] Could not parse GI quality '{giQualityOverride}'.");
            }
#else
            Debug.LogWarning("[BasisRenderCaptureAutomation] GI quality override requested but BASIS_HAS_GI is not defined on this build.");
#endif
        }

        private static void ApplyRtaoQualityOverride()
        {
            if (string.IsNullOrEmpty(rtaoQualityOverride))
            {
                return;
            }
#if BASIS_HAS_RTAO && !UNITY_ANDROID
            if (Enum.TryParse(rtaoQualityOverride, true, out BasisRTAOQuality quality))
            {
                BasisRTAOFeature.HasQualityOverride = true;
                BasisRTAOFeature.QualityOverride = quality;
                Debug.Log($"[BasisRenderCaptureAutomation] RTAO quality overridden to {quality}.");
            }
            else
            {
                Debug.LogWarning($"[BasisRenderCaptureAutomation] Could not parse RTAO quality '{rtaoQualityOverride}'.");
            }
#else
            Debug.LogWarning("[BasisRenderCaptureAutomation] RTAO quality override requested but BASIS_HAS_RTAO is not defined on this build.");
#endif
        }

        private static string ValueOf(string arg)
        {
            int eq = arg.IndexOf('=');
            return eq < 0 || eq == arg.Length - 1 ? null : arg.Substring(eq + 1);
        }

        private static bool TryParseHostPort(string value, out string host, out ushort port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            int colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1)
            {
                return false;
            }

            host = value.Substring(0, colon);
            return ushort.TryParse(value.Substring(colon + 1), out port);
        }
    }
}
