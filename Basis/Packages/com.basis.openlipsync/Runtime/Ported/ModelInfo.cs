using Newtonsoft.Json;

namespace OpenLipSync.Inference
{
    public class ModelInfo
    {
        [JsonProperty("num_visemes")] public int NumVisemes { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("viseme_names")] public string[] VisemeNames { get; set; }

        /// <summary>Independent per-viseme weights (sigmoid) rather than a softmax across visemes.
        /// Multi-label lets mouth shapes overlap, which is what coarticulation actually looks like.</summary>
        [JsonProperty("multi_label")] public bool? MultiLabel { get; set; }

        /// <summary>Frames of audio the model may see beyond the frame it predicts. Implemented as a
        /// label shift so the network stays causal; costs this many frames of latency (2 = 20 ms).</summary>
        [JsonProperty("lookahead_frames")] public int LookaheadFrames { get; set; }

        /// <summary>"baked_into_graph" means the ONNX applies CMVN itself, so C# must feed RAW dB mel.
        /// The previous model expected per-utterance normalized mel that C# never applied, which cost
        /// 12.7 points of frame accuracy.</summary>
        [JsonProperty("normalization")] public string Normalization { get; set; }
    }
}
