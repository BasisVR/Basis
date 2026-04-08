using Newtonsoft.Json;

namespace OpenLipSync.Inference
{
    public class AudioInfo
    {
        [JsonProperty("sample_rate")] public int SampleRate { get; set; }
        [JsonProperty("hop_length_ms")] public int HopLengthMs { get; set; }
        [JsonProperty("window_length_ms")] public int WindowLengthMs { get; set; }
        [JsonProperty("n_mels")] public int NMels { get; set; }
        [JsonProperty("fmin")] public float Fmin { get; set; }
        [JsonProperty("fmax")] public float Fmax { get; set; }
        [JsonProperty("n_fft")] public int NFft { get; set; }
        [JsonProperty("normalization")] public string Normalization { get; set; }
        [JsonProperty("fps")] public float Fps { get; set; }
    }
}
