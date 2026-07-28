using Newtonsoft.Json;

namespace OpenLipSync.Inference
{
    public class AudioInfo
    {
        [JsonProperty("sample_rate")] public int SampleRate { get; set; }
        [JsonProperty("n_mels")] public int NMels { get; set; }
        [JsonProperty("fmin")] public float Fmin { get; set; }
        [JsonProperty("fmax")] public float Fmax { get; set; }
        [JsonProperty("n_fft")] public int NFft { get; set; }
        [JsonProperty("fps")] public float Fps { get; set; }

        // Sample-exact frame geometry. Prefer these over the *_ms fields: the runtime mel
        // front-end must match the training front-end sample for sample, and routing the
        // geometry through integer milliseconds invites off-by-one rounding.
        [JsonProperty("window_length_samples")] public int WindowLengthSamples { get; set; }
        [JsonProperty("hop_length_samples")] public int HopLengthSamples { get; set; }

        [JsonProperty("window_length_ms")] public int WindowLengthMs { get; set; }
        [JsonProperty("hop_length_ms")] public int HopLengthMs { get; set; }

        /// <summary>Must be "none" (or absent) for current models: the ONNX graph normalizes
        /// internally, so C# feeds RAW dB mel. If a model ever ships expecting pre-normalized
        /// input, the C# front-end has to change deliberately -- the two sides silently
        /// disagreeing here is precisely the bug that cost the previous model 12.7 points of
        /// frame accuracy.</summary>
        [JsonProperty("normalization")] public string Normalization { get; set; }
    }
}
