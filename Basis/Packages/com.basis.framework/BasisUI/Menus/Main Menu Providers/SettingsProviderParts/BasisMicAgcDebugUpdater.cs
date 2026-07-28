#if !BASIS_DISABLE_MICROPHONE
using Basis.BasisUI;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    /// <summary>
    /// Ticks the live AGC readout under the microphone AGC group: what gain the automatic
    /// gain control is currently holding, the speech level it settled on, and the noise
    /// floor the gate is working against. Without this the only way to tell whether the
    /// AGC is doing anything is to read the code.
    /// </summary>
    public class BasisMicAgcDebugUpdater : MonoBehaviour
    {
        public PanelElementDescriptor Field;

        private const float UpdateInterval = 0.2f;
        private float _timer;
        private string _last;
        private bool _richTextDisabled;

        private void Update()
        {
            if (Field == null) return;

            _timer += Time.unscaledDeltaTime;
            if (_timer < UpdateInterval) return;
            _timer = 0f;

            if (!_richTextDisabled)
            {
                _richTextDisabled = true;
                Field.DisableRichText();
            }

            var s = SMDMicrophone.Current;
            string text;

            if (!s.UseAGC)
            {
                text = BasisLocalization.Get("settings.microphone.agc.debug.off");
            }
            else if (!BasisLocalMicrophoneDriver.AgcHasSpeech)
            {
                text = BasisLocalization.Get("settings.microphone.agc.debug.listening");
            }
            else
            {
                float gain = BasisLocalMicrophoneDriver.AgcGainDb;
                float level = BasisLocalMicrophoneDriver.AgcSpeechLevel;
                float floor = BasisLocalMicrophoneDriver.GateNoiseFloor;

                text = $"Gain {gain:+0.0;-0.0;0.0} dB (max +{s.AgcMaxGainDb:F0} / -{BasisMicrophoneAgc.MaxCutDb:F0})\n" +
                       $"Speech {level:F4} -> target {BasisMicrophoneAgc.DefaultTargetRms:F3}\n" +
                       $"Noise floor {floor:F4}";

                if (s.UseNoiseGate)
                {
                    text += $"\nGate threshold {BasisLocalMicrophoneDriver.GateThreshold:F4}" +
                            (s.AutoNoiseGate ? " (auto)" : " (manual)");
                }
            }

            if (text == _last) return;
            _last = text;
            Field.SetDescription(text);
        }
    }
}

#endif
