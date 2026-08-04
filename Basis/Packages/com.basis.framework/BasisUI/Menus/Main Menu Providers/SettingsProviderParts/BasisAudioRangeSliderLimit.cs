using UnityEngine;

namespace Basis.BasisUI
{
    /// <summary>
    /// Pins the local Microphone/Hearing Range slider's upper bound to the ceiling the server
    /// pushes. Settings pages are built once per menu open and read the ceiling as they build, so
    /// an admin raising or lowering the limit would otherwise leave every panel that is already up
    /// showing the maximum it was built with — including the admin's own, since the server echoes
    /// the change back to the client that sent it.
    /// <para>
    /// Lives on the slider's own GameObject so it dies with it, and re-reads on enable as well as
    /// on the event, which covers a section that was collapsed (and so disabled) while the limit
    /// moved. The effective range is capped separately by
    /// <see cref="SMModuleDistanceBasedReductions.SetMicrophoneRangeCapMeters"/> /
    /// <see cref="SMModuleDistanceBasedReductions.SetHearingRangeCapMeters"/>; this is only the UI
    /// catching up with it.
    /// </para>
    /// </summary>
    public class BasisAudioRangeSliderLimit : MonoBehaviour
    {
        public enum RangeKind
        {
            Microphone,
            Hearing,
        }

        private PanelSlider _slider;
        private RangeKind _kind;

        /// <summary>
        /// Attaches the limiter to a freshly built slider and applies the current ceiling.
        /// </summary>
        public static void Attach(PanelSlider slider, RangeKind kind)
        {
            if (slider == null) return;

            BasisAudioRangeSliderLimit limit = slider.gameObject.AddComponent<BasisAudioRangeSliderLimit>();
            limit._slider = slider;
            limit._kind = kind;
            limit.Apply();
        }

        private void OnEnable()
        {
            BasisNetworkModeration.OnAudioRangeLimitsChanged += OnAudioRangeLimitsChanged;
            Apply();
        }

        private void OnDisable()
        {
            BasisNetworkModeration.OnAudioRangeLimitsChanged -= OnAudioRangeLimitsChanged;
        }

        private void OnAudioRangeLimitsChanged(float microphoneMeters, float hearingMeters)
        {
            if (_slider == null) return;
            _slider.SetSliderMax(_kind == RangeKind.Microphone ? microphoneMeters : hearingMeters);
        }

        // AddComponent runs OnEnable before Attach can hand over the slider, so the first Apply
        // there is a no-op and Attach repeats it once the field is set.
        private void Apply()
        {
            if (_slider == null) return;
            _slider.SetSliderMax(_kind == RangeKind.Microphone
                ? BasisNetworkModeration.ServerMaxMicrophoneRangeMeters
                : BasisNetworkModeration.ServerMaxHearingRangeMeters);
        }
    }
}
