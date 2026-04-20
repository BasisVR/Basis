using Newtonsoft.Json;

namespace OpenLipSync.Inference
{
    public class ModelInfo
    {
        [JsonProperty("num_visemes")] public int NumVisemes { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }
}
