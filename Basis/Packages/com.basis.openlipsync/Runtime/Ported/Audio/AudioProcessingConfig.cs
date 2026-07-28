using System;

namespace OpenLipSync.Inference.Audio
{
    public class AudioProcessingConfig
    {
        public int SampleRate { get; set; }
        public int HopLengthSamples { get; set; }
        public int WindowLengthSamples { get; set; }
        public int NFft { get; set; }
        public int NMels { get; set; }
        public float FMin { get; set; }
        public float FMax { get; set; }
        public float Fps { get; set; }

        public static AudioProcessingConfig FromModelConfig(ModelConfig modelConfig)
        {
            var audio = modelConfig.Audio;
            if (audio == null)
                throw new ArgumentException("Model config missing audio section");

            // Prefer the sample-exact geometry the model was trained with. Deriving it from
            // integer milliseconds instead is lossy, and the runtime mel front-end has to
            // agree with the training front-end sample for sample.
            int hop = audio.HopLengthSamples > 0
                ? audio.HopLengthSamples
                : (audio.HopLengthMs > 0 ? audio.SampleRate * audio.HopLengthMs / 1000
                                         : audio.SampleRate / 100);

            int win = audio.WindowLengthSamples > 0
                ? audio.WindowLengthSamples
                : (audio.WindowLengthMs > 0 ? audio.SampleRate * audio.WindowLengthMs / 1000
                                            : (int)(audio.SampleRate * 0.025f));

            return new AudioProcessingConfig
            {
                SampleRate = audio.SampleRate,
                HopLengthSamples = hop,
                WindowLengthSamples = win,
                NFft = audio.NFft,
                NMels = audio.NMels,
                FMin = audio.Fmin,
                FMax = audio.Fmax,
                Fps = audio.Fps > 0f ? audio.Fps : (float)audio.SampleRate / hop
            };
        }
    }
}
