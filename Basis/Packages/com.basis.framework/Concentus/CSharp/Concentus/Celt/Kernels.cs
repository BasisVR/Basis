/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2011 Xiph.Org Foundation
   Originally written by Jean-Marc Valin, Gregory Maxwell, Koen Vos,
   Timothy B. Terriberry, and the Opus open-source contributors
   Ported to C# by Logan Stromberg

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

namespace Concentus.Celt
{
    using Concentus.Celt.Enums;
    using Concentus.Celt.Structs;
    using Concentus.Common;
    using Concentus.Common.CPlusPlus;
    using System;
    using System.Diagnostics;
    using System.Numerics;

    internal static class Kernels
    {
        internal static void celt_fir(
             Span<short> x,
             Span<short> num,
             Span<short> y,
             int N,
             int ord,
             Span<short> mem
             )
        {
            int i, j;
            short[] rnum = new short[ord];
            short[] local_x = new short[N + ord];

            for (i = 0; i < ord; i++)
            {
                rnum[i] = num[ord - i - 1];
            }

            for (i = 0; i < ord; i++)
            {
                local_x[i] = mem[ord - i - 1];
            }

            for (i = 0; i < N; i++)
            {
                local_x[i + ord] = x[i];
            }

            for (i = 0; i < ord; i++)
            {
                mem[i] = x[N - i - 1];
            }

            // not worth trying to do vectorized xcorr kernel because the FIR order is usually very small (10 or so)
            for (i = 0; i < N - 3; i += 4)
            {
                int sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
                xcorr_kernel(rnum, 0, local_x, i, ref sum0, ref sum1, ref sum2, ref sum3, ord);
                y[i] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i]), Inlines.PSHR32(sum0, CeltConstants.SIG_SHIFT))));
                y[i + 1] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i + 1]), Inlines.PSHR32(sum1, CeltConstants.SIG_SHIFT))));
                y[i + 2] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i + 2]), Inlines.PSHR32(sum2, CeltConstants.SIG_SHIFT))));
                y[i + 3] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i + 3]), Inlines.PSHR32(sum3, CeltConstants.SIG_SHIFT))));
            }

            for (; i < N; i++)
            {
                int sum = 0;

                for (j = 0; j < ord; j++)
                {
                    sum = Inlines.MAC16_16(sum, rnum[j], local_x[i + j]);
                }

                y[i] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i]), Inlines.PSHR32(sum, CeltConstants.SIG_SHIFT))));
            }
        }

        internal static void celt_fir(
             Span<int> x,
             Span<int> num,
             Span<int> y,
             int N,
             int ord,
             Span<int> mem)
        {
            int i, j;
            int[] rnum = new int[ord];
            int[] local_x = new int[N + ord];

            for (i = 0; i < ord; i++)
            {
                rnum[i] = num[ord - i - 1];
            }

            for (i = 0; i < ord; i++)
            {
                local_x[i] = mem[ord - i - 1];
            }

            for (i = 0; i < N; i++)
            {
                local_x[i + ord] = x[i];
            }

            for (i = 0; i < ord; i++)
            {
                mem[i] = x[N - i - 1];
            }

            for (i = 0; i < N - 3; i += 4)
            {
                int sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
                xcorr_kernel(rnum, 0, local_x, i, ref sum0, ref sum1, ref sum2, ref sum3, ord);
                y[i] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i]), Inlines.PSHR32(sum0, CeltConstants.SIG_SHIFT))));
                y[i + 1] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i + 1]), Inlines.PSHR32(sum1, CeltConstants.SIG_SHIFT))));
                y[i + 2] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i + 2]), Inlines.PSHR32(sum2, CeltConstants.SIG_SHIFT))));
                y[i + 3] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i + 3]), Inlines.PSHR32(sum3, CeltConstants.SIG_SHIFT))));
            }

            for (; i < N; i++)
            {
                int sum = 0;

                for (j = 0; j < ord; j++)
                {
                    sum = Inlines.MAC16_16(sum, rnum[j], local_x[i + j]);
                }

                y[i] = Inlines.SATURATE16((Inlines.ADD32(Inlines.EXTEND32(x[i]), Inlines.PSHR32(sum, CeltConstants.SIG_SHIFT))));
            }
        }

        /// <summary>
        /// OPT: This is the kernel you really want to optimize. It gets used a lot by the prefilter and by the PLC.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="sum0"></param>
        /// <param name="len"></param>
        internal static void xcorr_kernel(short[] x, int x_idx, short[] y, int y_idx, ref int sum0, ref int sum1, ref int sum2, ref int sum3, int len)
        {
            // I tried vectorizing this but the intermediate int16 * int16 multiply would overflow, so
            // I would have to widen each vector to int32 beforehand and that costs any potential speed benefit.
            int j;
            short y_0, y_1, y_2, y_3;
            int x_ptr = x_idx;
            int y_ptr = y_idx;
            Inlines.OpusAssert(len >= 3);
            y_3 = 0; /* gcc doesn't realize that y_3 can't be used uninitialized */
            y_0 = y[y_ptr++];
            y_1 = y[y_ptr++];
            y_2 = y[y_ptr++];
            for (j = 0; j < len - 3; j += 4)
            {
                short tmp;
                tmp = x[x_ptr++];
                y_3 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_0);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_1);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_2);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_3);
                tmp = x[x_ptr++];
                y_0 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_1);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_2);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_3);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_0);
                tmp = x[x_ptr++];
                y_1 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_2);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_3);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_0);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_1);
                tmp = x[x_ptr++];
                y_2 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_3);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_0);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_1);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_2);
            }
            if (j++ < len)
            {
                short tmp;
                tmp = x[x_ptr++];
                y_3 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_0);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_1);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_2);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_3);
            }
            if (j++ < len)
            {
                short tmp;
                tmp = x[x_ptr++];
                y_0 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_1);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_2);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_3);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_0);
            }
            if (j < len)
            {
                short tmp;
                tmp = x[x_ptr++];
                y_1 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_2);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_3);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_0);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_1);
            }
        }

        internal static void xcorr_kernel(int[] x, int x_idx, int[] y, int y_idx, ref int sum0, ref int sum1, ref int sum2, ref int sum3, int len)
        {
            int j;
            int y_0, y_1, y_2, y_3;
            int x_ptr = x_idx;
            int y_ptr = y_idx;
            Inlines.OpusAssert(len >= 3);
            y_3 = 0; /* gcc doesn't realize that y_3 can't be used uninitialized */
            y_0 = y[y_ptr++];
            y_1 = y[y_ptr++];
            y_2 = y[y_ptr++];
            for (j = 0; j < len - 3; j += 4)
            {
                int tmp;
                tmp = x[x_ptr++];
                y_3 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_0);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_1);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_2);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_3);
                tmp = x[x_ptr++];
                y_0 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_1);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_2);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_3);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_0);
                tmp = x[x_ptr++];
                y_1 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_2);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_3);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_0);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_1);
                tmp = x[x_ptr++];
                y_2 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_3);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_0);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_1);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_2);
            }
            if (j++ < len)
            {
                int tmp;
                tmp = x[x_ptr++];
                y_3 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_0);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_1);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_2);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_3);
            }
            if (j++ < len)
            {
                int tmp;
                tmp = x[x_ptr++];
                y_0 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_1);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_2);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_3);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_0);
            }
            if (j < len)
            {
                int tmp;
                tmp = x[x_ptr++];
                y_1 = y[y_ptr++];
                sum0 = Inlines.MAC16_16(sum0, tmp, y_2);
                sum1 = Inlines.MAC16_16(sum1, tmp, y_3);
                sum2 = Inlines.MAC16_16(sum2, tmp, y_0);
                sum3 = Inlines.MAC16_16(sum3, tmp, y_1);
            }
        }

        internal static void xcorr_kernel_vector(int[] x, int x_idx, int[] y, int y_idx, ref int sum0, ref int sum1, ref int sum2, ref int sum3, int len)
        {
            int idx = 0;
            int vectorEnd = len - 4 - ((len - 4) % Vector<int>.Count);

            while (idx < vectorEnd)
            {
                Vector<int> xVec = new Vector<int>(x, x_idx + idx);
                sum0 += Vector.Dot(Vector<int>.One, Vector.Multiply(xVec, new Vector<int>(y, y_idx + idx)));
                sum1 += Vector.Dot(Vector<int>.One, Vector.Multiply(xVec, new Vector<int>(y, y_idx + idx + 1)));
                sum2 += Vector.Dot(Vector<int>.One, Vector.Multiply(xVec, new Vector<int>(y, y_idx + idx + 2)));
                sum3 += Vector.Dot(Vector<int>.One, Vector.Multiply(xVec, new Vector<int>(y, y_idx + idx + 3)));
                idx += Vector<int>.Count;
            }

            // FIXME this doesn't account for offset between the 4-block sum and the width of the vector
            // If the vector<int> width isn't divisible by 4 (for some reason...) the sums will be off
            while (idx < len)
            {
                int tmp = x[x_idx + idx];
                sum0 = Inlines.MAC16_16(sum0, tmp, y[y_idx + idx]);
                sum1 = Inlines.MAC16_16(sum1, tmp, y[y_idx + idx + 1]);
                sum2 = Inlines.MAC16_16(sum2, tmp, y[y_idx + idx + 2]);
                sum3 = Inlines.MAC16_16(sum3, tmp, y[y_idx + idx + 3]);
                idx++;
            }
        }

        internal static int celt_inner_prod(Span<short> x, Span<short> y, int N)
        {
            int i;
            int xy = 0;
            for (i = 0; i < N; i++)
                xy = Inlines.MAC16_16(xy, x[i], y[i]);
            return xy;
        }

        internal static int celt_inner_prod(Span<int> x, Span<int> y, int N)
        {
            int i;
            int xy = 0;
            for (i = 0; i < N; i++)
                xy = Inlines.MAC16_16(xy, x[i], y[i]);
            return xy;
        }
        
        internal static void dual_inner_prod(Span<int> x, Span<int> y01, Span<int> y02, int N, out int xy1, out int xy2)
        {
            int i;
            int xy01 = 0;
            int xy02 = 0;
            for (i = 0; i < N; i++)
            {
                xy01 = Inlines.MAC16_16(xy01, x[i], y01[i]);
                xy02 = Inlines.MAC16_16(xy02, x[i], y02[i]);
            }
            xy1 = xy01;
            xy2 = xy02;
        }
    }
}