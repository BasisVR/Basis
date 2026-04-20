using System;
using Unity.Collections;
using Unity.Mathematics;

namespace uLipSync
{
    public struct BasisLipSyncWorkspace : IDisposable
    {
        public NativeArray<float> buffer;
        public NativeArray<float> down;
        public NativeArray<float> frame; 
        public NativeArray<float> powerHalf;
        public NativeArray<float> melSpectrum;

        public NativeArray<float> tmp; 
        public NativeArray<float> fftRe;
        public NativeArray<float> fftIm;
        public NativeArray<float> mfccZ;
        public BasisFftPlan fftPlan;

        public bool IsCreated =>
            buffer.IsCreated && down.IsCreated && frame.IsCreated &&
            powerHalf.IsCreated && melSpectrum.IsCreated &&
            tmp.IsCreated && fftRe.IsCreated && fftIm.IsCreated &&
            mfccZ.IsCreated && fftPlan.IsCreated;

        public void Dispose()
        {
            if (buffer.IsCreated) buffer.Dispose();
            if (down.IsCreated) down.Dispose();
            if (frame.IsCreated) frame.Dispose();
            if (powerHalf.IsCreated) powerHalf.Dispose();
            if (melSpectrum.IsCreated) melSpectrum.Dispose();
            if (tmp.IsCreated) tmp.Dispose();
            if (fftRe.IsCreated) fftRe.Dispose();
            if (fftIm.IsCreated) fftIm.Dispose();
            if (mfccZ.IsCreated) mfccZ.Dispose();
            if (fftPlan.IsCreated) fftPlan.Dispose();
        }

        public static int ComputeDownsampleLength(int inputLen, int outputSampleRate, int targetSampleRate)
        {
            if (outputSampleRate <= targetSampleRate) return inputLen;

            if (outputSampleRate % targetSampleRate == 0)
            {
                int skip = outputSampleRate / targetSampleRate;
                return inputLen / skip;
            }

            float df = (float)outputSampleRate / targetSampleRate;
            return (int)math.round(inputLen / df);
        }

        static int NextPow2(int x)
        {
            x = math.max(1, x);
            x--;
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            return x + 1;
        }

        public static BasisLipSyncWorkspace Create(
            int inputLen,
            int outputSampleRate,
            int targetSampleRate,
            int melDiv,
            int mfccLen,
            int fftN,
            Allocator allocator)
        {
            int downLen = ComputeDownsampleLength(inputLen, outputSampleRate, targetSampleRate);

            if (fftN <= 0) fftN = NextPow2(downLen);
            if ((fftN & (fftN - 1)) != 0) fftN = NextPow2(fftN);
            fftN = math.max(fftN, downLen);
            int specLen = fftN / 2 + 1;

            var ws = new BasisLipSyncWorkspace
            {
                buffer = new NativeArray<float>(inputLen, allocator),
                down = new NativeArray<float>(downLen, allocator),
                frame = new NativeArray<float>(fftN, allocator),
                powerHalf = new NativeArray<float>(specLen, allocator),
                melSpectrum = new NativeArray<float>(melDiv, allocator),

                tmp = new NativeArray<float>(math.max(math.max(inputLen, downLen), fftN), allocator),
                fftRe = new NativeArray<float>(fftN, allocator),
                fftIm = new NativeArray<float>(fftN, allocator),

                mfccZ = new NativeArray<float>(mfccLen, allocator),

                fftPlan = BasisFftPlan.Build(fftN, allocator),
            };
            return ws;
        }
    }
}
