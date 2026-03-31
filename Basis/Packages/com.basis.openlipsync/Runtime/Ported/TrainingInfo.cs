using Newtonsoft.Json;

namespace OpenLipSync.Inference
{
    public class TrainingInfo
    {
        [JsonProperty("multi_label")] public bool MultiLabel { get; set; }
    }
}
